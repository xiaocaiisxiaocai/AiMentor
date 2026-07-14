using System.Security.Cryptography;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class ToolCompensationWorkflowTests
{
    private static readonly AccessContext Requester = AccessContext.Create("tenant-a", "user-a", ["engineers"]);
    private static readonly AccessContext Approver =
        AccessContext.Create("tenant-a", "approver-a", ["tool-approvers"]);
    private static readonly AccessContext ReconcilerA =
        AccessContext.Create("tenant-a", "reconciler-a", ["tool-reconcilers"]);
    private static readonly AccessContext ReconcilerB =
        AccessContext.Create("tenant-a", "reconciler-b", ["tool-reconcilers"]);

    [Fact]
    public async Task CompensationRequiresIndependentApprovalAndReplaysWithoutSecondSideEffect()
    {
        var tool = new ReversibleTestTool();
        var service = CreateService(tool);
        var arguments = JsonSerializer.SerializeToElement(new { value = 2 });
        var preparation = await service.PrepareForwardAsync(new string('A', 64), tool,
            new ToolExecutionContext(Requester, "forward-run"), arguments);
        await tool.ExecuteAsync(new ToolExecutionContext(Requester, "forward-run"), arguments);
        await service.ActivateAsync(preparation);

        var pending = await service.RequestApprovalAsync(preparation.Id, "恢复测试状态", Requester);
        var requesterWithApproverRole = AccessContext.Create(Requester.TenantId, Requester.SubjectId,
            ["tool-approvers"]);
        var selfDecision = await Assert.ThrowsAsync<ToolCompensationException>(() => service.DecideAsync(
            preparation.Id, pending.ApprovalId!, true, "自行批准", requesterWithApproverRole));
        var approved = await service.DecideAsync(preparation.Id, pending.ApprovalId!, true, "独立批准", Approver);
        var completed = await service.ExecuteAsync(preparation.Id, approved.ApprovalId!, "reverse-key-1", Requester);
        var replay = await service.ExecuteAsync(preparation.Id, approved.ApprovalId!, "reverse-key-1", Requester);

        Assert.Equal("TOOL_COMPENSATION_SELF_DECISION_DENIED", selfDecision.Code);
        Assert.Equal(ToolCompensationStatus.Completed, completed.Status);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(1, tool.State);
        Assert.Equal(1, tool.CompensationCount);
        Assert.DoesNotContain("previousValue", JsonSerializer.Serialize(Assert.Single(await service.ListAsync(Requester))),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompensationFailureAfterInvocationFreezesOutcomeUnknownAndRejectsRetry()
    {
        var tool = new ReversibleTestTool(throwAfterCompensation: true);
        var service = CreateService(tool);
        var arguments = JsonSerializer.SerializeToElement(new { value = 2 });
        var preparation = await service.PrepareForwardAsync(new string('B', 64), tool,
            new ToolExecutionContext(Requester, "forward-run"), arguments);
        await tool.ExecuteAsync(new ToolExecutionContext(Requester, "forward-run"), arguments);
        await service.ActivateAsync(preparation);
        var pending = await service.RequestApprovalAsync(preparation.Id, "验证结果不确定冻结", Requester);
        await service.DecideAsync(preparation.Id, pending.ApprovalId!, true, "独立批准", Approver);

        var uncertain = await Assert.ThrowsAsync<ToolCompensationException>(() => service.ExecuteAsync(
            preparation.Id, pending.ApprovalId!, "reverse-key-2", Requester));
        var retry = await Assert.ThrowsAsync<ToolCompensationException>(() => service.ExecuteAsync(
            preparation.Id, pending.ApprovalId!, "reverse-key-2", Requester));

        Assert.Equal("TOOL_COMPENSATION_OUTCOME_UNKNOWN", uncertain.Code);
        Assert.Equal("TOOL_COMPENSATION_OUTCOME_UNKNOWN", retry.Code);
        Assert.Equal(ToolCompensationStatus.OutcomeUnknown,
            Assert.Single(await service.ListAsync(Requester)).Status);
        Assert.Single(await service.ListAsync(Requester, ToolCompensationStatus.OutcomeUnknown));
        Assert.Empty(await service.ListAsync(Requester, ToolCompensationStatus.Completed));
        Assert.Equal(1, tool.CompensationCount);
    }

    [Fact]
    public async Task CompensationBarrierRunsAfterExecutingAndBeforeReverseSideEffect()
    {
        var tool = new ReversibleTestTool();
        var barrier = new RecordingExecutionBarrier();
        var service = CreateService(tool, barrier: barrier);
        var arguments = JsonSerializer.SerializeToElement(new { value = 2 });
        var preparation = await service.PrepareForwardAsync(new string('E', 64), tool,
            new ToolExecutionContext(Requester, "forward-run"), arguments);
        await tool.ExecuteAsync(new ToolExecutionContext(Requester, "forward-run"), arguments);
        await service.ActivateAsync(preparation);
        var pending = await service.RequestApprovalAsync(preparation.Id, "验证反向执行屏障", Requester);
        await service.DecideAsync(preparation.Id, pending.ApprovalId!, true, "独立批准", Approver);

        var execution = service.ExecuteAsync(preparation.Id, pending.ApprovalId!, "barrier-reverse-key", Requester);
        var signal = await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("test.state.restore", signal.ToolName);
        Assert.Equal(64, signal.ExecutionKey.Length);
        Assert.Equal(ToolCompensationStatus.Executing,
            Assert.Single(await service.ListAsync(Requester, ToolCompensationStatus.Executing)).Status);
        Assert.Equal(0, tool.CompensationCount);

        barrier.Release.TrySetResult();
        Assert.Equal(ToolCompensationStatus.Completed, (await execution).Status);
        Assert.Equal(1, tool.CompensationCount);
    }

    [Fact]
    public async Task CompensationListRejectsUndefinedStatusFilter()
    {
        var invalid = await Assert.ThrowsAsync<ToolCompensationException>(() =>
            CreateService(new ReversibleTestTool()).ListAsync(Requester, (ToolCompensationStatus)255));

        Assert.Equal("TOOL_COMPENSATION_STATUS_INVALID", invalid.Code);
        Assert.Equal(ToolCompensationErrorKind.Validation, invalid.Kind);
    }

    [Fact]
    public async Task MemoryCorrectionRestoresOnlyTheVersionProducedByItsForwardExecution()
    {
        var memoryStore = new InMemoryMemoryStore();
        var workflow = new MemoryWorkflowService(memoryStore,
            new RuleBasedMemoryContentSafetyService(), new InMemoryTraceSink(), TimeProvider.System,
            new MemoryWorkflowOptions());
        var proposal = await workflow.ProposeAsync(new ProposeMemoryCommand(MemoryScope.UserPreference,
            "answer.format", "列表"), Requester);
        var memory = await workflow.ApproveAsync(proposal.Id, Requester);
        var tool = new MemoryCorrectTool(workflow, memoryStore, TimeProvider.System);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            memoryId = memory.Id,
            expectedVersion = memory.Version,
            value = "表格",
            expiresAt = memory.ExpiresAt.AddDays(-1)
        });

        var snapshot = await tool.CaptureCompensationStateAsync(new ToolExecutionContext(Requester, "forward"),
            arguments);
        await tool.ExecuteAsync(new ToolExecutionContext(Requester, "forward"), arguments);
        await tool.CompensateAsync(new ToolExecutionContext(Requester, "reverse"), snapshot);
        var restored = Assert.Single(await workflow.ListAsync(Requester));

        Assert.Equal("列表", restored.Value);
        Assert.Equal(3, restored.Version);
        Assert.Equal(memory.ExpiresAt, restored.ExpiresAt);
    }

    [Fact]
    public async Task CompensableMutationFailsClosedWhenDurableCompensationStoreIsUnavailable()
    {
        var tool = new ReversibleTestTool();
        var registry = new ServerToolRegistry([tool]);
        var safety = new RuleBasedToolInvocationSafetyService(new ToolSafetyOptions
        {
            AllowedTools = new HashSet<string>([tool.Descriptor.Name], StringComparer.OrdinalIgnoreCase)
        });
        var executor = new SafeToolExecutor(registry, safety, new InMemoryTraceSink(), new ToolExecutorOptions(),
            TimeProvider.System, compensationService: new UnavailableToolCompensationService());

        var result = await executor.ExecuteAsync(tool.Descriptor.Name,
            JsonSerializer.SerializeToElement(new { value = 2 }), Requester, "forward-key");

        Assert.Equal(ToolExecutionStatus.Rejected, result.Status);
        Assert.Equal("TOOL_COMPENSATION_DURABLE_STORE_UNAVAILABLE", result.Safety.Code);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task SafeExecutorPublishesCompensationOnlyAfterForwardExecutionCompletes()
    {
        var tool = new ReversibleTestTool();
        var registry = new ServerToolRegistry([tool]);
        var safety = new RuleBasedToolInvocationSafetyService(new ToolSafetyOptions
        {
            AllowedTools = new HashSet<string>([tool.Descriptor.Name], StringComparer.OrdinalIgnoreCase)
        });
        var trace = new InMemoryTraceSink();
        var approvals = new InMemoryToolApprovalService(registry, safety, trace, new ToolApprovalOptions(),
            TimeProvider.System);
        var compensation = CreateService(tool);
        var executor = new SafeToolExecutor(registry, safety, trace, new ToolExecutorOptions(), TimeProvider.System,
            approvals, new InMemoryToolExecutionLedger(TimeProvider.System), compensationService: compensation);
        var arguments = JsonSerializer.SerializeToElement(new { value = 2 });
        var approval = await approvals.RequestAsync(tool.Descriptor.Name, arguments, "更新测试状态", Requester);
        await approvals.DecideAsync(approval.Id, true, "独立批准正向执行", Approver);

        var result = await executor.ExecuteAsync(tool.Descriptor.Name, arguments, Requester, "forward-key-2",
            approval.Id);

        Assert.Equal(ToolExecutionStatus.Completed, result.Status);
        Assert.NotNull(result.CompensationId);
        var summary = Assert.Single(await compensation.ListAsync(Requester));
        Assert.Equal(result.CompensationId, summary.Id);
        Assert.Equal(ToolCompensationStatus.Available, summary.Status);
        Assert.Equal(2, tool.State);
    }

    [Fact]
    public async Task ApprovedCompensationExpiresBeforeItCanBeConsumed()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero));
        var tool = new ReversibleTestTool();
        var service = CreateService(tool, clock, new ToolCompensationOptions
        {
            ApprovalLifetime = TimeSpan.FromMinutes(1)
        });
        var arguments = JsonSerializer.SerializeToElement(new { value = 2 });
        var preparation = await service.PrepareForwardAsync(new string('C', 64), tool,
            new ToolExecutionContext(Requester, "forward-run"), arguments);
        await tool.ExecuteAsync(new ToolExecutionContext(Requester, "forward-run"), arguments);
        await service.ActivateAsync(preparation);
        var pending = await service.RequestApprovalAsync(preparation.Id, "验证批准过期", Requester);
        await service.DecideAsync(preparation.Id, pending.ApprovalId!, true, "独立批准", Approver);
        clock.Advance(TimeSpan.FromMinutes(2));

        var expired = await Assert.ThrowsAsync<ToolCompensationException>(() => service.ExecuteAsync(
            preparation.Id, pending.ApprovalId!, "expired-reverse-key", Requester));

        Assert.Equal("TOOL_COMPENSATION_APPROVAL_REQUIRED", expired.Code);
        Assert.Equal(ToolCompensationStatus.Available,
            Assert.Single(await service.ListAsync(Requester)).Status);
        Assert.Equal(0, tool.CompensationCount);
    }

    [Fact]
    public async Task InvalidLeaseConfigurationFailsBeforeReverseToolStarts()
    {
        var tool = new ReversibleTestTool();
        var service = CreateService(tool, options: new ToolCompensationOptions
        {
            ExecutionLeaseDuration = TimeSpan.FromSeconds(1)
        });
        var arguments = JsonSerializer.SerializeToElement(new { value = 2 });
        var preparation = await service.PrepareForwardAsync(new string('D', 64), tool,
            new ToolExecutionContext(Requester, "forward-run"), arguments);
        await tool.ExecuteAsync(new ToolExecutionContext(Requester, "forward-run"), arguments);
        await service.ActivateAsync(preparation);
        var pending = await service.RequestApprovalAsync(preparation.Id, "验证错误租约配置", Requester);
        await service.DecideAsync(preparation.Id, pending.ApprovalId!, true, "独立批准", Approver);

        var invalid = await Assert.ThrowsAsync<ToolCompensationException>(() => service.ExecuteAsync(
            preparation.Id, pending.ApprovalId!, "invalid-lease-key", Requester));

        Assert.Equal("TOOL_COMPENSATION_CONFIGURATION_INVALID", invalid.Code);
        Assert.Equal(ToolCompensationStatus.Approved,
            Assert.Single(await service.ListAsync(Requester)).Status);
        Assert.Equal(0, tool.CompensationCount);
    }

    [Fact]
    public async Task MemoryRestoreOutcomeUnknownRequiresTwoDifferentReconcilersToResolveApplied()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero));
        var memoryStore = new InMemoryMemoryStore();
        var workflow = new MemoryWorkflowService(memoryStore,
            new RuleBasedMemoryContentSafetyService(), new InMemoryTraceSink(), clock,
            new MemoryWorkflowOptions());
        var memory = await CreateMemoryAsync(workflow);
        var tool = new ThrowAfterMemoryRestoreTool(new MemoryCorrectTool(workflow, memoryStore, clock));
        var probe = new MemoryCorrectRestoreOutcomeProbe(memoryStore, clock);
        var service = CreateService(tool, clock, outcomeProbes: [probe]);
        var preparation = await PrepareApprovedCorrectionAsync(service, tool, memory);

        await Assert.ThrowsAsync<ToolCompensationException>(() => service.ExecuteAsync(
            preparation.Id, preparation.ApprovalId, "uncertain-restore", Requester));
        var forbidden = await Assert.ThrowsAsync<ToolCompensationException>(() =>
            service.ProbeOutcomeAsync(preparation.Id, Requester));
        var foreignTenant = await Assert.ThrowsAsync<ToolCompensationException>(() =>
            service.ProbeOutcomeAsync(preparation.Id,
                AccessContext.Create("tenant-b", "reconciler-x", ["tool-reconcilers"])));
        var evidence = await service.ProbeOutcomeAsync(preparation.Id, ReconcilerA);
        var first = await service.ReviewOutcomeAsync(preparation.Id, true, "目标已恢复", ReconcilerA);
        var sameReviewer = await service.ReviewOutcomeAsync(preparation.Id, true, "再次确认", ReconcilerA);
        var second = await service.ReviewOutcomeAsync(preparation.Id, true, "独立确认", ReconcilerB);

        Assert.Equal("TOOL_COMPENSATION_RECONCILER_ROLE_REQUIRED", forbidden.Code);
        Assert.Equal("TOOL_COMPENSATION_OUTCOME_UNKNOWN_NOT_FOUND", foreignTenant.Code);
        Assert.Equal(ToolOutcomeProbeState.Applied, evidence.State);
        Assert.Equal(ToolReconciliationReviewStatus.AwaitingSecondReviewer, first.Status);
        Assert.Equal(ToolReconciliationReviewStatus.ReviewerMustDiffer, sameReviewer.Status);
        Assert.Equal(ToolReconciliationReviewStatus.ResolvedApplied, second.Status);
        Assert.Equal(ToolCompensationStatus.Completed,
            Assert.Single(await service.ListAsync(Requester)).Status);
    }

    [Fact]
    public async Task NotAppliedResolutionClearsOldApprovalAndRequiresANewApproval()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero));
        var memoryStore = new InMemoryMemoryStore();
        var workflow = new MemoryWorkflowService(memoryStore,
            new RuleBasedMemoryContentSafetyService(), new InMemoryTraceSink(), clock,
            new MemoryWorkflowOptions());
        var memory = await CreateMemoryAsync(workflow);
        var tool = new MemoryCorrectTool(workflow, memoryStore, clock);
        var service = CreateService(tool, clock, barrier: new ThrowBeforeSideEffectBarrier(),
            outcomeProbes: [new MemoryCorrectRestoreOutcomeProbe(memoryStore, clock)]);
        var preparation = await PrepareApprovedCorrectionAsync(service, tool, memory);

        await Assert.ThrowsAsync<ToolCompensationException>(() => service.ExecuteAsync(
            preparation.Id, preparation.ApprovalId, "old-reverse-key", Requester));
        Assert.Equal(ToolOutcomeProbeState.NotApplied,
            (await service.ProbeOutcomeAsync(preparation.Id, ReconcilerA)).State);
        await service.ReviewOutcomeAsync(preparation.Id, true, "确认未恢复", ReconcilerA);
        var resolved = await service.ReviewOutcomeAsync(preparation.Id, true, "独立确认未恢复", ReconcilerB);
        var oldApproval = await Assert.ThrowsAsync<ToolCompensationException>(() => service.ExecuteAsync(
            preparation.Id, preparation.ApprovalId, "old-reverse-key", Requester));

        Assert.Equal(ToolReconciliationReviewStatus.RetryAuthorized, resolved.Status);
        var summary = Assert.Single(await service.ListAsync(Requester));
        Assert.Equal(ToolCompensationStatus.Available, summary.Status);
        Assert.Null(summary.ApprovalId);
        Assert.Equal("TOOL_COMPENSATION_APPROVAL_REQUIRED", oldApproval.Code);
    }

    [Fact]
    public async Task ExpiredCompensationEvidenceCannotBeCombinedWithSecondReview()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero));
        var memoryStore = new InMemoryMemoryStore();
        var workflow = new MemoryWorkflowService(memoryStore,
            new RuleBasedMemoryContentSafetyService(), new InMemoryTraceSink(), clock,
            new MemoryWorkflowOptions());
        var memory = await CreateMemoryAsync(workflow);
        var tool = new MemoryCorrectTool(workflow, memoryStore, clock);
        var service = CreateService(tool, clock, new ToolCompensationOptions
        {
            ReconciliationEvidenceLifetime = TimeSpan.FromMinutes(1)
        }, new ThrowBeforeSideEffectBarrier(), [new MemoryCorrectRestoreOutcomeProbe(memoryStore, clock)]);
        var preparation = await PrepareApprovedCorrectionAsync(service, tool, memory);
        await Assert.ThrowsAsync<ToolCompensationException>(() => service.ExecuteAsync(
            preparation.Id, preparation.ApprovalId, "expiring-evidence", Requester));
        await service.ReviewOutcomeAsync(preparation.Id, true, "第一人确认", ReconcilerA);
        clock.Advance(TimeSpan.FromMinutes(2));

        var expired = await service.ReviewOutcomeAsync(preparation.Id, true, "第二人迟到", ReconcilerB);

        Assert.Equal(ToolReconciliationReviewStatus.EvidenceExpired, expired.Status);
        Assert.Equal(ToolCompensationStatus.OutcomeUnknown,
            Assert.Single(await service.ListAsync(Requester)).Status);
    }

    [Theory]
    [InlineData(true, false, ToolOutcomeProbeState.Applied)]
    [InlineData(false, true, ToolOutcomeProbeState.Applied)]
    [InlineData(false, false, (ToolOutcomeProbeState)255)]
    public async Task InvalidProbeIdentityOrStateCannotResolveAnotherCompensation(
        bool replaceId, bool replaceTool, ToolOutcomeProbeState state)
    {
        var tool = new ReversibleTestTool(throwAfterCompensation: true);
        var probe = new InvalidCompensationProbe(tool.CompensationToolName, replaceId, replaceTool, state);
        var service = CreateService(tool, outcomeProbes: [probe]);
        var arguments = JsonSerializer.SerializeToElement(new { value = 2 });
        var preparation = await service.PrepareForwardAsync(new string('E', 64), tool,
            new ToolExecutionContext(Requester, "forward"), arguments);
        await tool.ExecuteAsync(new ToolExecutionContext(Requester, "forward"), arguments);
        await service.ActivateAsync(preparation);
        var pending = await service.RequestApprovalAsync(preparation.Id, "制造结果不确定", Requester);
        await service.DecideAsync(preparation.Id, pending.ApprovalId!, true, "批准测试", Approver);
        await Assert.ThrowsAsync<ToolCompensationException>(() => service.ExecuteAsync(
            preparation.Id, pending.ApprovalId!, "invalid-probe", Requester));

        var failure = await Assert.ThrowsAsync<ToolCompensationException>(() =>
            service.ProbeOutcomeAsync(preparation.Id, ReconcilerA));

        Assert.Equal("TOOL_COMPENSATION_PROBE_RESULT_INVALID", failure.Code);
        Assert.Equal(ToolCompensationStatus.OutcomeUnknown,
            Assert.Single(await service.ListAsync(Requester)).Status);
    }

    private static InMemoryToolCompensationService CreateService(IServerTool tool, TimeProvider? timeProvider = null,
        ToolCompensationOptions? options = null, IToolExecutionBarrier? barrier = null,
        IEnumerable<IToolCompensationOutcomeProbe>? outcomeProbes = null)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var cipher = new AesGcmWorkflowStateCipher("v1", new Dictionary<string, byte[]> { ["v1"] = key });
        return new InMemoryToolCompensationService(new ServerToolRegistry([tool]), cipher, new InMemoryTraceSink(),
            options ?? new ToolCompensationOptions(), timeProvider ?? TimeProvider.System, barrier, outcomeProbes);
    }

    private static async Task<MemoryRecord> CreateMemoryAsync(MemoryWorkflowService workflow)
    {
        var proposal = await workflow.ProposeAsync(new ProposeMemoryCommand(MemoryScope.UserPreference,
            "answer.format", "列表"), Requester);
        return await workflow.ApproveAsync(proposal.Id, Requester);
    }

    private static async Task<(string Id, string ApprovalId)> PrepareApprovedCorrectionAsync(
        InMemoryToolCompensationService service, ICompensableServerTool tool, MemoryRecord memory)
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            memoryId = memory.Id,
            expectedVersion = memory.Version,
            value = "表格"
        });
        var preparation = await service.PrepareForwardAsync(new string('F', 64), tool,
            new ToolExecutionContext(Requester, "forward"), arguments);
        await tool.ExecuteAsync(new ToolExecutionContext(Requester, "forward"), arguments);
        await service.ActivateAsync(preparation);
        var pending = await service.RequestApprovalAsync(preparation.Id, "恢复旧值", Requester);
        await service.DecideAsync(preparation.Id, pending.ApprovalId!, true, "批准恢复", Approver);
        return (preparation.Id, pending.ApprovalId!);
    }

    /// <summary>阻塞在反向副作用前，并向测试公开账本已经进入 Executing 的确定信号。</summary>
    private sealed class RecordingExecutionBarrier : IToolExecutionBarrier
    {
        public TaskCompletionSource<(string ExecutionKey, string ToolName)> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAfterExecutingAsync(string executionKey, string toolName,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult((executionKey, toolName));
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>模拟反向账本进入 Executing 后、目标副作用开始前实例失联。</summary>
    private sealed class ThrowBeforeSideEffectBarrier : IToolExecutionBarrier
    {
        public Task WaitAfterExecutingAsync(string executionKey, string toolName,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("模拟反向副作用前实例失联");
    }

    /// <summary>委托真实记忆恢复后模拟响应丢失，使目标已生效但账本保持结果不确定。</summary>
    private sealed class ThrowAfterMemoryRestoreTool(MemoryCorrectTool inner) : ICompensableServerTool
    {
        public ToolDescriptor Descriptor => inner.Descriptor;
        public string CompensationToolName => inner.CompensationToolName;
        public SafetyDecision ValidateArguments(JsonElement arguments) => inner.ValidateArguments(arguments);
        public Task<JsonElement> CaptureCompensationStateAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            inner.CaptureCompensationStateAsync(context, arguments, cancellationToken);
        public Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) => inner.ExecuteAsync(context, arguments, cancellationToken);
        public async Task<JsonElement> CompensateAsync(ToolExecutionContext context, JsonElement compensationState,
            CancellationToken cancellationToken = default)
        {
            await inner.CompensateAsync(context, compensationState, cancellationToken);
            throw new InvalidOperationException("模拟恢复提交后响应丢失");
        }
    }

    /// <summary>模拟错误或恶意专属探测器，验证编排层不会信任其身份和枚举值。</summary>
    private sealed class InvalidCompensationProbe(
        string compensationToolName,
        bool replaceId,
        bool replaceTool,
        ToolOutcomeProbeState state) : IToolCompensationOutcomeProbe
    {
        public string CompensationToolName => compensationToolName;

        public Task<ToolCompensationOutcomeProbeResult> ProbeAsync(string compensationId, AccessContext owner,
            JsonElement compensationState, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolCompensationOutcomeProbeResult(
            replaceId ? Guid.NewGuid().ToString("N") : compensationId,
            replaceTool ? "other.restore" : CompensationToolName,
            state,
            "TEST_PROBE_RESULT",
            "测试探测结果。",
            DateTimeOffset.MaxValue));
    }

    /// <summary>提供可观察状态的最小可逆工具，用于验证快照、审批、幂等和结果不确定边界。</summary>
    private sealed class ReversibleTestTool(bool throwAfterCompensation = false) : ICompensableServerTool
    {
        public int State { get; private set; } = 1;
        public int ExecutionCount { get; private set; }
        public int CompensationCount { get; private set; }
        public string CompensationToolName => "test.state.restore";
        public ToolDescriptor Descriptor { get; } = new("test.state.update", "test", ToolOperationRisk.Mutation,
            TimeSpan.FromSeconds(1), 1024, true);

        public SafetyDecision ValidateArguments(JsonElement arguments) => SafetyDecision.Allowed;

        public Task<JsonElement> CaptureCompensationStateAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { previousValue = State }));

        public Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            State = arguments.GetProperty("value").GetInt32();
            ExecutionCount++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { State }));
        }

        public Task<JsonElement> CompensateAsync(ToolExecutionContext context, JsonElement compensationState,
            CancellationToken cancellationToken = default)
        {
            State = compensationState.GetProperty("previousValue").GetInt32();
            CompensationCount++;
            if (throwAfterCompensation) throw new InvalidOperationException("模拟补偿提交后连接中断");
            return Task.FromResult(JsonSerializer.SerializeToElement(new { State }));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
