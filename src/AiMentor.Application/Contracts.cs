using AiMentor.Domain;

namespace AiMentor.Application;

public interface IKnowledgeRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Evidence>> SearchAsync(string query, AccessContext access, int limit, CancellationToken cancellationToken = default);
    KnowledgeStatistics Statistics { get; }
}

public interface IQueryNormalizer
{
    string Normalize(string question);
}

public interface IKnowledgeChunkSource
{
    Task<IReadOnlyList<KnowledgeChunk>> ReadAllChunksAsync(CancellationToken cancellationToken = default);
}

public interface ITextEmbeddingGenerator
{
    int Dimensions { get; }
    Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record KnowledgeStatistics(int Documents, int Chunks);

public interface IInputSafetyService
{
    string PolicyVersion { get; }
    SafetyDecision Review(string input);
}

public interface IRetrievedContentSafetyService
{
    string PolicyVersion { get; }
    RetrievedContentReview Review(IReadOnlyList<Evidence> evidence);
}

public sealed record RetrievedContentRejection(string ChunkId, string Code);

public sealed record RetrievedContentReview(
    IReadOnlyList<Evidence> AcceptedEvidence,
    IReadOnlyList<RetrievedContentRejection> Rejections);

public interface IToolInvocationSafetyService
{
    string PolicyVersion { get; }
    SafetyDecision Review(ToolInvocationRequest request, AccessContext access);
}

public sealed record ToolInvocationRequest(
    string ToolName,
    ToolOperationRisk Risk,
    IReadOnlyDictionary<string, object?> Arguments);

public interface IOutputSafetyService
{
    string PolicyVersion { get; }
    SafetyDecision Review(string answer, IReadOnlyList<Evidence> evidence, IReadOnlyList<Citation> citations);
}

public interface IAnswerComposer
{
    Task<string> ComposeAsync(string question, IReadOnlyList<Evidence> evidence, CancellationToken cancellationToken = default);
}

public interface IEvidenceReranker
{
    Task<IReadOnlyList<Evidence>> RerankAsync(string question, IReadOnlyList<Evidence> evidence,
        CancellationToken cancellationToken = default);
}

public interface IEvidenceSufficiencyEvaluator
{
    EvidenceAssessment Evaluate(string question, IReadOnlyList<Evidence> evidence, double minimumScore);
}

public sealed record EvidenceAssessment(bool IsSufficient, double Confidence, string Code, string Explanation);

public interface ITraceSink
{
    Task WriteAsync(string runId, IReadOnlyList<TraceStep> trace, CancellationToken cancellationToken = default);
}

public interface ITrustedQuestionService
{
    Task<TrustedAnswer> AskAsync(TrustedQuestion question, CancellationToken cancellationToken = default);
}

public sealed class TrustedQuestionOptions
{
    public int SearchLimit { get; init; } = 5;
    public double MinimumTopScore { get; init; } = 0.25;
    public int MinimumEvidenceCount { get; init; } = 1;
}
