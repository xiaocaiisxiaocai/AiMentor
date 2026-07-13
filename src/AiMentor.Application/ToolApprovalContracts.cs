using System.Text.Json;
using AiMentor.Domain;

namespace AiMentor.Application;

/// <summary>创建、裁决、查询并一次性消费修改性工具审批。</summary>
public interface IToolApprovalService
{
    Task<ToolApprovalRequest> RequestAsync(string toolName, JsonElement arguments, string justification,
        AccessContext requester, CancellationToken cancellationToken = default);
    Task<ToolApprovalRequest> DecideAsync(string approvalId, bool approved, string reason, AccessContext approver,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolApprovalRequest>> ListAsync(AccessContext access, ToolApprovalStatus? status = null,
        CancellationToken cancellationToken = default);
    Task<ToolApprovalRequest> GetAsync(string approvalId, AccessContext access,
        CancellationToken cancellationToken = default);
    Task<ToolApprovalConsumption> ConsumeAsync(string approvalId, string toolName, JsonElement arguments,
        AccessContext requester, CancellationToken cancellationToken = default);
}

/// <summary>配置审批有效期、审批人组以及内存记录容量。</summary>
public sealed class ToolApprovalOptions
{
    public TimeSpan ApprovalLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public IReadOnlySet<string> ApproverGroups { get; init; } =
        new HashSet<string>(["tool-approvers"], StringComparer.OrdinalIgnoreCase);
    public int MaximumEntries { get; init; } = 10_000;
    public int MaximumArgumentBytes { get; init; } = 16 * 1024;
}

/// <summary>区分审批输入、权限、资源不存在和状态冲突错误。</summary>
public enum ToolApprovalErrorKind { Validation, Forbidden, NotFound, Conflict, Capacity }

/// <summary>携带稳定错误码且不泄漏工具参数的审批工作流异常。</summary>
public sealed class ToolApprovalException(string code, string message, ToolApprovalErrorKind kind) : Exception(message)
{
    public string Code { get; } = code;
    public ToolApprovalErrorKind Kind { get; } = kind;
}
