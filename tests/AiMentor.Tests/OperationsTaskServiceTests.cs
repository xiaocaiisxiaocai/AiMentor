using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class OperationsTaskServiceTests
{
    [Fact]
    public async Task UnifiedQueueIsRedactedSortedCursorPagedAndAtlasOwnerIsolated()
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var access = AccessContext.Create("tenant-a", "owner", ["tool-approvers", "tool-reconcilers"]);
        var atlas = new InMemoryAtlasIncidentStore(clock);
        await atlas.CreateAsync(Checkpoint("atlas-own", access, now.AddHours(4)));
        await atlas.CreateAsync(Checkpoint("atlas-other", AccessContext.Create("tenant-a", "other", ["readers"]),
            now.AddHours(1)));
        var approval = new ToolApprovalRequest("approval-1", "tenant-a", "owner", "memory.delete",
            ToolOperationRisk.Mutation, "secret-argument-hash", ["memoryId"], "secret justification", now,
            now.AddMinutes(-1), ToolApprovalStatus.Pending);
        var compensation = new ToolCompensationSummary("comp-1", "memory.correct", "memory.restore",
            ToolCompensationStatus.OutcomeUnknown, now, now.AddHours(2), Justification: "secret compensation");
        var execution = new OutcomeUnknownToolExecution("execution-1", "tenant-a", "owner", "memory.delete",
            "run-secret", now, now.AddMinutes(1));
        var service = new OperationsTaskService(new ApprovalStub([approval]), new CompensationStub([compensation]),
            new ExecutionStub([execution]), atlas, new ToolApprovalOptions(), new ToolCompensationOptions(),
            new ToolExecutionReconciliationOptions(), new InMemoryOperationsActionStore(),
            new OperationsActionOptions(), clock);

        var first = await service.ListAsync(access, null, null, null, 2);
        var second = await service.ListAsync(access, null, null, first.NextCursor, 2);
        var all = first.Items.Concat(second.Items).ToArray();

        Assert.Equal(4, all.Length);
        Assert.DoesNotContain(all, item => item.Id == "atlas-other");
        Assert.Equal("approval-1", all[0].Id);
        Assert.True(all[0].Overdue);
        Assert.NotNull(first.NextCursor);
        Assert.All(all, item => Assert.StartsWith("\"", item.ETag, StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(all);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memory.delete", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", json, StringComparison.Ordinal);
        Assert.DoesNotContain("owner", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnifiedQueueShouldIncludeOlderOutcomeUnknownExecutionsBeyondFirstHundred()
    {
        var now = new DateTimeOffset(2026, 7, 16, 2, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var access = AccessContext.Create("tenant-a", "operator", ["tool-reconcilers"]);
        var rows = Enumerable.Range(0, 205).Select(index => new OutcomeUnknownToolExecution(
            index.ToString("X64", System.Globalization.CultureInfo.InvariantCulture), access.TenantId,
            "owner", "memory.delete", $"run-{index}",
            now.AddDays(-1), now.AddSeconds(-index))).ToArray();
        var executionOptions = new ToolExecutionReconciliationOptions
        {
            MaximumPageSize = 100,
            MaximumOperationsScan = 500
        };
        var service = new OperationsTaskService(new ApprovalStub([]), new CompensationStub([]),
            new ExecutionStub(rows), new InMemoryAtlasIncidentStore(clock), new ToolApprovalOptions(),
            new ToolCompensationOptions(), executionOptions, new InMemoryOperationsActionStore(),
            new OperationsActionOptions(), clock);

        var all = new List<OperationsTaskSummary>();
        string? cursor = null;
        do
        {
            var page = await service.ListAsync(access, "execution", null, cursor, 100);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(205, all.Count);
        Assert.Equal(205, all.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(all, item => item.Id == 204.ToString(
            "X64", System.Globalization.CultureInfo.InvariantCulture));

        var boundedOptions = new ToolExecutionReconciliationOptions
        {
            MaximumPageSize = 100,
            MaximumOperationsScan = 200
        };
        var boundedService = new OperationsTaskService(new ApprovalStub([]), new CompensationStub([]),
            new ExecutionStub(rows), new InMemoryAtlasIncidentStore(clock), new ToolApprovalOptions(),
            new ToolCompensationOptions(), boundedOptions, new InMemoryOperationsActionStore(),
            new OperationsActionOptions(), clock);
        var overflow = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            boundedService.ListAsync(access, "execution", null, null, 100));
        Assert.Contains("扫描上限", overflow.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlaUsesTerminalCompletionTimeAndNeverEscalatesTerminalIncident()
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var access = AccessContext.Create("tenant-a", "operator",
            ["tool-approvers", "operations-escalators"]);
        var approval = new ToolApprovalRequest("approval-on-time", "tenant-a", "requester", "memory.delete",
            ToolOperationRisk.Mutation, new string('A', 64), ["memoryId"], "redacted", now.AddHours(-2),
            now.AddHours(-1), ToolApprovalStatus.Approved, "operator", now.AddHours(-1).AddMinutes(-1));
        var atlas = new InMemoryAtlasIncidentStore(clock);
        await atlas.CreateAsync(Checkpoint("incident-expired", access, now.AddMinutes(-1)));
        var service = new OperationsTaskService(new ApprovalStub([approval]), new CompensationStub([]),
            new ExecutionStub([]), atlas, new ToolApprovalOptions(), new ToolCompensationOptions(),
            new ToolExecutionReconciliationOptions(), new InMemoryOperationsActionStore(),
            new OperationsActionOptions(), clock);

        var rows = (await service.ListAsync(access, null, null, null, 10)).Items;
        var onTime = Assert.Single(rows, item => item.Id == approval.Id);
        var incident = Assert.Single(rows, item => item.Id == "incident-expired");

        Assert.Equal("OnTrack", onTime.SlaStatus);
        Assert.False(onTime.Overdue);
        Assert.DoesNotContain("escalate", onTime.AllowedActions);
        Assert.Equal(AtlasIncidentStatus.Expired.ToString(), incident.Status);
        Assert.Equal("Breached", incident.SlaStatus);
        Assert.DoesNotContain("escalate", incident.AllowedActions);
    }

    [Fact]
    public void OperationsStaticAssetsContainAccessibleLoadingAndConflictStates()
    {
        var root = Directory.GetParent(Directory.GetParent(WorkspacePathLocator.FindKnowledgeRoot())!.FullName)!.FullName;
        var html = File.ReadAllText(Path.Combine(root, "src", "AiMentor.Api", "wwwroot", "ops", "index.html"));
        var script = File.ReadAllText(Path.Combine(root, "src", "AiMentor.Api", "wwwroot", "ops", "app.js"));
        Assert.Contains("aria-live", html, StringComparison.Ordinal);
        Assert.Contains("task-list", html, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", script, StringComparison.Ordinal);
        Assert.Contains("409", script, StringComparison.Ordinal);
        Assert.Contains("401", script, StringComparison.Ordinal);
        Assert.Contains("403", script, StringComparison.Ordinal);
        Assert.Contains("Idempotency-Key", script, StringComparison.Ordinal);
        Assert.Contains("/ops/api/tasks/", script, StringComparison.Ordinal);
        Assert.Contains("/ops/api/actions/", script, StringComparison.Ordinal);
        Assert.Contains("X-AiMentor-CSRF", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/v1/operations", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/tool-approvals/", script, StringComparison.Ordinal);
        Assert.Contains("syncDetail()", script, StringComparison.Ordinal);
        Assert.Contains("clearDetail()", script, StringComparison.Ordinal);
        Assert.Contains("state.items=[]", script, StringComparison.Ordinal);
    }

    private static AtlasIncidentCheckpoint Checkpoint(string id, AccessContext access, DateTimeOffset expires) =>
        new(id, access, "BK-RUN-001", "2.2", AtlasIncidentStatus.RequiredInputs, new SafeAtlasIncidentInput(),
            ["region"], [], null, 1, expires.AddHours(-4), expires.AddHours(-4), expires);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ApprovalStub(IReadOnlyList<ToolApprovalRequest> rows) : IToolApprovalService
    {
        public Task<IReadOnlyList<ToolApprovalRequest>> ListAsync(AccessContext access, ToolApprovalStatus? status = null,
            CancellationToken cancellationToken = default) => Task.FromResult(rows);
        public Task<ToolApprovalRequest> RequestAsync(string toolName, JsonElement arguments, string justification, AccessContext requester, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolApprovalRequest> DecideAsync(string approvalId, bool approved, string reason, AccessContext approver, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolApprovalRequest> GetAsync(string approvalId, AccessContext access, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolApprovalConsumption> ConsumeAsync(string approvalId, string toolName, JsonElement arguments, AccessContext requester, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CompensationStub(IReadOnlyList<ToolCompensationSummary> rows) : IToolCompensationService
    {
        public bool IsAvailable => true;
        public Task<IReadOnlyList<ToolCompensationSummary>> ListAsync(AccessContext access, ToolCompensationStatus? status = null, CancellationToken cancellationToken = default) => Task.FromResult(rows);
        public Task<ToolCompensationPreparation> PrepareForwardAsync(string executionKey, ICompensableServerTool tool, ToolExecutionContext context, JsonElement arguments, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ActivateAsync(ToolCompensationPreparation preparation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DiscardAsync(ToolCompensationPreparation preparation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkForwardOutcomeUnknownAsync(ToolCompensationPreparation preparation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolCompensationSummary> RequestApprovalAsync(string compensationId, string justification, AccessContext requester, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolCompensationSummary> DecideAsync(string compensationId, string approvalId, bool approved, string reason, AccessContext approver, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolCompensationExecutionResult> ExecuteAsync(string compensationId, string approvalId, string idempotencyKey, AccessContext requester, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ExecutionStub(IReadOnlyList<OutcomeUnknownToolExecution> rows) : IToolExecutionReconciliationService
    {
        public Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(AccessContext access,
            int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutcomeUnknownToolExecution>>(rows.Take(limit).ToArray());
        public Task<OutcomeUnknownToolExecutionPage> ListOutcomeUnknownPageAsync(AccessContext access, int limit,
            string? cursor = null, CancellationToken cancellationToken = default)
        {
            var offset = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            var items = rows.Skip(offset).Take(limit).ToArray();
            var nextOffset = offset + items.Length;
            return Task.FromResult(new OutcomeUnknownToolExecutionPage(
                items, nextOffset < rows.Count
                    ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null));
        }
        public Task<ToolOutcomeProbeResult> ProbeOutcomeAsync(AccessContext access, string executionKey, JsonElement arguments, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolReconciliationReviewResult> ReviewOutcomeAsync(AccessContext access, string executionKey, JsonElement arguments, bool confirmed, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
