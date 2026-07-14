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
        Assert.Equal(1, tool.CompensationCount);
    }

    [Fact]
    public async Task MemoryCorrectionRestoresOnlyTheVersionProducedByItsForwardExecution()
    {
        var workflow = new MemoryWorkflowService(new InMemoryMemoryStore(),
            new RuleBasedMemoryContentSafetyService(), new InMemoryTraceSink(), TimeProvider.System,
            new MemoryWorkflowOptions());
        var proposal = await workflow.ProposeAsync(new ProposeMemoryCommand(MemoryScope.UserPreference,
            "answer.format", "列表"), Requester);
        var memory = await workflow.ApproveAsync(proposal.Id, Requester);
        var tool = new MemoryCorrectTool(workflow);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            memoryId = memory.Id,
            expectedVersion = memory.Version,
            value = "表格"
        });

        var snapshot = await tool.CaptureCompensationStateAsync(new ToolExecutionContext(Requester, "forward"),
            arguments);
        await tool.ExecuteAsync(new ToolExecutionContext(Requester, "forward"), arguments);
        await tool.CompensateAsync(new ToolExecutionContext(Requester, "reverse"), snapshot);
        var restored = Assert.Single(await workflow.ListAsync(Requester));

        Assert.Equal("列表", restored.Value);
        Assert.Equal(3, restored.Version);
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

    private static InMemoryToolCompensationService CreateService(IServerTool tool, TimeProvider? timeProvider = null,
        ToolCompensationOptions? options = null)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var cipher = new AesGcmWorkflowStateCipher("v1", new Dictionary<string, byte[]> { ["v1"] = key });
        return new InMemoryToolCompensationService(new ServerToolRegistry([tool]), cipher, new InMemoryTraceSink(),
            options ?? new ToolCompensationOptions(), timeProvider ?? TimeProvider.System);
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
