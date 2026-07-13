namespace AiMentor.Domain;

public enum MemoryScope { Session, UserPreference, LongTermFact }
public enum MemoryProposalStatus { PendingApproval, Approved }

public sealed record MemoryProposal(
    string Id,
    string TenantId,
    string SubjectId,
    MemoryScope Scope,
    string? SessionId,
    string Key,
    string Value,
    DateTimeOffset CreatedAt,
    DateTimeOffset ApprovalExpiresAt,
    DateTimeOffset MemoryExpiresAt,
    MemoryProposalStatus Status);

public sealed record MemoryRecord(
    string Id,
    string TenantId,
    string SubjectId,
    MemoryScope Scope,
    string? SessionId,
    string Key,
    string Value,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);
