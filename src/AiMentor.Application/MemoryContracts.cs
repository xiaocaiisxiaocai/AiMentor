using AiMentor.Domain;

namespace AiMentor.Application;

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

public enum MemoryStoreStatus { Success, NotFound, Conflict, Expired, RetentionExceeded, AlreadyExists }

public sealed record MemoryStoreResult<T>(MemoryStoreStatus Status, T? Value = default);

public interface IMemoryContentSafetyService
{
    SafetyDecision Review(string key, string value);
}

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

public sealed record ProposeMemoryCommand(
    MemoryScope Scope,
    string Key,
    string Value,
    string? SessionId = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record CorrectMemoryCommand(string Value, int ExpectedVersion, DateTimeOffset? ExpiresAt = null);

public enum MemoryWorkflowErrorKind { Validation, NotFound, Conflict, Unsafe }

public sealed class MemoryWorkflowException(string code, string message, MemoryWorkflowErrorKind kind) : Exception(message)
{
    public string Code { get; } = code;
    public MemoryWorkflowErrorKind Kind { get; } = kind;
}

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
