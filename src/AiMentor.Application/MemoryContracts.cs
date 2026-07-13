using AiMentor.Domain;

namespace AiMentor.Application;

/// <summary>定义带所有权校验和乐观并发语义的记忆存储端口。</summary>
public interface IMemoryStore
{
    Task SaveProposalAsync(MemoryProposal proposal, CancellationToken cancellationToken = default);
    Task<MemoryStoreResult<MemoryRecord>> ApproveAsync(string proposalId, AccessContext access, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(AccessContext access, MemoryScope? scope, string? sessionId,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<MemoryStoreResult<MemoryRecord>> UpdateAsync(string memoryId, AccessContext access, int expectedVersion,
        string value, DateTimeOffset? expiresAt, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<MemoryStoreResult<bool>> DeleteAsync(string memoryId, AccessContext access, int expectedVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>存储层返回的稳定状态，避免基础设施异常直接泄漏到 API。</summary>
public enum MemoryStoreStatus { Success, NotFound, Conflict, Expired, RetentionExceeded, AlreadyExists }

/// <summary>封装记忆存储操作的状态和可选结果。</summary>
public sealed record MemoryStoreResult<T>(MemoryStoreStatus Status, T? Value = default);

/// <summary>审核待保存或待注入的记忆是否包含凭证、敏感标识或提示词注入。</summary>
public interface IMemoryContentSafetyService
{
    SafetyDecision Review(string key, string value);
}

/// <summary>为单次问答提供最小、相关且属于当前调用者的只读记忆上下文。</summary>
public interface IMemoryContextProvider
{
    Task<IReadOnlyList<MemoryContextItem>> GetRelevantAsync(string question, AccessContext access, string? sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>限制单次提示词可注入的记忆数量与总体字符预算。</summary>
public sealed class MemoryContextOptions
{
    public int MaximumItems { get; init; } = 5;
    public int MaximumTotalCharacters { get; init; } = 1_000;
}

/// <summary>编排记忆提案、显式批准、查看、更正和删除工作流。</summary>
public interface IMemoryWorkflowService
{
    Task<MemoryProposal> ProposeAsync(ProposeMemoryCommand command, AccessContext access,
        CancellationToken cancellationToken = default);
    Task<MemoryRecord> ApproveAsync(string proposalId, AccessContext access,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryRecord>> ListAsync(AccessContext access, MemoryScope? scope = null, string? sessionId = null,
        CancellationToken cancellationToken = default);
    Task<MemoryRecord> CorrectAsync(string memoryId, CorrectMemoryCommand command, AccessContext access,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(string memoryId, int expectedVersion, AccessContext access,
        CancellationToken cancellationToken = default);
}

/// <summary>表示尚未生效、必须由同一用户批准的记忆提案。</summary>
public sealed record ProposeMemoryCommand(
    MemoryScope Scope,
    string Key,
    string Value,
    string? SessionId = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>使用预期版本更正已批准记忆，防止并发覆盖。</summary>
public sealed record CorrectMemoryCommand(string Value, int ExpectedVersion, DateTimeOffset? ExpiresAt = null);

/// <summary>供 API 映射稳定 HTTP 状态的记忆工作流错误类别。</summary>
public enum MemoryWorkflowErrorKind { Validation, NotFound, Conflict, Unsafe }

/// <summary>携带稳定错误码且不包含记忆正文的工作流异常。</summary>
public sealed class MemoryWorkflowException(string code, string message, MemoryWorkflowErrorKind kind) : Exception(message)
{
    public string Code { get; } = code;
    public MemoryWorkflowErrorKind Kind { get; } = kind;
}

/// <summary>配置各类记忆的批准窗口、默认期限和最大保留期限。</summary>
public sealed class MemoryWorkflowOptions
{
    public TimeSpan ApprovalWindow { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan DefaultSessionLifetime { get; init; } = TimeSpan.FromHours(8);
    public TimeSpan MaximumSessionLifetime { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan DefaultPreferenceLifetime { get; init; } = TimeSpan.FromDays(180);
    public TimeSpan MaximumPreferenceLifetime { get; init; } = TimeSpan.FromDays(365);
    public TimeSpan DefaultFactLifetime { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan MaximumFactLifetime { get; init; } = TimeSpan.FromDays(365);
}
