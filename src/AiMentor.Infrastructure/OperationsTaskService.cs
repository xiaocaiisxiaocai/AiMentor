using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>聚合各工作流已经授权过滤后的最小任务摘要，不读取参数、正文、理由或加密载荷。</summary>
public sealed class OperationsTaskService(
    IToolApprovalService approvals,
    IToolCompensationService compensations,
    IToolExecutionReconciliationService executions,
    IAtlasIncidentStore atlas,
    ToolApprovalOptions approvalOptions,
    ToolCompensationOptions compensationOptions,
    ToolExecutionReconciliationOptions executionOptions,
    IOperationsActionStore actionStore,
    OperationsActionOptions actionOptions,
    TimeProvider timeProvider) : IOperationsTaskService
{
    public async Task<OperationsTaskPage> ListAsync(AccessContext access, string? type, string? status,
        string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "limit 必须在 1 到 100 之间。");
        var now = timeProvider.GetUtcNow();
        var tasks = new List<OperationsTaskSummary>();
        var actionRows = await actionStore.ListTaskStateAsync(access.TenantId, cancellationToken);
        var actionsByTarget = actionRows.GroupBy(item => (item.TargetType, item.TargetId))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var approvalRows = await approvals.ListAsync(access, cancellationToken: cancellationToken);
        tasks.AddRange(approvalRows.Select(item => Decorate(Summary(item, access, now), actionsByTarget, access, now)));
        var compensationRows = await compensations.ListAsync(access, cancellationToken: cancellationToken);
        tasks.AddRange(compensationRows.Select(item =>
            Decorate(Summary(item, access, now), actionsByTarget, access, now)));
        if (access.Groups.Overlaps(executionOptions.ReconcilerGroups))
        {
            var executionRows = await executions.ListOutcomeUnknownAsync(access, 100, cancellationToken);
            tasks.AddRange(executionRows.Select(item => Decorate(Summary(item, now), actionsByTarget, access, now)));
        }
        var atlasRows = await atlas.ListAsync(access, 100, cancellationToken);
        tasks.AddRange(atlasRows.Select(item => Decorate(Summary(item, now), actionsByTarget, access, now)));
        tasks.AddRange(actionRows.Where(item => (item.Status == OperationsActionStatus.AwaitingReview
                    && item.RequesterSubjectId != access.SubjectId
                    || item.Status is OperationsActionStatus.Executing or OperationsActionStatus.OutcomeUnknown
                        or OperationsActionStatus.OutcomeUnknownArchived)
                && CanReview(item, access))
            .Select(item => Create(item.Id, "action-review", item.Status.ToString(), item.CreatedAt, item.CreatedAt,
                item.ExpiresAt, item.Status == OperationsActionStatus.AwaitingReview ? ["confirm", "decline"] : [],
                now, $"{item.Status}|{item.Version}") with
            {
                Version = item.Version,
                TargetType = item.TargetType,
                TargetId = item.TargetId,
                RequestedAction = item.Action,
                ETag = OperationsActionService.ETag(item)
            }));

        if (!string.IsNullOrWhiteSpace(type))
            tasks = tasks.Where(item => item.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(status))
            tasks = tasks.Where(item => item.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
        tasks.Sort(Compare);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var key = DecodeCursor(cursor);
            tasks = tasks.Where(item => Compare(Key(item), key) > 0).ToList();
        }
        var page = tasks.Take(limit).ToArray();
        var next = tasks.Count > limit ? EncodeCursor(Key(page[^1])) : null;
        return new OperationsTaskPage(page, next);
    }

    public async Task<bool> IsEscalationCurrentAsync(string targetType, string targetId, string targetETag,
        AccessContext access, CancellationToken cancellationToken = default)
    {
        OperationsTaskSummary? task = targetType switch
        {
            "approval" => (await approvals.ListAsync(access, cancellationToken: cancellationToken))
                .Where(item => item.Id == targetId).Select(item => Summary(item, access, timeProvider.GetUtcNow()))
                .SingleOrDefault(),
            "compensation" => (await compensations.ListAsync(access, cancellationToken: cancellationToken))
                .Where(item => item.Id == targetId).Select(item => Summary(item, access, timeProvider.GetUtcNow()))
                .SingleOrDefault(),
            "incident" => await IncidentTaskAsync(access.TenantId, targetId, cancellationToken),
            _ => null
        };
        return task is not null && string.Equals(task.ETag, targetETag, StringComparison.Ordinal)
            && task.SlaStatus == "Breached" && IsEscalationEligible(task);
    }

    private OperationsTaskSummary Summary(ToolApprovalRequest item, AccessContext access, DateTimeOffset now)
    {
        var actions = item.Status == ToolApprovalStatus.Pending
            && !string.Equals(item.RequesterSubjectId, access.SubjectId, StringComparison.Ordinal)
            && access.Groups.Overlaps(approvalOptions.ApproverGroups)
            ? new[] { "approve", "reject" } : [];
        var completedAt = item.Status == ToolApprovalStatus.Pending ? null : item.DecidedAt ?? item.ConsumedAt;
        var sla = Sla(item.ExpiresAt, now, completedAt, item.Status == ToolApprovalStatus.Pending,
            item.Status == ToolApprovalStatus.Expired);
        return Create(item.Id, "approval", item.Status.ToString(), item.CreatedAt,
            item.DecidedAt ?? item.ConsumedAt ?? item.CreatedAt, item.ExpiresAt, actions, now,
            $"{item.Status}|{item.DecidedAt:O}|{item.ConsumedAt:O}", sla);
    }

    private OperationsTaskSummary Summary(ToolCompensationSummary item, AccessContext access, DateTimeOffset now)
    {
        IReadOnlyList<string> actions = item.Status switch
        {
            ToolCompensationStatus.AwaitingApproval when access.Groups.Overlaps(compensationOptions.ApproverGroups)
                => ["approve", "reject"],
            _ => []
        };
        var sla = Sla(item.ExpiresAt, now, item.CompletedAt,
            item.Status == ToolCompensationStatus.AwaitingApproval, item.Status == ToolCompensationStatus.Expired);
        return Create(item.Id, "compensation", item.Status.ToString(), item.CreatedAt,
            item.CompletedAt ?? item.CreatedAt, item.ExpiresAt, actions, now,
            $"{item.Status}|{item.ApprovalId}|{item.CompletedAt:O}", sla);
    }

    private static OperationsTaskSummary Summary(OutcomeUnknownToolExecution item, DateTimeOffset now) =>
        Create(item.ExecutionKey, "execution", "OutcomeUnknown", item.CreatedAt, item.UpdatedAt, null,
            [], now, $"{item.UpdatedAt:O}");

    private static OperationsTaskSummary Summary(AtlasIncidentCheckpoint item, DateTimeOffset now)
    {
        var terminal = item.Status is AtlasIncidentStatus.Cancelled or AtlasIncidentStatus.Expired;
        var sla = Sla(item.ExpiresAt, now, terminal ? item.UpdatedAt : null, !terminal,
            item.Status == AtlasIncidentStatus.Expired);
        return Create(item.RunId, "incident", item.Status.ToString(), item.CreatedAt, item.UpdatedAt,
            item.ExpiresAt, [], now, $"{item.Status}|{item.Version}", sla);
    }

    private static OperationsTaskSummary Summary(AtlasIncidentTaskState item, DateTimeOffset now)
    {
        var terminal = item.Status is AtlasIncidentStatus.Cancelled or AtlasIncidentStatus.Expired;
        var sla = Sla(item.ExpiresAt, now, terminal ? item.UpdatedAt : null, !terminal,
            item.Status == AtlasIncidentStatus.Expired);
        return Create(item.RunId, "incident", item.Status.ToString(), item.CreatedAt, item.UpdatedAt,
            item.ExpiresAt, [], now, $"{item.Status}|{item.Version}", sla);
    }

    private async Task<OperationsTaskSummary?> IncidentTaskAsync(string tenantId, string runId,
        CancellationToken cancellationToken)
    {
        var state = await atlas.GetTaskStateAsync(tenantId, runId, cancellationToken);
        return state is null ? null : Summary(state, timeProvider.GetUtcNow());
    }

    private OperationsTaskSummary Decorate(OperationsTaskSummary task,
        IReadOnlyDictionary<(string TargetType, string TargetId), OperationsActionRecord[]> actionsByTarget,
        AccessContext access, DateTimeOffset now)
    {
        var related = actionsByTarget.GetValueOrDefault((task.Type, task.Id)) ?? [];
        var escalated = related.Any(item => item.Action == "escalate"
            && item.Status == OperationsActionStatus.Completed
            && string.Equals(item.TargetETag, task.ETag, StringComparison.Ordinal));
        var active = related.Any(item => item.Status is OperationsActionStatus.AwaitingReview
            or OperationsActionStatus.Executing or OperationsActionStatus.OutcomeUnknown
            or OperationsActionStatus.OutcomeUnknownArchived);
        var sla = escalated ? "Escalated" : task.SlaStatus;
        IReadOnlyList<string> allowed = active ? [] : task.AllowedActions;
        if (!access.Groups.Overlaps(actionOptions.ReviewerGroups))
            allowed = allowed.Where(item => item is not ("approve" or "reject")).ToArray();
        if (!active && sla == "Breached" && IsEscalationEligible(task)
            && access.Groups.Overlaps(actionOptions.EscalatorGroups))
            allowed = allowed.Concat(["escalate"]).Distinct(StringComparer.Ordinal).ToArray();
        return task with
        {
            Overdue = sla is "Breached" or "Escalated",
            SlaStatus = sla,
            AllowedActions = allowed
        };
    }

    private bool CanReview(OperationsActionRecord action, AccessContext access)
    {
        if (action.Action == "escalate") return access.Groups.Overlaps(actionOptions.EscalatorGroups);
        if (!access.Groups.Overlaps(actionOptions.ReviewerGroups)) return false;
        return action.TargetType switch
        {
            "approval" => access.Groups.Overlaps(approvalOptions.ApproverGroups),
            "compensation" => access.Groups.Overlaps(compensationOptions.ApproverGroups),
            _ => false
        };
    }

    private static bool IsEscalationEligible(OperationsTaskSummary task) => task.Type switch
    {
        // 审批与补偿允许对已确认的逾期终态补记 SLA；Atlas 终态不可再升级。
        "approval" => true,
        "compensation" => true,
        "incident" => task.Status is not (nameof(AtlasIncidentStatus.Cancelled) or nameof(AtlasIncidentStatus.Expired)),
        _ => false
    };

    private static string Sla(DateTimeOffset? dueAt, DateTimeOffset now, DateTimeOffset? completedAt,
        bool active, bool forcedBreach)
    {
        if (dueAt is null) return "OnTrack";
        if (forcedBreach) return "Breached";
        if (completedAt is not null) return completedAt <= dueAt ? "OnTrack" : "Breached";
        return active && now >= dueAt ? "Breached" : "OnTrack";
    }

    private static OperationsTaskSummary Create(string id, string type, string status, DateTimeOffset createdAt,
        DateTimeOffset updatedAt, DateTimeOffset? dueAt, IReadOnlyList<string> actions, DateTimeOffset now,
        string version, string? slaStatus = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{type}\u001f{id}\u001f{version}")));
        var sla = slaStatus ?? (dueAt is not null && dueAt <= now ? "Breached" : "OnTrack");
        return new OperationsTaskSummary(id, type, status, createdAt, updatedAt, dueAt,
            sla == "Breached", actions, $"\"{hash}\"", sla);
    }

    private static int Compare(OperationsTaskSummary left, OperationsTaskSummary right) =>
        Compare(Key(left), Key(right));
    private static int Compare(CursorKey left, CursorKey right)
    {
        var due = left.DueTicks.CompareTo(right.DueTicks);
        if (due != 0) return due;
        var type = string.CompareOrdinal(left.Type, right.Type);
        return type != 0 ? type : string.CompareOrdinal(left.Id, right.Id);
    }
    private static CursorKey Key(OperationsTaskSummary item) =>
        new(item.DueAt?.UtcTicks ?? long.MaxValue, item.Type, item.Id);
    private static string EncodeCursor(CursorKey key) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(key));
    private static CursorKey DecodeCursor(string cursor)
    {
        try
        {
            return JsonSerializer.Deserialize<CursorKey>(Convert.FromBase64String(cursor))
                   ?? throw new FormatException();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("cursor 格式无效。", nameof(cursor));
        }
    }
    private sealed record CursorKey(long DueTicks, string Type, string Id);
}
