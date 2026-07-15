using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>按回答句逐句选择词项覆盖最高的证据原句，并保留完整 provenance。</summary>
public sealed class RuleBasedCitationMapper : ICitationMapper
{
    public IReadOnlyList<Citation> Map(string answer, IReadOnlyList<Evidence> evidence)
    {
        var claims = CitationTextAnalysis.Claims(answer);
        var candidates = evidence.SelectMany(item => EvidenceTextAnalysis.Sentences(item.Chunk.Content)
            .Select(sentence => new { Evidence = item, Quote = sentence })).ToArray();
        var citations = new List<Citation>();
        for (var index = 0; index < claims.Count; index++)
        {
            var claimTokens = EvidenceTextAnalysis.Tokens(claims[index]);
            var best = candidates.Select(candidate => new
                {
                    candidate.Evidence,
                    candidate.Quote,
                    Coverage = EvidenceTextAnalysis.Coverage(claimTokens, EvidenceTextAnalysis.Tokens(candidate.Quote))
                })
                .OrderByDescending(candidate => candidate.Coverage)
                .ThenByDescending(candidate => candidate.Evidence.RetrievalScore ?? candidate.Evidence.Score)
                .FirstOrDefault();
            if (best is null || best.Coverage < 0.15) continue;
            var chunk = best.Evidence.Chunk;
            citations.Add(new Citation(chunk.DocumentId, chunk.Version, chunk.Title, chunk.Section, best.Quote,
                Math.Round(best.Evidence.RetrievalScore ?? best.Evidence.Score, 4), chunk.Id, chunk.SourcePath, index,
                claims[index]));
        }
        return citations;
    }
}

/// <summary>独立于映射器重新验证每条引用；任一 provenance 或语义关系错误都失败关闭。</summary>
public sealed class RuleBasedCitationVerifier : ICitationVerifier
{
    public CitationVerificationResult Verify(string answer, IReadOnlyList<Evidence> evidence,
        IReadOnlyList<Citation> citations)
    {
        var claims = CitationTextAnalysis.Claims(answer);
        if (claims.Count == 0 || citations.Count == 0)
            return Invalid("CITATION_MAPPING_MISSING", "事实回答缺少句子级引用。");
        if (citations.Select(citation => citation.SentenceIndex).Distinct().Count() != claims.Count)
            return Invalid("CITATION_CLAIM_COVERAGE_INCOMPLETE", "并非每个事实句都有独立引用。");

        foreach (var citation in citations)
        {
            if (citation.SentenceIndex is not int index || index < 0 || index >= claims.Count)
                return Invalid("CITATION_SENTENCE_INDEX_INVALID", "引用句索引超出回答范围。");
            if (string.IsNullOrWhiteSpace(citation.ClaimText)
                || !string.Equals(CitationTextAnalysis.Normalize(citation.ClaimText), CitationTextAnalysis.Normalize(claims[index]),
                    StringComparison.Ordinal))
                return Invalid("CITATION_CLAIM_MISMATCH", "引用 claim 与对应回答句不一致。");
            var source = evidence.FirstOrDefault(item => string.Equals(item.Chunk.Id, citation.ChunkId, StringComparison.Ordinal)
                && string.Equals(item.Chunk.DocumentId, citation.DocumentId, StringComparison.Ordinal)
                && string.Equals(item.Chunk.Version, citation.Version, StringComparison.Ordinal));
            if (source is null) return Invalid("CITATION_CHUNK_MISMATCH", "引用分块不属于本次证据集合。");
            if (!string.Equals(source.Chunk.SourcePath, citation.SourceAnchor, StringComparison.Ordinal))
                return Invalid("CITATION_ANCHOR_MISMATCH", "引用原文锚点与证据分块不一致。");
            if (string.IsNullOrWhiteSpace(citation.Quote)
                || !CitationTextAnalysis.Normalize(source.Chunk.Content).Contains(CitationTextAnalysis.Normalize(citation.Quote), StringComparison.Ordinal))
                return Invalid("CITATION_QUOTE_MISMATCH", "引用摘录无法在精确分块中回查。");
            var coverage = EvidenceTextAnalysis.Coverage(EvidenceTextAnalysis.Tokens(citation.ClaimText),
                EvidenceTextAnalysis.Tokens(citation.Quote));
            if (coverage < 0.15) return Invalid("CITATION_CLAIM_NOT_GROUNDED", "引用摘录不能支持对应 claim。");
        }
        return new CitationVerificationResult(true, "CITATION_VERIFIED", "句子级引用通过独立验证。");
    }

    private static CitationVerificationResult Invalid(string code, string message) => new(false, code, message);
}

/// <summary>检测同一文档多版本，以及同章节同权来源给出不同数值的冲突。</summary>
public sealed partial class RuleBasedEvidenceConflictDetector : IEvidenceConflictDetector
{
    public IReadOnlyList<EvidenceConflict> Detect(IReadOnlyList<Evidence> evidence)
    {
        var conflicts = new List<EvidenceConflict>();
        foreach (var versions in evidence.GroupBy(item => item.Chunk.DocumentId, StringComparer.Ordinal)
                     .Where(group => group.Select(item => item.Chunk.Version).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            conflicts.Add(new EvidenceConflict("MULTIPLE_ACTIVE_VERSIONS", [versions.Key],
                $"同一文档同时命中多个有效版本：{string.Join(", ", versions.Select(item => item.Chunk.Version).Distinct(StringComparer.Ordinal))}。"));
        }

        foreach (var group in evidence.GroupBy(item => item.Chunk.Section, StringComparer.Ordinal)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key)
                         && group.Select(item => item.Chunk.DocumentId).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            var values = group.Select(item => new
            {
                item.Chunk.DocumentId,
                Values = NumericValue().Matches(item.Chunk.Content).Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray()
            }).Where(item => item.Values.Length > 0).ToArray();
            if (values.Length > 1 && values.Select(item => string.Join('|', item.Values)).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                conflicts.Add(new EvidenceConflict("SAME_AUTHORITY_CONFLICT",
                    values.Select(item => item.DocumentId).Distinct(StringComparer.Ordinal).ToArray(),
                    $"同权来源在章节“{group.Key}”给出了不一致的事实值。"));
            }
        }
        return conflicts;
    }

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s*(?:分钟|小时|天|工作日|次|个|%|％|MB|GB)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumericValue();
}

internal static partial class CitationTextAnalysis
{
    public static IReadOnlyList<string> Claims(string answer) => Regex
        .Split(answer.Replace("\r", string.Empty, StringComparison.Ordinal), @"(?<=[。！？；])|\n+")
        .Select(value => TrustedPrefix().Replace(value.Trim(), string.Empty).Trim().TrimStart('：', ':'))
        .Where(value => value.Length > 0 && !value.Contains("证据不足", StringComparison.Ordinal))
        .ToArray();

    public static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"^(?:根据当前可访问的正式知识|根据(?:当前)?证据)\s*[:：]?\s*", RegexOptions.CultureInvariant)]
    private static partial Regex TrustedPrefix();
}
