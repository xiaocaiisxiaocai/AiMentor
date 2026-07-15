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
            new ToolExecutionReconciliationOptions(), clock);

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
        public Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(AccessContext access, int limit, CancellationToken cancellationToken = default) => Task.FromResult(rows);
        public Task<ToolOutcomeProbeResult> ProbeOutcomeAsync(AccessContext access, string executionKey, JsonElement arguments, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolReconciliationReviewResult> ReviewOutcomeAsync(AccessContext access, string executionKey, JsonElement arguments, bool confirmed, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
