using System.Text.Json.Serialization;

namespace AiMentor.Domain;

/// <summary>描述修改性工具审批从申请到终止的生命周期。</summary>
public enum ToolApprovalStatus { Pending, Approved, Rejected, Consumed, Expired }

/// <summary>
/// 保存不含原始参数值的审批凭据；参数摘要用于把批准精确绑定到申请时的调用。
/// </summary>
public sealed record ToolApprovalRequest(
    string Id,
    string TenantId,
    string RequesterSubjectId,
    string ToolName,
    ToolOperationRisk Risk,
    [property: JsonIgnore] string ArgumentsHash,
    IReadOnlyList<string> ArgumentNames,
    string Justification,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ToolApprovalStatus Status,
    string? ApproverSubjectId = null,
    DateTimeOffset? DecidedAt = null,
    string? DecisionReason = null,
    DateTimeOffset? ConsumedAt = null);

/// <summary>返回审批凭据能否授权当前精确工具调用的原子消费结果。</summary>
public sealed record ToolApprovalConsumption(bool Allowed, SafetyDecision Decision);
