namespace AiMentor.Domain;

public sealed record AccessContext(string TenantId, string SubjectId, IReadOnlySet<string> Groups)
{
    public static AccessContext Create(string tenantId, string subjectId, IEnumerable<string>? groups) =>
        new(tenantId.Trim(), subjectId.Trim(), new HashSet<string>(groups ?? [], StringComparer.OrdinalIgnoreCase));
}

public enum SafetyAction { Allow, Transform, Refuse, RequireApproval }
public enum AnswerDecision { Answered, Refused, InsufficientEvidence, Failed }

public sealed record SafetyDecision(SafetyAction Action, string Code, string Message)
{
    public static SafetyDecision Allowed { get; } = new(SafetyAction.Allow, "SAFE", "输入通过安全审核。");
}

public sealed record KnowledgeDocument(
    string Id,
    string Version,
    string Title,
    string TenantId,
    IReadOnlySet<string> AllowedGroups,
    string Status,
    string SourcePath,
    IReadOnlyList<KnowledgeChunk> Chunks);

public sealed record KnowledgeChunk(
    string Id,
    string DocumentId,
    string Version,
    string Title,
    string Section,
    string Content,
    string TenantId,
    IReadOnlySet<string> AllowedGroups,
    string SourcePath);

public sealed record Evidence(KnowledgeChunk Chunk, double Score, double? RetrievalScore = null);

public sealed record Citation(
    string DocumentId,
    string Version,
    string Title,
    string Section,
    string Quote,
    double Score);

public sealed record TrustedQuestion(string Question, AccessContext Access, string? CorrelationId = null);

public sealed record TraceStep(string Name, string Outcome, DateTimeOffset Timestamp, IReadOnlyDictionary<string, object?> Details);

public sealed record TrustedAnswer(
    string RunId,
    AnswerDecision Decision,
    string Answer,
    bool EvidenceSufficient,
    SafetyDecision Safety,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<TraceStep> Trace);
