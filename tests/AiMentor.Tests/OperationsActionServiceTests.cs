using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class OperationsActionServiceTests
{
    [Fact]
    public async Task ActionRequiresIndependentReviewerAndIdempotencyCannotChangePayload()
    {
        var fixture = Fixture.Create();
        var task = Assert.Single((await fixture.Tasks.ListAsync(fixture.OperatorA, "approval", null, null, 10)).Items);

        var requested = await fixture.Actions.RequestAsync("approval", task.Id, "approve", task.ETag,
            "申请批准", "request-key-001", fixture.OperatorA);
        var proposerView = Assert.Single((await fixture.Tasks.ListAsync(
            fixture.OperatorA, "approval", null, null, 10)).Items);
        var reviewerView = Assert.Single((await fixture.Tasks.ListAsync(
            fixture.OperatorB, "action-review", null, null, 10)).Items);
        var replay = await fixture.Actions.RequestAsync("approval", task.Id, "approve", task.ETag,
            "申请批准", "request-key-001", fixture.OperatorA);
        var changed = await Assert.ThrowsAsync<OperationsActionException>(() => fixture.Actions.RequestAsync(
            "approval", task.Id, "reject", task.ETag, "申请批准", "request-key-001", fixture.OperatorA));
        var selfReview = await Assert.ThrowsAsync<OperationsActionException>(() => fixture.Actions.ReviewAsync(
            requested.Id, requested.Version, requested.ETag, true, "自己复核", fixture.OperatorA));

        var completed = await fixture.Actions.ReviewAsync(requested.Id, requested.Version, requested.ETag, true,
            "独立复核通过", fixture.OperatorB);
        var reviewReplay = await fixture.Actions.ReviewAsync(requested.Id, requested.Version, requested.ETag, true,
            "独立复核通过", fixture.OperatorB);

        Assert.True(replay.IdempotentReplay);
        Assert.Empty(proposerView.AllowedActions);
        Assert.Equal(["confirm", "decline"], reviewerView.AllowedActions);
        Assert.Equal("OPERATIONS_IDEMPOTENCY_KEY_REUSED", changed.Code);
        Assert.Equal("OPERATIONS_REVIEWER_MUST_DIFFER", selfReview.Code);
        Assert.Equal(OperationsActionStatus.Completed, completed.Status);
        Assert.True(reviewReplay.IdempotentReplay);
        Assert.Equal(1, fixture.Approvals.DecideCount);
        Assert.Equal(ToolApprovalStatus.Approved, fixture.Approvals.Current.Status);
        Assert.DoesNotContain("独立复核通过", fixture.Approvals.LastReason, StringComparison.Ordinal);
        Assert.Contains(requested.Id, fixture.Approvals.LastReason, StringComparison.Ordinal);
        var raw = JsonSerializer.Serialize(Assert.Single(await fixture.Store.ListAsync("tenant-a", 10)));
        Assert.DoesNotContain("申请批准", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("独立复核通过", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentSecondReviewHasOneWinnerAndStaleTargetFailsClosed()
    {
        var fixture = Fixture.Create();
        var task = Assert.Single((await fixture.Tasks.ListAsync(fixture.OperatorA, "approval", null, null, 10)).Items);
        var requested = await fixture.Actions.RequestAsync("approval", task.Id, "approve", task.ETag,
            "等待并发复核", "request-key-002", fixture.OperatorA);
        fixture.Approvals.MoveTo(ToolApprovalStatus.Rejected);

        var stale = await Assert.ThrowsAsync<OperationsActionException>(() => fixture.Actions.ReviewAsync(
            requested.Id, requested.Version, requested.ETag, true, "复核旧版本", fixture.OperatorB));
        var audit = Assert.Single(await fixture.Store.ListAsync("tenant-a", 10));

        Assert.Equal("OPERATIONS_TARGET_VERSION_CONFLICT", stale.Code);
        Assert.Equal(OperationsActionStatus.Failed, audit.Status);
        Assert.Equal(0, fixture.Approvals.DecideCount);

        var now = fixture.Clock.GetUtcNow();
        var direct = new OperationsActionRecord("concurrent", "tenant-a", "approval", "approval-2", "approve",
            "\"etag\"", new string('A', 64), new string('B', 64), "operator-a", new string('C', 64),
            OperationsActionStatus.AwaitingReview, 1, now, now.AddMinutes(15));
        Assert.Equal(OperationsActionCreateStatus.Created,
            (await fixture.Store.CreateAsync(direct, 100)).Status);
        var outcomes = await Task.WhenAll(
            fixture.Store.TryAcquireReviewAsync("tenant-a", direct.Id, 1, "reviewer-a", true,
                new string('D', 64), now),
            fixture.Store.TryAcquireReviewAsync("tenant-a", direct.Id, 1, "reviewer-b", true,
                new string('E', 64), now));
        Assert.Single(outcomes, item => item.Status == OperationsActionAcquireStatus.Acquired);
        Assert.Single(outcomes, item => item.Status == OperationsActionAcquireStatus.VersionConflict);
    }

    [Fact]
    public async Task BreachedSlaRequiresTwoPeopleAndEscalationSurvivesServiceReconstruction()
    {
        var fixture = Fixture.Create(overdue: true);
        var task = Assert.Single((await fixture.Tasks.ListAsync(fixture.OperatorA, "approval", null, null, 10)).Items);
        Assert.Equal("Breached", task.SlaStatus);
        Assert.Contains("escalate", task.AllowedActions);

        var request = await fixture.Actions.RequestAsync("approval", task.Id, "escalate", task.ETag,
            "超过处理时限", "sla-key-0001", fixture.OperatorA);
        await Assert.ThrowsAsync<OperationsActionException>(() => fixture.Actions.ReviewAsync(request.Id,
            request.Version, request.ETag, true, "自行升级", fixture.OperatorA));
        var completed = await fixture.Actions.ReviewAsync(request.Id, request.Version, request.ETag, true,
            "独立确认升级", fixture.OperatorB);

        var reconstructedTasks = fixture.ReconstructTasks();
        var escalated = Assert.Single((await reconstructedTasks.ListAsync(
            fixture.OperatorA, "approval", null, null, 10)).Items);
        Assert.Equal(OperationsActionStatus.Completed, completed.Status);
        Assert.Equal("OPERATIONS_SLA_ESCALATED", completed.OutcomeCode);
        Assert.Equal("Escalated", escalated.SlaStatus);
        Assert.DoesNotContain("escalate", escalated.AllowedActions);
        Assert.Equal(0, fixture.Approvals.DecideCount);
    }

    [Fact]
    public async Task EscalationReviewRejectsTargetThatChangedAfterProposal()
    {
        var fixture = Fixture.Create(overdue: true);
        var task = Assert.Single((await fixture.Tasks.ListAsync(fixture.OperatorA, "approval", null, null, 10)).Items);
        var request = await fixture.Actions.RequestAsync("approval", task.Id, "escalate", task.ETag,
            "提出逾期升级", "stale-sla-001", fixture.OperatorA);
        fixture.Approvals.MoveTo(ToolApprovalStatus.Rejected);

        var conflict = await Assert.ThrowsAsync<OperationsActionException>(() => fixture.Actions.ReviewAsync(
            request.Id, request.Version, request.ETag, true, "确认升级", fixture.OperatorB));
        var audit = Assert.Single(await fixture.Store.ListAsync("tenant-a", 10));

        Assert.Equal("OPERATIONS_TARGET_VERSION_CONFLICT", conflict.Code);
        Assert.Equal(OperationsActionStatus.Failed, audit.Status);
        Assert.Equal(0, fixture.Approvals.DecideCount);
    }

    [Fact]
    public async Task IncidentEscalationReviewUsesMetadataAndRejectsStaleTerminalWithoutImpersonatingOwner()
    {
        var now = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var owner = AccessContext.Create("tenant-a", "incident-owner", ["operations-escalators"]);
        var reviewer = AccessContext.Create("tenant-a", "incident-reviewer", ["operations-escalators"]);
        var atlas = new InMemoryAtlasIncidentStore(clock);
        var checkpoint = new AtlasIncidentCheckpoint("incident-1", owner, "BK-RUN-001", "2.2",
            AtlasIncidentStatus.RequiredInputs, new SafeAtlasIncidentInput(), ["region"], [], null, 1,
            now.AddHours(-2), now.AddHours(-2), now.AddMinutes(1));
        await atlas.CreateAsync(checkpoint);
        var approval = new ToolApprovalRequest("unused", "tenant-a", "requester", "memory.delete",
            ToolOperationRisk.Mutation, new string('F', 64), ["memoryId"], "unused", now,
            now.AddMinutes(15), ToolApprovalStatus.Rejected);
        var approvals = new ApprovalService(approval, clock);
        var compensations = new EmptyCompensationService();
        var actionStore = new InMemoryOperationsActionStore(clock);
        var actionOptions = new OperationsActionOptions();
        var tasks = new OperationsTaskService(approvals, compensations, new EmptyExecutionService(), atlas,
            new ToolApprovalOptions(), new ToolCompensationOptions(), new ToolExecutionReconciliationOptions(),
            actionStore, actionOptions, clock);
        var actions = new OperationsActionService(actionStore, tasks, approvals, compensations,
            new ToolApprovalOptions(), new ToolCompensationOptions(), actionOptions, clock);

        var task = Assert.Single((await tasks.ListAsync(owner, "incident", null, null, 10)).Items);
        var record = new OperationsActionRecord("incident-escalation", "tenant-a", "incident", task.Id,
            "escalate", task.ETag, new string('A', 64), new string('B', 64), owner.SubjectId,
            new string('C', 64), OperationsActionStatus.AwaitingReview, 1, now, now.AddMinutes(15));
        await actionStore.CreateAsync(record, 100);
        var forbidden = await Assert.ThrowsAsync<AtlasIncidentWorkflowException>(() =>
            atlas.GetAsync(checkpoint.RunId, reviewer));
        clock.Advance(TimeSpan.FromMinutes(2));
        var conflict = await Assert.ThrowsAsync<OperationsActionException>(() => actions.ReviewAsync(record.Id,
            record.Version, OperationsActionService.ETag(record), true, "独立确认升级", reviewer));
        var after = (await atlas.GetAsync(checkpoint.RunId, owner))!;
        var audit = Assert.Single(await actionStore.ListAsync("tenant-a", 10));

        Assert.Equal("ATLAS_RUN_FORBIDDEN", forbidden.Code);
        Assert.Equal("OPERATIONS_TARGET_VERSION_CONFLICT", conflict.Code);
        Assert.Equal(AtlasIncidentStatus.Expired, after.Status);
        Assert.Equal(2, after.Version);
        Assert.Equal(OperationsActionStatus.Failed, audit.Status);
        Assert.Equal(0, approvals.DecideCount);
    }

    [Fact]
    public async Task AuditListIsFilteredByActionDomainRoles()
    {
        var fixture = Fixture.Create();
        var now = fixture.Clock.GetUtcNow();
        OperationsActionRecord Record(string id, string targetType, string action, char marker) => new(
            id, "tenant-a", targetType, $"target-{id}", action, "\"etag\"", new string(marker, 64),
            new string((char)(marker + 1), 64), "requester", new string((char)(marker + 2), 64),
            OperationsActionStatus.Completed, 3, now, now.AddMinutes(15), "reviewer",
            new string((char)(marker + 3), 64), now, now, "completed");
        await fixture.Store.CreateAsync(Record("approval-action", "approval", "approve", 'A'), 100);
        await fixture.Store.CreateAsync(Record("compensation-action", "compensation", "reject", 'F'), 100);
        await fixture.Store.CreateAsync(Record("escalation-action", "incident", "escalate", 'K'), 100);
        var escalatorOnly = AccessContext.Create("tenant-a", "escalator", ["operations-escalators"]);
        var reviewerOnly = AccessContext.Create("tenant-a", "reviewer", ["tool-approvers"]);
        var outsider = AccessContext.Create("tenant-a", "reader", ["readers"]);

        var escalationAudit = await fixture.Actions.ListAsync(escalatorOnly);
        var workflowAudit = await fixture.Actions.ListAsync(reviewerOnly);
        var denied = await Assert.ThrowsAsync<OperationsActionException>(() => fixture.Actions.ListAsync(outsider));
        var approvalHidden = await Assert.ThrowsAsync<OperationsActionException>(() =>
            fixture.Actions.GetAsync("approval-action", escalatorOnly));
        var escalationHidden = await Assert.ThrowsAsync<OperationsActionException>(() =>
            fixture.Actions.GetAsync("escalation-action", reviewerOnly));

        Assert.Equal("escalation-action", Assert.Single(escalationAudit).Id);
        Assert.Equal(["approval-action", "compensation-action"],
            workflowAudit.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("OPERATIONS_REVIEWER_ROLE_REQUIRED", denied.Code);
        Assert.Equal("OPERATIONS_ACTION_NOT_FOUND", approvalHidden.Code);
        Assert.Equal("OPERATIONS_ACTION_NOT_FOUND", escalationHidden.Code);
    }

    [Fact]
    public async Task UnknownUnderlyingOutcomeIsPersistedAndTargetCannotBeReplayed()
    {
        var fixture = Fixture.Create();
        fixture.Approvals.ThrowAfterDecision = true;
        var task = Assert.Single((await fixture.Tasks.ListAsync(fixture.OperatorA, "approval", null, null, 10)).Items);
        var request = await fixture.Actions.RequestAsync("approval", task.Id, "approve", task.ETag,
            "故障注入", "unknown-key-01", fixture.OperatorA);

        var unknown = await Assert.ThrowsAsync<OperationsActionException>(() => fixture.Actions.ReviewAsync(
            request.Id, request.Version, request.ETag, true, "执行后连接中断", fixture.OperatorB));
        var audit = Assert.Single(await fixture.Store.ListAsync("tenant-a", 10));
        var queue = await fixture.Tasks.ListAsync(fixture.OperatorA, null, null, null, 10);
        var target = Assert.Single(queue.Items, item => item.Type == "approval");
        var frozen = Assert.Single(queue.Items, item => item.Type == "action-review");

        Assert.Equal("OPERATIONS_ACTION_OUTCOME_UNKNOWN", unknown.Code);
        Assert.Equal(OperationsActionStatus.OutcomeUnknown, audit.Status);
        Assert.Empty(target.AllowedActions);
        Assert.Equal("OutcomeUnknown", frozen.Status);
        Assert.Empty(frozen.AllowedActions);
        Assert.Equal(1, fixture.Approvals.DecideCount);
    }

    [Fact]
    public async Task CompensationDecisionAlsoUsesPersistentTwoPersonAction()
    {
        var fixture = Fixture.CreateWithCompensation();
        var task = Assert.Single((await fixture.Tasks.ListAsync(
            fixture.OperatorA, "compensation", null, null, 10)).Items);
        var request = await fixture.Actions.RequestAsync("compensation", task.Id, "reject", task.ETag,
            "提出拒绝反向操作", "comp-key-0001", fixture.OperatorA);
        var completed = await fixture.Actions.ReviewAsync(request.Id, request.Version, request.ETag, true,
            "独立确认拒绝", fixture.OperatorB);

        Assert.Equal(OperationsActionStatus.Completed, completed.Status);
        Assert.Equal("OPERATIONS_COMPENSATION_REJECTED", completed.OutcomeCode);
        Assert.Equal(1, fixture.Compensations.DecideCount);
        Assert.False(fixture.Compensations.Decision);
    }

    [Fact]
    public async Task CrashedExecutingActionExpiresToOutcomeUnknownWithoutTakeover()
    {
        var fixture = Fixture.Create();
        var now = fixture.Clock.GetUtcNow();
        var record = new OperationsActionRecord("crashed-action", "tenant-a", "approval", "approval-1", "approve",
            "\"etag\"", new string('A', 64), new string('B', 64), "operator-a", new string('C', 64),
            OperationsActionStatus.AwaitingReview, 1, now, now.AddMinutes(15));
        await fixture.Store.CreateAsync(record, 100);
        var acquired = await fixture.Store.TryAcquireReviewAsync("tenant-a", record.Id, 1, "operator-b", true,
            new string('D', 64), now);
        Assert.Equal(OperationsActionAcquireStatus.Acquired, acquired.Status);

        fixture.Clock.Advance(TimeSpan.FromMinutes(16));
        var frozen = Assert.Single(await fixture.Store.ListTaskStateAsync("tenant-a"));
        var takeover = await fixture.Store.TryAcquireReviewAsync("tenant-a", record.Id, frozen.Version,
            "operator-c", true, new string('E', 64), fixture.Clock.GetUtcNow());

        Assert.Equal(OperationsActionStatus.OutcomeUnknown, frozen.Status);
        Assert.Equal("OPERATIONS_ACTION_EXECUTION_EXPIRED_OUTCOME_UNKNOWN", frozen.OutcomeCode);
        Assert.Equal(OperationsActionAcquireStatus.VersionConflict, takeover.Status);

        var pendingNow = fixture.Clock.GetUtcNow();
        var pending = record with
        {
            Id = "expired-pending",
            TargetId = "approval-2",
            RequestFingerprint = new string('1', 64),
            IdempotencyHash = new string('2', 64),
            CreatedAt = pendingNow,
            ExpiresAt = pendingNow.AddMinutes(1)
        };
        await fixture.Store.CreateAsync(pending, 100);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var expired = await fixture.Store.TryAcquireReviewAsync("tenant-a", pending.Id, 1, "operator-b", true,
            new string('3', 64), fixture.Clock.GetUtcNow());
        Assert.Equal(OperationsActionAcquireStatus.Expired, expired.Status);
    }

    [Fact]
    public async Task CapacityIsTenantScopedAndArchivedUnknownOutcomeKeepsTargetFrozenAndAuditReadable()
    {
        var now = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var options = new OperationsActionOptions
        {
            MaximumEntries = 1,
            MaximumAuditEntriesPerTenant = 2,
            OutcomeUnknownRetention = TimeSpan.FromMinutes(1)
        };
        var store = new InMemoryOperationsActionStore(clock, options);
        OperationsActionRecord Record(string id, string tenant, string target, char marker) => new(id, tenant,
            "approval", target, "approve", "\"etag\"", new string(marker, 64),
            new string((char)(marker + 1), 64), "requester", new string((char)(marker + 2), 64),
            OperationsActionStatus.AwaitingReview, 1, now, now.AddMinutes(15));
        var first = Record("unknown-a", "tenant-a", "target-a", 'A');
        var otherTenant = Record("pending-b", "tenant-b", "target-b", 'F');
        Assert.Equal(OperationsActionCreateStatus.Created, (await store.CreateAsync(first, 1)).Status);
        Assert.Equal(OperationsActionCreateStatus.Created, (await store.CreateAsync(otherTenant, 1)).Status);
        var acquired = await store.TryAcquireReviewAsync("tenant-a", first.Id, 1, "reviewer", true,
            new string('D', 64), now);
        await store.CompleteAsync("tenant-a", first.Id, acquired.Record!.Version,
            OperationsActionStatus.OutcomeUnknown, "OPERATIONS_ACTION_OUTCOME_UNKNOWN", now);
        Assert.Equal(OperationsActionCreateStatus.Created,
            (await store.CreateAsync(Record("pending-a", "tenant-a", "target-a-2", 'K'), 1)).Status);

        clock.Advance(TimeSpan.FromMinutes(2));
        var archived = Assert.Single(await store.ListAsync("tenant-a", 10), item => item.Id == first.Id);
        var taskState = Assert.Single(await store.ListTaskStateAsync("tenant-a"), item => item.Id == first.Id);
        var sameTarget = await store.CreateAsync(Record("replay-a", "tenant-a", "target-a", 'P'), 10);
        var auditCapacity = await store.CreateAsync(Record("capacity-a", "tenant-a", "target-a-3", 'U'), 10);

        Assert.Equal(OperationsActionStatus.OutcomeUnknownArchived, archived.Status);
        Assert.Equal("OPERATIONS_ACTION_OUTCOME_UNKNOWN", archived.OutcomeCode);
        Assert.Equal(OperationsActionStatus.OutcomeUnknownArchived, taskState.Status);
        Assert.Equal(OperationsActionCreateStatus.TargetBusy, sameTarget.Status);
        Assert.Equal(OperationsActionCreateStatus.Capacity, auditCapacity.Status);
    }

    private sealed class Fixture
    {
        private readonly ToolApprovalOptions _approvalOptions = new();
        private readonly ToolCompensationOptions _compensationOptions = new();
        private readonly ToolExecutionReconciliationOptions _executionOptions = new();
        private readonly OperationsActionOptions _actionOptions = new();
        private readonly EmptyCompensationService _compensations;
        private readonly EmptyExecutionService _executions = new();
        private readonly InMemoryAtlasIncidentStore _atlas;

        private Fixture(ManualTimeProvider clock, ApprovalService approvals, InMemoryOperationsActionStore store,
            EmptyCompensationService? compensations = null)
        {
            Clock = clock; Approvals = approvals; Store = store; _atlas = new(clock);
            _compensations = compensations ?? new EmptyCompensationService();
            Tasks = ReconstructTasks();
            Actions = new OperationsActionService(store, Tasks, approvals, _compensations, _approvalOptions,
                _compensationOptions, _actionOptions, clock);
        }

        public ManualTimeProvider Clock { get; }
        public ApprovalService Approvals { get; }
        public InMemoryOperationsActionStore Store { get; }
        public EmptyCompensationService Compensations => _compensations;
        public OperationsTaskService Tasks { get; }
        public OperationsActionService Actions { get; }
        public AccessContext OperatorA { get; } = AccessContext.Create("tenant-a", "operator-a",
            ["tool-approvers", "operations-escalators"]);
        public AccessContext OperatorB { get; } = AccessContext.Create("tenant-a", "operator-b",
            ["tool-approvers", "operations-escalators"]);

        public OperationsTaskService ReconstructTasks() => new(Approvals, _compensations, _executions, _atlas,
            _approvalOptions, _compensationOptions, _executionOptions, Store, _actionOptions, Clock);

        public static Fixture Create(bool overdue = false)
        {
            var now = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
            var clock = new ManualTimeProvider(now);
            var approval = new ToolApprovalRequest("approval-1", "tenant-a", "tool-requester", "memory.delete",
                ToolOperationRisk.Mutation, new string('F', 64), ["memoryId"], "不进入工作台", now.AddMinutes(-10),
                overdue ? now.AddMinutes(-1) : now.AddMinutes(10), ToolApprovalStatus.Pending);
            return new Fixture(clock, new ApprovalService(approval, clock), new InMemoryOperationsActionStore(clock));
        }

        public static Fixture CreateWithCompensation()
        {
            var now = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
            var clock = new ManualTimeProvider(now);
            var approval = new ToolApprovalRequest("unused", "tenant-a", "tool-requester", "memory.delete",
                ToolOperationRisk.Mutation, new string('F', 64), ["memoryId"], "unused", now,
                now.AddMinutes(15), ToolApprovalStatus.Rejected);
            var compensation = new ToolCompensationSummary("compensation-1", "memory.correct", "memory.restore",
                ToolCompensationStatus.AwaitingApproval, now, now.AddMinutes(15), "reverse-approval");
            return new Fixture(clock, new ApprovalService(approval, clock),
                new InMemoryOperationsActionStore(clock), new EmptyCompensationService(compensation));
        }
    }

    private sealed class ApprovalService(ToolApprovalRequest initial, TimeProvider clock) : IToolApprovalService
    {
        public ToolApprovalRequest Current { get; private set; } = initial;
        public int DecideCount { get; private set; }
        public bool ThrowAfterDecision { get; set; }
        public string LastReason { get; private set; } = string.Empty;
        public void MoveTo(ToolApprovalStatus status) => Current = Current with
        {
            Status = status,
            DecidedAt = clock.GetUtcNow()
        };
        public Task<IReadOnlyList<ToolApprovalRequest>> ListAsync(AccessContext access,
            ToolApprovalStatus? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolApprovalRequest>>(status is null || Current.Status == status
                ? [Current] : []);
        public Task<ToolApprovalRequest> DecideAsync(string approvalId, bool approved, string reason,
            AccessContext approver, CancellationToken cancellationToken = default)
        {
            if (Current.Status != ToolApprovalStatus.Pending)
                throw new ToolApprovalException("TOOL_APPROVAL_ALREADY_DECIDED", "审批已经变化。",
                    ToolApprovalErrorKind.Conflict);
            DecideCount++;
            LastReason = reason;
            Current = Current with
            {
                Status = approved ? ToolApprovalStatus.Approved : ToolApprovalStatus.Rejected,
                ApproverSubjectId = approver.SubjectId,
                DecidedAt = clock.GetUtcNow()
            };
            if (ThrowAfterDecision) throw new InvalidOperationException("模拟底层提交后的连接中断。");
            return Task.FromResult(Current);
        }
        public Task<ToolApprovalRequest> RequestAsync(string toolName, JsonElement arguments, string justification,
            AccessContext requester, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolApprovalRequest> GetAsync(string approvalId, AccessContext access,
            CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<ToolApprovalConsumption> ConsumeAsync(string approvalId, string toolName, JsonElement arguments,
            AccessContext requester, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public sealed class EmptyCompensationService(ToolCompensationSummary? current = null) : IToolCompensationService
    {
        public bool IsAvailable => true;
        public int DecideCount { get; private set; }
        public bool Decision { get; private set; }
        public Task<IReadOnlyList<ToolCompensationSummary>> ListAsync(AccessContext access,
            ToolCompensationStatus? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolCompensationSummary>>(current is not null
                && (status is null || current.Status == status) ? [current] : []);
        public Task<ToolCompensationPreparation> PrepareForwardAsync(string executionKey, ICompensableServerTool tool,
            ToolExecutionContext context, JsonElement arguments, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ActivateAsync(ToolCompensationPreparation preparation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DiscardAsync(ToolCompensationPreparation preparation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkForwardOutcomeUnknownAsync(ToolCompensationPreparation preparation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolCompensationSummary> RequestApprovalAsync(string compensationId, string justification,
            AccessContext requester, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolCompensationSummary> DecideAsync(string compensationId, string approvalId, bool approved,
            string reason, AccessContext approver, CancellationToken cancellationToken = default)
        {
            if (current is null || current.Id != compensationId || current.ApprovalId != approvalId)
                throw new ToolCompensationException("TOOL_COMPENSATION_NOT_FOUND", "未找到补偿。",
                    ToolCompensationErrorKind.NotFound);
            DecideCount++; Decision = approved;
            current = current with
            {
                Status = approved ? ToolCompensationStatus.Approved
                : ToolCompensationStatus.Rejected
            };
            return Task.FromResult(current);
        }
        public Task<ToolCompensationExecutionResult> ExecuteAsync(string compensationId, string approvalId,
            string idempotencyKey, AccessContext requester, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyExecutionService : IToolExecutionReconciliationService
    {
        public Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(AccessContext access, int limit,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OutcomeUnknownToolExecution>>([]);
        public Task<ToolOutcomeProbeResult> ProbeOutcomeAsync(AccessContext access, string executionKey,
            JsonElement arguments, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ToolReconciliationReviewResult> ReviewOutcomeAsync(AccessContext access, string executionKey,
            JsonElement arguments, bool confirmed, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
