using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AiMentor.Infrastructure;

public sealed class AgentFrameworkAnswerComposer : IAnswerComposer
{
    private readonly ChatClientAgent _agent;

    public AgentFrameworkAnswerComposer(IChatClient chatClient)
    {
        _agent = new ChatClientAgent(chatClient,
            instructions: "你是企业可信问答助手。只能使用输入中给出的证据回答；不补充证据外事实；回答简洁、明确。引用由上层系统结构化附加，不要伪造引用。");
    }

    public async Task<string> ComposeAsync(string question, IReadOnlyList<Evidence> evidence, CancellationToken cancellationToken = default)
    {
        var prompt = new StringBuilder()
            .AppendLine("<question>").AppendLine(question).AppendLine("</question>")
            .AppendLine("<evidence>");
        foreach (var item in evidence)
        {
            prompt.Append("[SOURCE ").Append(item.Chunk.DocumentId).Append('|').Append(item.Chunk.Version).Append('|')
                .Append(item.Chunk.Section).AppendLine("]").AppendLine(item.Chunk.Content).AppendLine("[/SOURCE]");
        }
        prompt.AppendLine("</evidence>");

        var session = await _agent.CreateSessionAsync(cancellationToken);
        var response = await _agent.RunAsync(prompt.ToString(), session, cancellationToken: cancellationToken);
        return string.IsNullOrWhiteSpace(response.Text) ? "证据已找到，但未能形成有效回答。" : response.Text.Trim();
    }
}

/// <summary>
/// 无外部密钥的确定性模型沙箱。它实现生产环境同一 IChatClient 契约，便于闭环测试；上线时替换为真实模型客户端。
/// </summary>
public sealed class DeterministicGroundedChatClient : IChatClient
{
    private static readonly ChatClientMetadata Metadata = new("AiMentor.Deterministic", defaultModelId: "grounded-sandbox-v1");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prompt = messages.LastOrDefault()?.Text ?? string.Empty;
        var answer = Compose(prompt);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(Metadata) ? Metadata : null;

    public void Dispose() { }

    private static string Compose(string prompt)
    {
        var question = Regex.Match(prompt, @"<question>\s*(?<value>.*?)\s*</question>", RegexOptions.Singleline).Groups["value"].Value.Trim();
        var sources = Regex.Matches(prompt, @"\[SOURCE (?<id>[^|]+)\|(?<version>[^|]+)\|(?<section>[^\]]+)\]\s*(?<content>.*?)\s*\[/SOURCE\]", RegexOptions.Singleline);
        if (sources.Count == 0) return "证据不足，无法回答。";

        var keywords = Tokenize(question);
        var ranked = sources.Cast<Match>()
            .SelectMany(source => SplitSentences(source.Groups["content"].Value)
                .Select(sentence => new { Sentence = sentence, Score = Tokenize(sentence).Count(keywords.Contains) }))
            .Where(item => item.Sentence.Length > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Sentence.Length)
            .ToArray();
        var minimumScore = ranked.Length == 0 ? int.MaxValue : Math.Max(2, (int)Math.Ceiling(ranked[0].Score * 0.6));
        var candidates = ranked
            .Where(item => item.Score >= minimumScore)
            .Take(3)
            .Select(item => item.Sentence.TrimStart('-', '*', ' '))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return candidates.Length == 0
            ? "证据不足，无法回答。"
            : $"根据当前可访问的正式知识：{string.Join("；", candidates)}";
    }

    private static IEnumerable<string> SplitSentences(string content) =>
        Regex.Split(content.Replace("\r", string.Empty, StringComparison.Ordinal), @"(?<=[。！？；])|\n+")
            .Select(x => x.Trim()).Where(x => x.Length > 0);

    private static HashSet<string> Tokenize(string value)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(value.ToLowerInvariant(), @"[a-z0-9][a-z0-9._-]*|[\u4e00-\u9fff]+"))
        {
            var token = match.Value;
            if (token.Any(character => character is >= '\u4e00' and <= '\u9fff'))
            {
                if (token.Length == 1) tokens.Add(token);
                for (var i = 0; i < token.Length - 1; i++) tokens.Add(token.Substring(i, 2));
            }
            else tokens.Add(token);
        }
        return tokens;
    }
}
