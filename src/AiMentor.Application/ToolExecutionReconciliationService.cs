using AiMentor.Domain;

namespace AiMentor.Application;

/// <summary>实施租户隔离和独立角色门禁后，返回最小化的结果不确定执行摘要。</summary>
public sealed class ToolExecutionReconciliationService(
    IToolExecutionLedger ledger,
    ITraceSink traceSink,
    ToolExecutionReconciliationOptions options,
    TimeProvider timeProvider) : IToolExecutionReconciliationService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(
        AccessContext access, int limit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(access.TenantId) || string.IsNullOrWhiteSpace(access.SubjectId))
            throw new ToolExecutionReconciliationException("TOOL_RECONCILIATION_ACCESS_INVALID",
                "对账访问上下文无效。", ToolExecutionReconciliationErrorKind.Validation);
        if (!access.Groups.Overlaps(options.ReconcilerGroups))
            throw new ToolExecutionReconciliationException("TOOL_RECONCILER_ROLE_REQUIRED",
                "只有工具执行对账人员可以查看结果不确定记录。", ToolExecutionReconciliationErrorKind.Forbidden);
        if (limit <= 0 || limit > options.MaximumPageSize)
            throw new ToolExecutionReconciliationException("TOOL_RECONCILIATION_LIMIT_INVALID",
                $"limit 必须在 1 到 {options.MaximumPageSize} 之间。",
                ToolExecutionReconciliationErrorKind.Validation);

        var records = await ledger.ListOutcomeUnknownAsync(access.TenantId, limit, cancellationToken);
        await traceSink.WriteAsync($"tool-reconciliation-{Guid.NewGuid():N}",
            [new TraceStep("tool.execution.reconciliation.list", "ok", timeProvider.GetUtcNow(),
                new Dictionary<string, object?>
                {
                    ["tenantId"] = access.TenantId,
                    ["subjectId"] = access.SubjectId,
                    ["count"] = records.Count
                })], cancellationToken);
        return records;
    }
}
