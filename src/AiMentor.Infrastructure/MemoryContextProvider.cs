using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>为离线评测提供不读取用户记忆的显式空实现。</summary>
public sealed class EmptyMemoryContextProvider : IMemoryContextProvider
{
    public Task<IReadOnlyList<MemoryContextItem>> GetRelevantAsync(string question, AccessContext access,
        string? sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MemoryContextItem>>([]);
}

/// <summary>按租户、用户、会话和相关性选择已批准记忆，并在注入前再次执行内容审核。</summary>
public sealed partial class SafeMemoryContextProvider(
    IMemoryStore store,
    IMemoryContentSafetyService safety,
    MemoryContextOptions options,
    TimeProvider timeProvider) : IMemoryContextProvider
{
    public async Task<IReadOnlyList<MemoryContextItem>> GetRelevantAsync(string question, AccessContext access,
        string? sessionId, CancellationToken cancellationToken = default)
    {
        if (options.MaximumItems <= 0 || options.MaximumTotalCharacters <= 0) return [];
        var active = await store.ListActiveAsync(access, null, null, timeProvider.GetUtcNow(), cancellationToken);
        var questionTokens = Tokenize(question);
        var candidates = active
            .Where(memory => memory.Scope != MemoryScope.Session
                || (!string.IsNullOrWhiteSpace(sessionId)
                    && string.Equals(memory.SessionId, sessionId.Trim(), StringComparison.Ordinal)))
            .Select(memory => new { Memory = memory, Score = Score(memory, questionTokens) })
            .Where(item => item.Memory.Scope == MemoryScope.UserPreference || item.Score > 0)
            .Where(item => safety.Review(item.Memory.Key, item.Memory.Value).Action == SafetyAction.Allow)
            .OrderByDescending(item => item.Memory.Scope == MemoryScope.Session)
            .ThenByDescending(item => item.Score)
            .ThenByDescending(item => item.Memory.UpdatedAt)
            .Take(options.MaximumItems)
            .ToArray();

        var result = new List<MemoryContextItem>(candidates.Length);
        var characters = 0;
        foreach (var candidate in candidates)
        {
            var itemCharacters = candidate.Memory.Key.Length + candidate.Memory.Value.Length;
            if (characters + itemCharacters > options.MaximumTotalCharacters) continue;
            characters += itemCharacters;
            result.Add(new MemoryContextItem(candidate.Memory.Scope, candidate.Memory.Key, candidate.Memory.Value,
                candidate.Memory.UpdatedAt));
        }
        return result;
    }

    private static int Score(MemoryRecord memory, IReadOnlySet<string> questionTokens)
    {
        var keyTokens = Tokenize(memory.Key);
        var valueTokens = Tokenize(memory.Value);
        return keyTokens.Count(questionTokens.Contains) * 3 + valueTokens.Count(questionTokens.Contains);
    }

    private static HashSet<string> Tokenize(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in WordToken().Matches(value))
        {
            var token = match.Value.Trim().ToLowerInvariant();
            if (token.Length > 1) result.Add(token);
        }
        var chinese = ChineseText().Replace(value, string.Empty);
        for (var index = 0; index + 1 < chinese.Length; index++) result.Add(chinese.Substring(index, 2));
        return result;
    }

    [GeneratedRegex(@"[A-Za-z0-9_.-]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordToken();

    [GeneratedRegex(@"[^\p{IsCJKUnifiedIdeographs}]", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseText();
}
