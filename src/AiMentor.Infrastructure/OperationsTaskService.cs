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
    TimeProvider timeProvider) : IOperationsTaskService
{
    public async Task<OperationsTaskPage> ListAsync(AccessContext access, string? type, string? status,
        string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "limit 必须在 1 到 100 之间。");
        var now = timeProvider.GetUtcNow();
        var tasks = new List<OperationsTaskSummary>();
        var approvalRows = await approvals.ListAsync(access, cancellationToken: cancellationToken);
        tasks.AddRange(approvalRows.Select(item => Summary(item, access, now)));
        var compensationRows = await compensations.ListAsync(access, cancellationToken: cancellationToken);
        tasks.AddRange(compensationRows.Select(item => Summary(item, access, now)));
        if (access.Groups.Overlaps(executionOptions.ReconcilerGroups))
        {
            var executionRows = await executions.ListOutcomeUnknownAsync(access, 100, cancellationToken);
            tasks.AddRange(executionRows.Select(item => Summary(item, now)));
        }
        var atlasRows = await atlas.ListAsync(access, 100, cancellationToken);
        tasks.AddRange(atlasRows.Select(item => Summary(item, now)));

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

    private OperationsTaskSummary Summary(ToolApprovalRequest item, AccessContext access, DateTimeOffset now)
    {
        var actions = item.Status == ToolApprovalStatus.Pending && access.Groups.Overlaps(approvalOptions.ApproverGroups)
            ? new[] { "approve", "reject" } : [];
        return Create(item.Id, "approval", item.Status.ToString(), item.CreatedAt,
            item.DecidedAt ?? item.ConsumedAt ?? item.CreatedAt, item.ExpiresAt, actions, now,
            $"{item.Status}|{item.DecidedAt:O}|{item.ConsumedAt:O}");
    }

    private OperationsTaskSummary Summary(ToolCompensationSummary item, AccessContext access, DateTimeOffset now)
    {
        IReadOnlyList<string> actions = item.Status switch
        {
            ToolCompensationStatus.AwaitingApproval when access.Groups.Overlaps(compensationOptions.ApproverGroups)
                => ["approve", "reject"],
            ToolCompensationStatus.OutcomeUnknown when access.Groups.Overlaps(compensationOptions.ReconcilerGroups)
                => ["probe", "review"],
            _ => []
        };
        return Create(item.Id, "compensation", item.Status.ToString(), item.CreatedAt,
            item.CompletedAt ?? item.CreatedAt, item.ExpiresAt, actions, now,
            $"{item.Status}|{item.ApprovalId}|{item.CompletedAt:O}");
    }

    private static OperationsTaskSummary Summary(OutcomeUnknownToolExecution item, DateTimeOffset now) =>
        Create(item.ExecutionKey, "execution", "OutcomeUnknown", item.CreatedAt, item.UpdatedAt, null,
            ["probe", "review"], now, $"{item.UpdatedAt:O}");

    private static OperationsTaskSummary Summary(AtlasIncidentCheckpoint item, DateTimeOffset now)
    {
        var terminal = item.Status is AtlasIncidentStatus.Cancelled or AtlasIncidentStatus.Expired;
        return Create(item.RunId, "incident", item.Status.ToString(), item.CreatedAt, item.UpdatedAt,
            item.ExpiresAt, terminal ? [] : ["resume", "cancel"], now, $"{item.Status}|{item.Version}");
    }

    private static OperationsTaskSummary Create(string id, string type, string status, DateTimeOffset createdAt,
        DateTimeOffset updatedAt, DateTimeOffset? dueAt, IReadOnlyList<string> actions, DateTimeOffset now,
        string version)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{type}\u001f{id}\u001f{version}")));
        return new OperationsTaskSummary(id, type, status, createdAt, updatedAt, dueAt,
            dueAt is not null && dueAt < now, actions, $"\"{hash}\"");
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
