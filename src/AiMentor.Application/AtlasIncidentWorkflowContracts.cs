using AiMentor.Domain;

namespace AiMentor.Application;

public enum AtlasIncidentStatus
{
    RequiredInputs,
    InspectingClock,
    InspectingJwks,
    InspectingRecentChanges,
    DiagnosisReady,
    AwaitingActionApproval,
    Cancelled,
    Expired
}

/// <summary>仅承载可安全持久化的 Token 元数据，禁止放入原始 Token。</summary>
public sealed record AtlasTokenMetadata(
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? NotBefore = null,
    bool? IssuerMatches = null,
    bool? AudienceMatches = null,
    bool? SignatureValid = null);

/// <summary>增量补充排查上下文；RawToken 只用于在 API 边界明确拒绝，不会进入检查点。</summary>
public sealed record AtlasIncidentInput(
    string? Region = null,
    string? Node = null,
    DateTimeOffset? ObservedAt = null,
    AtlasTokenMetadata? TokenMetadata = null,
    int? NodeUtcOffsetSeconds = null,
    bool? JwksCacheStale = null,
    bool? RecentIdentityConfigurationChange = null,
    string? ProposedAction = null,
    string? RawToken = null);

/// <summary>允许进入检查点的脱敏字段集合，类型层面排除原始 Token。</summary>
public sealed record SafeAtlasIncidentInput(
    string? Region = null,
    string? Node = null,
    DateTimeOffset? ObservedAt = null,
    AtlasTokenMetadata? TokenMetadata = null,
    int? NodeUtcOffsetSeconds = null,
    bool? JwksCacheStale = null,
    bool? RecentIdentityConfigurationChange = null,
    string? ProposedAction = null);

public sealed record ResumeAtlasIncidentCommand(long ExpectedVersion, AtlasIncidentInput Input);
public sealed record CancelAtlasIncidentCommand(long ExpectedVersion);

public sealed record AtlasIncidentFinding(string Code, string Summary, string Recommendation, int Priority);

/// <summary>检查点不包含原始 Token、审批凭据或连接信息。</summary>
public sealed record AtlasIncidentCheckpoint(
    string RunId,
    AccessContext Access,
    string RunbookId,
    string RunbookVersion,
    AtlasIncidentStatus Status,
    SafeAtlasIncidentInput SafeInput,
    IReadOnlyList<string> RequiredInputs,
    IReadOnlyList<AtlasIncidentFinding> Findings,
    string? ProposedAction,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

public sealed record AtlasIncidentLeaseResult(bool Acquired, string? LeaseToken, AtlasIncidentCheckpoint? Checkpoint);

/// <summary>可替换的状态存储；租约与版本号共同保证同一运行只有一个推进者。</summary>
public interface IAtlasIncidentStore
{
    Task CreateAsync(AtlasIncidentCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AtlasIncidentCheckpoint>> ListAsync(AccessContext access, int limit,
        CancellationToken cancellationToken = default);
    Task<AtlasIncidentCheckpoint?> GetAsync(string runId, AccessContext access,
        CancellationToken cancellationToken = default);
    Task<AtlasIncidentLeaseResult> TryAcquireAsync(string runId, AccessContext access, long expectedVersion,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    Task<AtlasIncidentCheckpoint> SaveAndReleaseAsync(AtlasIncidentCheckpoint checkpoint, string leaseToken,
        CancellationToken cancellationToken = default);
    Task<AtlasIncidentCheckpoint> CancelAsync(string runId, AccessContext access, long expectedVersion,
        CancellationToken cancellationToken = default);
}

public interface IAtlasIncidentWorkflow
{
    Task<AtlasIncidentCheckpoint> StartAsync(AtlasIncidentInput input, AccessContext access,
        CancellationToken cancellationToken = default);
    Task<AtlasIncidentCheckpoint> GetAsync(string runId, AccessContext access,
        CancellationToken cancellationToken = default);
    Task<AtlasIncidentCheckpoint> ResumeAsync(string runId, long expectedVersion, AtlasIncidentInput input,
        AccessContext access, CancellationToken cancellationToken = default);
    Task<AtlasIncidentCheckpoint> CancelAsync(string runId, long expectedVersion, AccessContext access,
        CancellationToken cancellationToken = default);
}

public enum AtlasIncidentErrorKind { Validation, Forbidden, NotFound, Conflict }

public sealed class AtlasIncidentWorkflowException(string code, string message, AtlasIncidentErrorKind kind)
    : Exception(message)
{
    public string Code { get; } = code;
    public AtlasIncidentErrorKind Kind { get; } = kind;
}

public sealed class AtlasIncidentWorkflowOptions
{
    public string RunbookId { get; init; } = "BK-RUN-001";
    public string RunbookVersion { get; init; } = "2.2";
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan ProgressLeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
}
