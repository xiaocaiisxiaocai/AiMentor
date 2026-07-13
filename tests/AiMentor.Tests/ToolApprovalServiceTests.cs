using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class ToolApprovalServiceTests
{
    private static readonly AccessContext Requester = AccessContext.Create("tenant-a", "requester", ["users"]);
    private static readonly AccessContext Approver = AccessContext.Create("tenant-a", "approver", ["tool-approvers"]);

    [Fact]
    public async Task ApprovedRequestShouldBindRequesterToolAndCanonicalArgumentsAndConsumeOnce()
    {
        var fixture = CreateFixture();
        var requestedArguments = JsonSerializer.SerializeToElement(new { expectedVersion = 2, memoryId = "memory-1" });
        var reorderedArguments = JsonSerializer.SerializeToElement(new { memoryId = "memory-1", expectedVersion = 2 });
        var request = await fixture.Approvals.RequestAsync("memory.delete", requestedArguments,
            "用户要求删除错误记忆", Requester);

        var selfDecision = await Assert.ThrowsAsync<ToolApprovalException>(() =>
            fixture.Approvals.DecideAsync(request.Id, true, "自行批准", Requester));
        Assert.Equal("TOOL_APPROVER_ROLE_REQUIRED", selfDecision.Code);

        var approved = await fixture.Approvals.DecideAsync(request.Id, true, "已核对删除范围", Approver);
        var first = await fixture.Approvals.ConsumeAsync(request.Id, "memory.delete", reorderedArguments, Requester);
        var replay = await fixture.Approvals.ConsumeAsync(request.Id, "memory.delete", reorderedArguments, Requester);

        Assert.Equal(ToolApprovalStatus.Approved, approved.Status);
        Assert.True(first.Allowed);
        Assert.Equal("TOOL_APPROVAL_CONSUMED", first.Decision.Code);
        Assert.False(replay.Allowed);
        Assert.Equal("TOOL_APPROVAL_ALREADY_CONSUMED", replay.Decision.Code);
        Assert.Equal(["expectedVersion", "memoryId"], request.ArgumentNames);
    }

    [Fact]
    public async Task ApprovalShouldRejectSelfDecisionEvenWhenRequesterIsApprover()
    {
        var fixture = CreateFixture();
        var dualRole = AccessContext.Create("tenant-a", "dual-role", ["tool-approvers"]);
        var request = await fixture.Approvals.RequestAsync("memory.delete", Arguments("memory-1", 1),
            "申请删除过期记忆", dualRole);

        var exception = await Assert.ThrowsAsync<ToolApprovalException>(() =>
            fixture.Approvals.DecideAsync(request.Id, true, "批准", dualRole));

        Assert.Equal("TOOL_APPROVAL_SELF_DECISION_DENIED", exception.Code);
    }

    [Fact]
    public async Task ApprovalShouldRejectOtherTenantAndChangedArguments()
    {
        var fixture = CreateFixture();
        var request = await fixture.Approvals.RequestAsync("memory.delete", Arguments("memory-1", 1),
            "申请删除错误记忆", Requester);
        var otherTenant = AccessContext.Create("tenant-b", "approver-b", ["tool-approvers"]);

        var tenantError = await Assert.ThrowsAsync<ToolApprovalException>(() =>
            fixture.Approvals.DecideAsync(request.Id, true, "批准", otherTenant));
        await fixture.Approvals.DecideAsync(request.Id, true, "已核对", Approver);
        var changed = await fixture.Approvals.ConsumeAsync(request.Id, "memory.delete",
            Arguments("memory-1", 2), Requester);

        Assert.Equal("TOOL_APPROVAL_NOT_FOUND", tenantError.Code);
        Assert.False(changed.Allowed);
        Assert.Equal("TOOL_APPROVAL_SCOPE_MISMATCH", changed.Decision.Code);
    }

    [Fact]
    public async Task RejectedApprovalShouldNotBeConsumableOrDecidedAgain()
    {
        var fixture = CreateFixture();
        var request = await fixture.Approvals.RequestAsync("memory.delete", Arguments("memory-1", 1),
            "申请删除错误记忆", Requester);

        var rejected = await fixture.Approvals.DecideAsync(request.Id, false, "删除依据不足", Approver);
        var consumption = await fixture.Approvals.ConsumeAsync(request.Id, "memory.delete",
            Arguments("memory-1", 1), Requester);
        var repeatedDecision = await Assert.ThrowsAsync<ToolApprovalException>(() =>
            fixture.Approvals.DecideAsync(request.Id, true, "重新批准", Approver));
        var invisibleToOtherUser = await fixture.Approvals.ListAsync(
            AccessContext.Create("tenant-a", "other-user", ["users"]));

        Assert.Equal(ToolApprovalStatus.Rejected, rejected.Status);
        Assert.False(consumption.Allowed);
        Assert.Equal("TOOL_APPROVAL_NOT_APPROVED", consumption.Decision.Code);
        Assert.Equal("TOOL_APPROVAL_ALREADY_DECIDED", repeatedDecision.Code);
        Assert.DoesNotContain(invisibleToOtherUser, item => item.Id == request.Id);
    }

    [Fact]
    public async Task ApprovalShouldExpireBeforeDecisionAndExecution()
    {
        var fixture = CreateFixture(TimeSpan.FromMinutes(1));
        var request = await fixture.Approvals.RequestAsync("memory.delete", Arguments("memory-1", 1),
            "申请删除过期记忆", Requester);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));

        var exception = await Assert.ThrowsAsync<ToolApprovalException>(() =>
            fixture.Approvals.DecideAsync(request.Id, true, "批准", Approver));
        var consumption = await fixture.Approvals.ConsumeAsync(request.Id, "memory.delete",
            Arguments("memory-1", 1), Requester);

        Assert.Equal("TOOL_APPROVAL_EXPIRED", exception.Code);
        Assert.False(consumption.Allowed);
        Assert.Equal("TOOL_APPROVAL_EXPIRED", consumption.Decision.Code);
    }

    [Fact]
    public async Task SafeExecutorShouldRequireApprovalExecuteOnceAndPreserveIdempotency()
    {
        var fixture = CreateFixture();
        var arguments = Arguments("memory-1", 1);
        var withoutApproval = await fixture.Executor.ExecuteAsync("memory.delete", arguments, Requester, "delete-1");
        var request = await fixture.Approvals.RequestAsync("memory.delete", arguments, "申请删除错误记忆", Requester);
        await fixture.Approvals.DecideAsync(request.Id, true, "已核对", Approver);

        var first = await fixture.Executor.ExecuteAsync("memory.delete", arguments, Requester, "delete-1", request.Id);
        var replay = await fixture.Executor.ExecuteAsync("memory.delete", arguments, Requester, "delete-1", request.Id);
        var changed = await fixture.Executor.ExecuteAsync("memory.delete", Arguments("memory-1", 2), Requester,
            "delete-1", request.Id);

        Assert.Equal(ToolExecutionStatus.RequiresApproval, withoutApproval.Status);
        Assert.Equal(ToolExecutionStatus.Completed, first.Status);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(1, fixture.Tool.ExecutionCount);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED_WITH_DIFFERENT_REQUEST", changed.Safety.Code);
    }

    private static ApprovalFixture CreateFixture(TimeSpan? lifetime = null)
    {
        var tool = new ApprovalTestTool();
        var registry = new ServerToolRegistry([tool]);
        var safety = new RuleBasedToolInvocationSafetyService(new ToolSafetyOptions
        {
            AllowedTools = new HashSet<string>([tool.Descriptor.Name], StringComparer.OrdinalIgnoreCase)
        });
        var trace = new InMemoryTraceSink();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero));
        var approvals = new InMemoryToolApprovalService(registry, safety, trace, new ToolApprovalOptions
        {
            ApprovalLifetime = lifetime ?? TimeSpan.FromMinutes(15)
        }, clock);
        var executor = new SafeToolExecutor(registry, safety, trace, new ToolExecutorOptions(), clock, approvals);
        return new ApprovalFixture(tool, approvals, executor, clock);
    }

    private static JsonElement Arguments(string memoryId, int expectedVersion) =>
        JsonSerializer.SerializeToElement(new { memoryId, expectedVersion });

    private sealed record ApprovalFixture(ApprovalTestTool Tool, InMemoryToolApprovalService Approvals,
        SafeToolExecutor Executor, ManualTimeProvider Clock);

    private sealed class ApprovalTestTool : IServerTool
    {
        private int _executionCount;
        public int ExecutionCount => _executionCount;
        public ToolDescriptor Descriptor { get; } = new("memory.delete", "测试删除工具", ToolOperationRisk.Mutation,
            TimeSpan.FromSeconds(1), 2_048, true);
        public SafetyDecision ValidateArguments(JsonElement arguments) =>
            arguments.TryGetProperty("memoryId", out _) && arguments.TryGetProperty("expectedVersion", out _)
                ? SafetyDecision.Allowed
                : new SafetyDecision(SafetyAction.Refuse, "ARGUMENTS_INVALID", "参数无效。");
        public Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            _ = arguments;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executionCount);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { deleted = true }));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
