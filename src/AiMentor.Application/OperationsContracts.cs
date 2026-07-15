using AiMentor.Domain;

namespace AiMentor.Application;

public sealed record OperationsTaskSummary(string Id, string Type, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? DueAt, bool Overdue, IReadOnlyList<string> AllowedActions, string ETag,
    string SlaStatus = "OnTrack", long? Version = null, string? TargetType = null, string? TargetId = null,
    string? RequestedAction = null);

public sealed record OperationsTaskPage(IReadOnlyList<OperationsTaskSummary> Items, string? NextCursor);

public interface IOperationsTaskService
{
    Task<OperationsTaskPage> ListAsync(AccessContext access, string? type, string? status, string? cursor,
        int limit, CancellationToken cancellationToken = default);
    Task<bool> IsEscalationCurrentAsync(string targetType, string targetId, string targetETag,
        AccessContext access, CancellationToken cancellationToken = default);
}

/// <summary>运营动作必须先提出、再由独立第二人复核；状态本身也是持久审计记录。</summary>
public enum OperationsActionStatus
{
    AwaitingReview, Executing, Completed, Rejected, Failed, OutcomeUnknown, Expired,
    OutcomeUnknownArchived
}

/// <summary>不包含理由原文的运营动作记录；主体标识仅供服务端职责分离，不通过 API 序列化。</summary>
public sealed record OperationsActionRecord(
    string Id,
    string TenantId,
    string TargetType,
    string TargetId,
    string Action,
    string TargetETag,
    string RequestFingerprint,
    string IdempotencyHash,
    string RequesterSubjectId,
    string RequestReasonHash,
    OperationsActionStatus Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? ReviewerSubjectId = null,
    string? ReviewReasonHash = null,
    DateTimeOffset? ReviewedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? OutcomeCode = null);

/// <summary>只返回工作台执行复核所需的脱敏字段。</summary>
public sealed record OperationsActionSummary(
    string Id,
    string TargetType,
    string TargetId,
    string Action,
    OperationsActionStatus Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? CompletedAt,
    string? OutcomeCode,
    bool IdempotentReplay,
    string ETag);

public enum OperationsActionCreateStatus { Created, Replay, IdempotencyConflict, TargetBusy, Capacity }
public sealed record OperationsActionCreateResult(OperationsActionCreateStatus Status, OperationsActionRecord? Record);
public enum OperationsActionAcquireStatus
{
    Acquired, Rejected, Replay, ReviewerMustDiffer, VersionConflict, NotFound, Expired
}
public sealed record OperationsActionAcquireResult(OperationsActionAcquireStatus Status, OperationsActionRecord? Record);

/// <summary>为内存和 SQL Server 提供同一原子创建、复核占位和终态写回契约。</summary>
public interface IOperationsActionStore
{
    Task<OperationsActionCreateResult> CreateAsync(OperationsActionRecord record, int maximumEntries,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperationsActionRecord>> ListAsync(string tenantId, int limit,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperationsActionRecord>> ListTaskStateAsync(string tenantId,
        CancellationToken cancellationToken = default);
    Task<OperationsActionRecord?> GetAsync(string tenantId, string id,
        CancellationToken cancellationToken = default);
    Task<OperationsActionRecord?> GetByIdempotencyAsync(string tenantId, string idempotencyHash,
        CancellationToken cancellationToken = default);
    Task<OperationsActionAcquireResult> TryAcquireReviewAsync(string tenantId, string id, long expectedVersion,
        string reviewerSubjectId, bool approved, string reviewReasonHash, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<OperationsActionRecord> CompleteAsync(string tenantId, string id, long expectedVersion,
        OperationsActionStatus status, string outcomeCode, DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public interface IOperationsActionService
{
    Task<OperationsActionSummary> RequestAsync(string targetType, string targetId, string action,
        string targetETag, string reason, string idempotencyKey, AccessContext access,
        CancellationToken cancellationToken = default);
    Task<OperationsActionSummary> ReviewAsync(string requestId, long expectedVersion, string expectedETag,
        bool approved, string reason, AccessContext access, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperationsActionSummary>> ListAsync(AccessContext access,
        CancellationToken cancellationToken = default);
    Task<OperationsActionSummary> GetAsync(string requestId, AccessContext access,
        CancellationToken cancellationToken = default);
}

public sealed class OperationsActionOptions
{
    public TimeSpan ReviewLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public int MaximumEntries { get; init; } = 10_000;
    public int MaximumAuditEntriesPerTenant { get; init; } = 100_000;
    public TimeSpan OutcomeUnknownRetention { get; init; } = TimeSpan.FromDays(30);
    public IReadOnlySet<string> ReviewerGroups { get; init; } =
        new HashSet<string>(["tool-approvers"], StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> EscalatorGroups { get; init; } =
        new HashSet<string>(["operations-escalators"], StringComparer.OrdinalIgnoreCase);
}

public enum OperationsActionErrorKind { Validation, Forbidden, NotFound, Conflict, Capacity, Unavailable }
public sealed class OperationsActionException(string code, string message, OperationsActionErrorKind kind)
    : Exception(message)
{
    public string Code { get; } = code;
    public OperationsActionErrorKind Kind { get; } = kind;
}
