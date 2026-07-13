namespace AiMentor.Domain;

/// <summary>限定记忆的可见范围和默认生命周期。</summary>
public enum MemoryScope { Session, UserPreference, LongTermFact }
/// <summary>区分尚未生效的提案和用户已明确批准的提案。</summary>
public enum MemoryProposalStatus { PendingApproval, Approved }

/// <summary>保存待批准内容及两个独立期限：批准期限和正式记忆期限。</summary>
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

/// <summary>表示当前用户已批准、可查看和可撤销的版本化记忆。</summary>
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

/// <summary>暴露给回答编排器的最小记忆投影，不包含租户、用户和存储标识。</summary>
public sealed record MemoryContextItem(
    MemoryScope Scope,
    string Key,
    string Value,
    DateTimeOffset UpdatedAt);
