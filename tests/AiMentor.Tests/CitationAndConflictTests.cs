using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class CitationAndConflictTests
{
    private static readonly Evidence Evidence = new(new KnowledgeChunk("chunk-1", "DOC-1", "1.0", "制度",
        "令牌", "Access Token 默认有效期为 30 分钟。", "tenant", new HashSet<string>(["group"]),
        "doc.md#L10-L10"), 0.9);

    [Fact]
    public void MapperShouldBindClaimToExactSentenceAndProvenance()
    {
        var citations = new RuleBasedCitationMapper().Map("根据当前证据：Access Token 默认有效期为 30 分钟。", [Evidence]);

        var citation = Assert.Single(citations);
        Assert.Equal("chunk-1", citation.ChunkId);
        Assert.Equal("doc.md#L10-L10", citation.SourceAnchor);
        Assert.Equal(0, citation.SentenceIndex);
        Assert.Equal("Access Token 默认有效期为 30 分钟。", citation.ClaimText);
        Assert.Equal(Evidence.Chunk.Content, citation.Quote);
    }

    [Theory]
    [InlineData("wrong", "doc.md#L10-L10", 0, "Access Token 默认有效期为 30 分钟。", "CITATION_CHUNK_MISMATCH")]
    [InlineData("chunk-1", "doc.md#L10-L10", 3, "Access Token 默认有效期为 30 分钟。", "CITATION_SENTENCE_INDEX_INVALID")]
    [InlineData("chunk-1", "doc.md#L10-L10", 0, "Refresh Token 永久有效。", "CITATION_CLAIM_MISMATCH")]
    public void VerifierShouldFailClosedForInvalidChunkIndexOrUngroundedClaim(string chunkId, string anchor,
        int sentenceIndex, string claim, string expectedCode)
    {
        var citation = new Citation("DOC-1", "1.0", "制度", "令牌", Evidence.Chunk.Content, 0.9,
            chunkId, anchor, sentenceIndex, claim);

        var result = new RuleBasedCitationVerifier().Verify(
            "根据当前证据：Access Token 默认有效期为 30 分钟。", [Evidence], [citation]);

        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public async Task MultipleActiveVersionsShouldRequireExpertReviewBeforeComposition()
    {
        var evidence = new[]
        {
            Evidence,
            new Evidence(Evidence.Chunk with { Version = "2.0", Content = "Access Token 默认有效期为 60 分钟。" }, 0.88)
        };
        var service = new TrustedQuestionService(new StaticRepository(evidence), new RuleBasedQueryNormalizer(),
            new RuleBasedInputSafetyService(), new RuleBasedRetrievedContentSafetyService(), new LexicalEvidenceReranker(),
            new RuleBasedEvidenceSufficiencyEvaluator(), new ThrowingComposer(), new RuleBasedOutputSafetyService(),
            new InMemoryTraceSink(), new TrustedQuestionOptions(), new EmptyMemoryContextProvider(),
            new RuleBasedCitationMapper(), new RuleBasedCitationVerifier(), new RuleBasedEvidenceConflictDetector());

        var answer = await service.AskAsync(new TrustedQuestion("Access Token 默认有效多久？",
            AccessContext.Create("tenant", "user", ["group"])));

        Assert.Equal(AnswerDecision.Refused, answer.Decision);
        Assert.Equal("EVIDENCE_CONFLICT_REQUIRES_EXPERT", answer.Safety.Code);
        Assert.Contains("领域专家", answer.Answer, StringComparison.Ordinal);
        Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<EvidenceConflict>>(answer.Conflicts),
            conflict => conflict.Kind == "MULTIPLE_ACTIVE_VERSIONS");
        Assert.Contains(answer.Trace, step => step.Name == "evidence.conflict"
            && step.Outcome == "expert_review_required");
    }

    [Fact]
    public void SameAuthorityNumericConflictShouldBeDisclosed()
    {
        var other = new Evidence(Evidence.Chunk with
        {
            Id = "chunk-2", DocumentId = "DOC-2", Content = "Access Token 默认有效期为 60 分钟。"
        }, 0.8);

        var conflicts = new RuleBasedEvidenceConflictDetector().Detect([Evidence, other]);

        Assert.Contains(conflicts, conflict => conflict.Kind == "SAME_AUTHORITY_CONFLICT"
            && conflict.RequiresExpertReview);
    }

    private sealed class StaticRepository(IReadOnlyList<Evidence> evidence) : IKnowledgeRepository
    {
        public KnowledgeStatistics Statistics => new(1, evidence.Count);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Evidence>> SearchAsync(string query, AccessContext access, int limit,
            CancellationToken cancellationToken = default) => Task.FromResult(evidence);
    }

    private sealed class ThrowingComposer : IAnswerComposer
    {
        public Task<string> ComposeAsync(string question, IReadOnlyList<Evidence> evidence,
            IReadOnlyList<MemoryContextItem> memories, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("冲突必须在生成前阻断。");
    }
}
