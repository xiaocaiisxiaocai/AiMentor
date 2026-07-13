using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

public sealed partial class RuleBasedQueryNormalizer : IQueryNormalizer
{
    public string Normalize(string question)
    {
        var normalized = QuestionScaffolding().Replace(question.Trim(), " ");
        normalized = Whitespace().Replace(normalized, " ").Trim(' ', '？', '?');
        return string.IsNullOrWhiteSpace(normalized) ? question.Trim() : normalized;
    }

    [GeneratedRegex(@"请问|请告诉我|帮我查询|需要什么条件|必须有哪些|什么情况属于|有哪些|需要哪些|是什么|能否|是否|多久|多少|怎样|怎么|如何|什么", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionScaffolding();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

/// <summary>使用可解释词项覆盖率重排证据，同时保留原始检索分用于引用。</summary>
public sealed class LexicalEvidenceReranker : IEvidenceReranker
{
    public Task<IReadOnlyList<Evidence>> RerankAsync(string question, IReadOnlyList<Evidence> evidence,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var questionTokens = EvidenceTextAnalysis.QuestionTokens(question);
        if (questionTokens.Count == 0) return Task.FromResult<IReadOnlyList<Evidence>>([]);

        var reranked = evidence.Select(item =>
        {
            var contentTokens = EvidenceTextAnalysis.Tokens($"{item.Chunk.Title} {item.Chunk.Section} {item.Chunk.Content}");
            var sentenceCoverage = EvidenceTextAnalysis.BestSentenceCoverage(questionTokens, item.Chunk.Content);
            var documentCoverage = EvidenceTextAnalysis.Coverage(questionTokens, contentTokens);
            var score = 0.65 * Math.Clamp(item.Score, 0, 1) + 0.20 * documentCoverage + 0.15 * sentenceCoverage;
            return new Evidence(item.Chunk, Math.Round(score, 6), item.RetrievalScore ?? item.Score);
        })
        .OrderByDescending(item => item.Score)
        .ThenBy(item => item.Chunk.DocumentId, StringComparer.Ordinal)
        .ToArray();
        return Task.FromResult<IReadOnlyList<Evidence>>(reranked);
    }
}

public sealed partial class RuleBasedEvidenceSufficiencyEvaluator : IEvidenceSufficiencyEvaluator
{
    public EvidenceAssessment Evaluate(string question, IReadOnlyList<Evidence> evidence, double minimumScore)
    {
        if (evidence.Count == 0) return Insufficient("NO_EVIDENCE", 1, "没有可访问的候选证据。");
        if (RequiresLiveData().IsMatch(question))
            return Insufficient("LIVE_DATA_REQUIRED", 0.98, "问题要求实时或运行态数据，静态知识库不能证明答案。");
        var highestRetrievalScore = evidence.Max(item => item.RetrievalScore ?? item.Score);
        if (highestRetrievalScore < minimumScore)
            return Insufficient("LOW_RELEVANCE", 1 - highestRetrievalScore, "原始召回的最高相关性低于门禁阈值。");

        var questionTokens = EvidenceTextAnalysis.QuestionTokens(question);
        if (questionTokens.Count < 2) return Insufficient("QUESTION_UNDERSPECIFIED", 0.9, "问题缺少足够的可判定主题信息。");

        var bestSentences = evidence.Take(3)
            .SelectMany(item => EvidenceTextAnalysis.Sentences(item.Chunk.Content))
            .Select(sentence => new
            {
                Text = sentence,
                Coverage = EvidenceTextAnalysis.Coverage(questionTokens, EvidenceTextAnalysis.Tokens(sentence))
            })
            .OrderByDescending(item => item.Coverage)
            .ToArray();
        var bestCoverage = bestSentences.FirstOrDefault()?.Coverage ?? 0;
        if (bestCoverage < 0.10)
            return Insufficient("NO_ANSWER_BEARING_SENTENCE", 1 - bestCoverage, "没有单个证据句覆盖问题的核心主题。");

        if (NumericAnswerQuestion().IsMatch(question))
        {
            var numericSupport = bestSentences.Take(5).Any(item => item.Coverage >= 0.10 && ContainsAnswerValue(item.Text));
            if (!numericSupport) return Insufficient("EXPECTED_VALUE_MISSING", 0.95, "问题要求数值或时长，但高相关证据句没有对应值。");
        }

        var confidence = Math.Clamp(0.55 * evidence[0].Score + 0.45 * bestCoverage, 0, 1);
        return new EvidenceAssessment(true, Math.Round(confidence, 4), "SUFFICIENT", "存在同时满足相关性和答案承载要求的证据句。");
    }

    private static EvidenceAssessment Insufficient(string code, double confidence, string explanation) =>
        new(false, Math.Round(Math.Clamp(confidence, 0, 1), 4), code, explanation);

    private static bool ContainsAnswerValue(string sentence)
    {
        var withoutListOrdinal = MarkdownListOrdinal().Replace(sentence, string.Empty);
        return NumericAnswerSignal().IsMatch(withoutListOrdinal);
    }

    [GeneratedRegex(@"今天|此刻|实时|去年|现在谁|当前使用.{0,12}(模型|版本)|订单\s*\d+.{0,12}(客户|个人信息)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequiresLiveData();

    [GeneratedRegex(@"多少|多久|多长|大小是多少|有效期|几(分钟|小时|天|次|个)", RegexOptions.CultureInvariant)]
    private static partial Regex NumericAnswerQuestion();

    [GeneratedRegex(@"\d|[一二两三四五六七八九十百]+\s*(分钟|小时|天|工作日|次|个|%|％)", RegexOptions.CultureInvariant)]
    private static partial Regex NumericAnswerSignal();

    [GeneratedRegex(@"^\s*\d+[.)、]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownListOrdinal();
}

internal static class EvidenceTextAnalysis
{
    private static readonly string[] QuestionNoise =
    [
        "请问", "请", "告诉我", "查询", "北辰", "公司", "系统", "什么", "多少", "多久", "多长", "是否", "能否",
        "可以", "应该", "怎么", "如何", "哪个", "当前", "现在", "具体", "一下", "是什么"
    ];

    public static HashSet<string> QuestionTokens(string question)
    {
        var normalized = question.ToLowerInvariant();
        foreach (var noise in QuestionNoise) normalized = normalized.Replace(noise, " ", StringComparison.OrdinalIgnoreCase);
        return Tokens(normalized);
    }

    public static HashSet<string> Tokens(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(value.ToLowerInvariant(), @"[a-z0-9][a-z0-9._-]*|[\u4e00-\u9fff]+"))
        {
            var token = match.Value;
            if (token.Any(character => character is >= '\u4e00' and <= '\u9fff'))
            {
                if (token.Length == 1) result.Add(token);
                for (var i = 0; i < token.Length - 1; i++) result.Add(token.Substring(i, 2));
            }
            else result.Add(token);
        }
        return result;
    }

    public static double Coverage(IReadOnlySet<string> expected, IReadOnlySet<string> actual) =>
        expected.Count == 0 ? 0 : expected.Count(actual.Contains) / (double)expected.Count;

    public static double BestSentenceCoverage(IReadOnlySet<string> questionTokens, string content) =>
        Sentences(content).Select(sentence => Coverage(questionTokens, Tokens(sentence))).DefaultIfEmpty(0).Max();

    public static IEnumerable<string> Sentences(string content) => Regex
        .Split(content.Replace("\r", string.Empty, StringComparison.Ordinal), @"(?<=[。！？；])|\n+")
        .Select(sentence => sentence.Trim().TrimStart('-', '*', ' '))
        .Where(sentence => sentence.Length > 0);
}
