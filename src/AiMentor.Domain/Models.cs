namespace AiMentor.Domain;

/// <summary>表示由服务端认证层构造、不可由请求体自报的访问身份。</summary>
public sealed record AccessContext(string TenantId, string SubjectId, IReadOnlySet<string> Groups)
{
    public static AccessContext Create(string tenantId, string subjectId, IEnumerable<string>? groups) =>
        new(tenantId.Trim(), subjectId.Trim(), new HashSet<string>(groups ?? [], StringComparer.OrdinalIgnoreCase));
}

/// <summary>安全策略可采取的标准动作。</summary>
public enum SafetyAction { Allow, Transform, Refuse, RequireApproval }
/// <summary>可信问答闭环对调用方暴露的终态。</summary>
public enum AnswerDecision { Answered, Refused, InsufficientEvidence, Failed }

/// <summary>携带稳定策略代码和可安全展示说明的审核决定。</summary>
public sealed record SafetyDecision(SafetyAction Action, string Code, string Message)
{
    public static SafetyDecision Allowed { get; } = new(SafetyAction.Allow, "SAFE", "输入通过安全审核。");
}

/// <summary>包含发布状态、租户和用户组 ACL 的知识文档。</summary>
public sealed record KnowledgeDocument(
    string Id,
    string Version,
    string Title,
    string TenantId,
    IReadOnlySet<string> AllowedGroups,
    string Status,
    string SourcePath,
    IReadOnlyList<KnowledgeChunk> Chunks);

/// <summary>保留来源和 ACL 的最小检索单元。</summary>
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

/// <summary>绑定知识分块、重排分和可选原始检索分的证据。</summary>
public sealed record Evidence(KnowledgeChunk Chunk, double Score, double? RetrievalScore = null);

/// <summary>从本次可访问证据生成、可回查原文的结构化引用。</summary>
public sealed record Citation(
    string DocumentId,
    string Version,
    string Title,
    string Section,
    string Quote,
    double Score);

/// <summary>表示已绑定服务端身份、追踪标识和可选会话的问答请求。</summary>
public sealed record TrustedQuestion(
    string Question,
    AccessContext Access,
    string? CorrelationId = null,
    string? SessionId = null);

/// <summary>记录不含敏感正文的单个审计步骤。</summary>
public sealed record TraceStep(string Name, string Outcome, DateTimeOffset Timestamp, IReadOnlyDictionary<string, object?> Details);

/// <summary>可信问答闭环的回答、引用、安全决定和运行轨迹。</summary>
public sealed record TrustedAnswer(
    string RunId,
    AnswerDecision Decision,
    string Answer,
    bool EvidenceSufficient,
    SafetyDecision Safety,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<TraceStep> Trace);
