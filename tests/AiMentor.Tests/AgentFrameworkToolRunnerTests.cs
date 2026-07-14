using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Extensions.AI;
using Xunit;

namespace AiMentor.Tests;

public sealed class AgentFrameworkToolRunnerTests
{
    private static readonly AccessContext Access = AccessContext.Create("tenant-a", "user-a", ["readers"]);

    [Fact]
    public async Task RegisteredToolShouldRunThroughExecutorAndProduceAnswer()
    {
        var executor = new RecordingExecutor();
        var runner = CreateRunner(new DeterministicGroundedChatClient(), executor);

        var result = await runner.RunAsync("当前知识库有多少文档和分块？", Access, "agent-normal");

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Contains("23 份文档", result.Answer, StringComparison.Ordinal);
        Assert.Contains("74 个分块", result.Answer, StringComparison.Ordinal);
        Assert.Single(result.ToolSteps);
        Assert.Equal("knowledge.stats", result.ToolSteps[0].ToolName);
        Assert.Equal(1, executor.ExecutionCount);
    }

    [Fact]
    public async Task UnknownModelFunctionShouldNeverReachExecutor()
    {
        var executor = new RecordingExecutor();
        var runner = CreateRunner(new ScriptedToolChatClient("unregistered_tool", repeat: false), executor);

        var result = await runner.RunAsync("调用一个不存在的工具", Access, "agent-unknown");

        Assert.Empty(result.ToolSteps);
        Assert.Equal(0, executor.ExecutionCount);
    }

    [Fact]
    public async Task RepeatedModelToolCallShouldBeStoppedAfterFirstExecution()
    {
        var executor = new RecordingExecutor();
        var runner = CreateRunner(new ScriptedToolChatClient("knowledge_stats", repeat: true), executor);

        var result = await runner.RunAsync("重复查询知识库统计", Access, "agent-repeat");

        Assert.Equal(AgentRunStatus.LimitExceeded, result.Status);
        Assert.Equal("AGENT_REPEATED_TOOL_CALL", result.Safety.Code);
        Assert.Equal(1, executor.ExecutionCount);
        Assert.Single(result.ToolSteps);
    }

    [Fact]
    public async Task OversizedCumulativeToolResultShouldNotReachFinalAnswer()
    {
        var executor = new RecordingExecutor(new string('测', 256));
        var runner = CreateRunner(new DeterministicGroundedChatClient(), executor,
            new AgentExecutionOptions
            {
                MaximumModelIterations = 4,
                MaximumToolCalls = 3,
                MaximumCumulativeToolResultBytes = 100,
                MaximumRunTime = TimeSpan.FromSeconds(2)
            });

        var result = await runner.RunAsync("当前知识库有多少文档和分块？", Access, "agent-result-budget");

        Assert.Equal(AgentRunStatus.LimitExceeded, result.Status);
        Assert.Equal("AGENT_TOOL_RESULT_BUDGET", result.Safety.Code);
        Assert.Equal(ToolExecutionStatus.ResultTooLarge, Assert.Single(result.ToolSteps).Status);
    }

    [Fact]
    public async Task TotalRunTimeoutShouldCancelModelCall()
    {
        var executor = new RecordingExecutor();
        var runner = CreateRunner(new HangingChatClient(), executor,
            new AgentExecutionOptions
            {
                MaximumModelIterations = 4,
                MaximumToolCalls = 3,
                MaximumCumulativeToolResultBytes = 4_096,
                MaximumRunTime = TimeSpan.FromMilliseconds(50)
            });

        var result = await runner.RunAsync("执行一个会超时的规划", Access, "agent-timeout");

        Assert.Equal(AgentRunStatus.LimitExceeded, result.Status);
        Assert.Equal("AGENT_RUN_TIMEOUT", result.Safety.Code);
        Assert.Equal(0, executor.ExecutionCount);
    }

    [Fact]
    public async Task MutationToolShouldPauseUntilIndependentApprovalThenResumeOnce()
    {
        var fixture = CreateApprovalRunner();
        var initial = await fixture.Runner.RunAsync(
            "请删除记忆 memoryId=memory-1 expectedVersion=1", Access, "agent-approval-success");

        Assert.Equal(AgentRunStatus.AwaitingApproval, initial.Status);
        Assert.NotNull(initial.Approval);
        Assert.Equal("memory.delete", initial.Approval.ToolName);
        Assert.Equal(0, fixture.Tool.ExecutionCount);

        var stillPending = await fixture.Runner.ResumeAsync(initial.RunId, Access);
        Assert.Equal(AgentRunStatus.AwaitingApproval, stillPending.Status);
        await fixture.Approvals.DecideAsync(initial.Approval.ApprovalId, true, "已核对删除范围", fixture.Approver);
        var completed = await fixture.Runner.ResumeAsync(initial.RunId, Access);

        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Equal(1, fixture.Tool.ExecutionCount);
        Assert.Equal(ToolExecutionStatus.Completed, Assert.Single(completed.ToolSteps).Status);
        var replay = await Assert.ThrowsAsync<AgentRunWorkflowException>(() =>
            fixture.Runner.ResumeAsync(initial.RunId, Access));
        Assert.Equal("AGENT_RUN_NOT_FOUND", replay.Code);
    }

    [Fact]
    public async Task RejectedOrExpiredApprovalShouldResumeAsRefusalWithoutExecutingTool()
    {
        var rejectedFixture = CreateApprovalRunner();
        var rejectedRun = await rejectedFixture.Runner.RunAsync(
            "请删除记忆 memoryId=memory-2 expectedVersion=1", Access, "agent-approval-rejected");
        await rejectedFixture.Approvals.DecideAsync(rejectedRun.Approval!.ApprovalId, false, "删除依据不足",
            rejectedFixture.Approver);
        var rejected = await rejectedFixture.Runner.ResumeAsync(rejectedRun.RunId, Access);

        var expiredFixture = CreateApprovalRunner(TimeSpan.FromMinutes(1));
        var expiredRun = await expiredFixture.Runner.RunAsync(
            "请删除记忆 memoryId=memory-3 expectedVersion=1", Access, "agent-approval-expired");
        expiredFixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var expired = await expiredFixture.Runner.ResumeAsync(expiredRun.RunId, Access);

        Assert.Equal(AgentRunStatus.Refused, rejected.Status);
        Assert.Equal("AGENT_TOOL_APPROVAL_REJECTED", rejected.Safety.Code);
        Assert.Equal(0, rejectedFixture.Tool.ExecutionCount);
        Assert.Equal(AgentRunStatus.Refused, expired.Status);
        Assert.Equal("AGENT_TOOL_APPROVAL_EXPIRED", expired.Safety.Code);
        Assert.Equal(0, expiredFixture.Tool.ExecutionCount);
    }

    [Fact]
    public async Task OnlyOriginalRequesterShouldResumeAndConcurrentResumeShouldExecuteOnce()
    {
        var fixture = CreateApprovalRunner();
        var initial = await fixture.Runner.RunAsync(
            "请删除记忆 memoryId=memory-4 expectedVersion=1", Access, "agent-approval-concurrent");
        var otherUser = AccessContext.Create("tenant-a", "user-b", ["readers"]);
        var forbidden = await Assert.ThrowsAsync<AgentRunWorkflowException>(() =>
            fixture.Runner.ResumeAsync(initial.RunId, otherUser));
        await fixture.Approvals.DecideAsync(initial.Approval!.ApprovalId, true, "批准", fixture.Approver);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                return (Result: await fixture.Runner.ResumeAsync(initial.RunId, Access), Error: (string?)null);
            }
            catch (AgentRunWorkflowException exception)
            {
                return (Result: (AgentRunResult?)null, Error: exception.Code);
            }
        }));

        Assert.Equal("AGENT_RUN_RESUME_FORBIDDEN", forbidden.Code);
        Assert.Single(attempts, attempt => attempt.Result?.Status == AgentRunStatus.Completed);
        Assert.Single(attempts, attempt => attempt.Error is "AGENT_RUN_ALREADY_RESUMED"
            or "AGENT_RUN_ALREADY_RESUMING" or "AGENT_RUN_NOT_FOUND");
        Assert.Equal(1, fixture.Tool.ExecutionCount);
    }

    [Fact]
    public async Task NewRunnerInstanceShouldRestoreSerializedSessionAndResumeExactlyOnce()
    {
        var fixture = CreateApprovalRunner();
        var initial = await fixture.Runner.RunAsync(
            "请删除记忆 memoryId=memory-restart expectedVersion=1", Access, "agent-after-restart");
        await fixture.Approvals.DecideAsync(initial.Approval!.ApprovalId, true, "批准跨实例恢复", fixture.Approver);

        // 使用全新的 Agent 与运行器对象模拟进程重建，只共享外部状态存储和业务依赖。
        var restoredRunner = new AgentFrameworkToolRunner(new DeterministicGroundedChatClient(), fixture.Registry,
            fixture.Executor, new RuleBasedInputSafetyService(), fixture.Trace, fixture.Options, fixture.Clock,
            fixture.Approvals, fixture.Checkpoints);
        var completed = await restoredRunner.ResumeAsync(initial.RunId, Access);

        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Equal(1, fixture.Tool.ExecutionCount);
        Assert.Single(completed.ToolSteps);
    }

    [Fact]
    public async Task LostRenewalShouldCancelResumedRunBeforeMutationExecutes()
    {
        var options = new AgentExecutionOptions
        {
            MaximumModelIterations = 4,
            MaximumToolCalls = 3,
            MaximumCumulativeToolResultBytes = 4_096,
            MaximumRunTime = TimeSpan.FromSeconds(1),
            ResumeLeaseDuration = TimeSpan.FromMilliseconds(120),
            ResumeLeaseRenewalInterval = TimeSpan.FromMilliseconds(30)
        };
        var fixture = CreateApprovalRunner(options: options, recordRenewals: true, allowRenewal: false,
            mutationDelay: TimeSpan.FromMilliseconds(200));
        var initial = await fixture.Runner.RunAsync(
            "请删除记忆 memoryId=memory-lease-loss expectedVersion=1", Access, "agent-lease-loss");
        await fixture.Approvals.DecideAsync(initial.Approval!.ApprovalId, true, "批准租约丢失测试", fixture.Approver);

        var exception = await Assert.ThrowsAsync<AgentRunWorkflowException>(() =>
            fixture.Runner.ResumeAsync(initial.RunId, Access));

        Assert.Equal("AGENT_RESUME_LEASE_LOST", exception.Code);
        Assert.True(fixture.RenewalStore!.RenewalCount >= 1);
        Assert.Equal(0, fixture.Tool.ExecutionCount);
    }

    [Fact]
    public async Task PendingApprovalCapacityShouldFailClosedBeforeCreatingAnotherPause()
    {
        var fixture = CreateApprovalRunner(maximumPendingRuns: 1);
        var first = await fixture.Runner.RunAsync(
            "请删除记忆 memoryId=memory-5 expectedVersion=1", Access, "agent-capacity-1");

        var exception = await Assert.ThrowsAsync<AgentRunWorkflowException>(() => fixture.Runner.RunAsync(
            "请删除记忆 memoryId=memory-6 expectedVersion=1", Access, "agent-capacity-2"));

        Assert.Equal(AgentRunStatus.AwaitingApproval, first.Status);
        Assert.Equal("AGENT_PENDING_CAPACITY_EXCEEDED", exception.Code);
        Assert.Equal(0, fixture.Tool.ExecutionCount);
    }

    private static AgentFrameworkToolRunner CreateRunner(IChatClient chatClient, RecordingExecutor executor,
        AgentExecutionOptions? options = null)
    {
        var registry = new ServerToolRegistry([new DescriptorOnlyTool()]);
        return new AgentFrameworkToolRunner(chatClient, registry, executor, new RuleBasedInputSafetyService(),
            new InMemoryTraceSink(), options ?? new AgentExecutionOptions
            {
                MaximumModelIterations = 4,
                MaximumToolCalls = 3,
                MaximumCumulativeToolResultBytes = 4_096,
                MaximumRunTime = TimeSpan.FromSeconds(2)
            }, TimeProvider.System);
    }

    private static ApprovalRunnerFixture CreateApprovalRunner(TimeSpan? approvalLifetime = null,
        int maximumPendingRuns = 1_000, IChatClient? chatClient = null, AgentExecutionOptions? options = null,
        bool recordRenewals = false, bool allowRenewal = true, TimeSpan? mutationDelay = null)
    {
        var tool = new MutationTool(mutationDelay);
        var registry = new ServerToolRegistry([tool]);
        var safety = new RuleBasedToolInvocationSafetyService(new ToolSafetyOptions
        {
            AllowedTools = new HashSet<string>([tool.Descriptor.Name], StringComparer.OrdinalIgnoreCase)
        });
        var trace = new InMemoryTraceSink();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero));
        var approvals = new InMemoryToolApprovalService(registry, safety, trace, new ToolApprovalOptions
        {
            ApprovalLifetime = approvalLifetime ?? TimeSpan.FromMinutes(15)
        }, clock);
        var executor = new SafeToolExecutor(registry, safety, trace, new ToolExecutorOptions(), clock, approvals);
        var agentOptions = options ?? new AgentExecutionOptions
        {
            MaximumModelIterations = 4,
            MaximumToolCalls = 3,
            MaximumCumulativeToolResultBytes = 4_096,
            MaximumPendingApprovalRuns = maximumPendingRuns,
            MaximumRunTime = TimeSpan.FromSeconds(2)
        };
        var inMemoryCheckpoints = new InMemoryAgentRunCheckpointStore(clock, maximumPendingRuns);
        var renewalStore = recordRenewals
            ? new RecordingCheckpointStore(inMemoryCheckpoints, allowRenewal)
            : null;
        IAgentRunCheckpointStore checkpoints = renewalStore is null ? inMemoryCheckpoints : renewalStore;
        var runner = new AgentFrameworkToolRunner(chatClient ?? new DeterministicGroundedChatClient(), registry, executor,
            new RuleBasedInputSafetyService(), trace, agentOptions, clock, approvals, checkpoints);
        return new ApprovalRunnerFixture(runner, approvals, tool, clock,
            AccessContext.Create("tenant-a", "approver", ["tool-approvers"]), registry, executor, trace,
            agentOptions, checkpoints, renewalStore);
    }

    private sealed class RecordingExecutor(string? padding = null) : IToolExecutor
    {
        private int _executionCount;
        public int ExecutionCount => _executionCount;

        public Task<ToolExecutionResult> ExecuteAsync(string toolName, JsonElement arguments, AccessContext access,
            string? idempotencyKey = null, string? approvalId = null, CancellationToken cancellationToken = default)
        {
            _ = arguments;
            _ = access;
            _ = idempotencyKey;
            _ = approvalId;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executionCount);
            var output = JsonSerializer.SerializeToElement(new { documents = 23, chunks = 74, padding });
            return Task.FromResult(new ToolExecutionResult(Guid.NewGuid().ToString("N"), toolName,
                ToolExecutionStatus.Completed, output,
                new SafetyDecision(SafetyAction.Allow, "TOOL_EXECUTION_SAFE", "测试执行成功。"), false, []));
        }
    }

    private sealed class DescriptorOnlyTool : IServerTool
    {
        public ToolDescriptor Descriptor { get; } = new("knowledge.stats", "返回知识库统计。",
            ToolOperationRisk.ReadOnly, TimeSpan.FromSeconds(1), 4_096, false);

        public SafetyDecision ValidateArguments(JsonElement arguments) => SafetyDecision.Allowed;

        public Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Agent 不得绕过 IToolExecutor 直接调用工具实现。");
    }

    private sealed class MutationTool(TimeSpan? executionDelay = null) : IServerTool
    {
        private int _executionCount;
        public int ExecutionCount => _executionCount;
        public ToolDescriptor Descriptor { get; } = new("memory.delete", "删除测试记忆。", ToolOperationRisk.Mutation,
            TimeSpan.FromSeconds(1), 2_048, true);
        public SafetyDecision ValidateArguments(JsonElement arguments) =>
            arguments.TryGetProperty("memoryId", out _) && arguments.TryGetProperty("expectedVersion", out _)
                ? SafetyDecision.Allowed
                : new SafetyDecision(SafetyAction.Refuse, "ARGUMENTS_INVALID", "参数无效。");
        public async Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            _ = arguments;
            if (executionDelay is not null)
                await Task.Delay(executionDelay.Value, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executionCount);
            return JsonSerializer.SerializeToElement(new { deleted = true });
        }
    }

    private sealed record ApprovalRunnerFixture(AgentFrameworkToolRunner Runner,
        InMemoryToolApprovalService Approvals, MutationTool Tool, ManualTimeProvider Clock, AccessContext Approver,
        ServerToolRegistry Registry, SafeToolExecutor Executor, InMemoryTraceSink Trace,
        AgentExecutionOptions Options, IAgentRunCheckpointStore Checkpoints,
        RecordingCheckpointStore? RenewalStore);

    /// <summary>记录续租调用并可模拟共享存储拒绝续租，用于验证旧实例失败关闭。</summary>
    private sealed class RecordingCheckpointStore(IAgentRunCheckpointStore inner, bool allowRenewal)
        : IAgentRunCheckpointStore
    {
        private int _renewalCount;
        public int RenewalCount => _renewalCount;

        public Task SavePendingAsync(AgentRunCheckpoint checkpoint, string? leaseToken = null,
            CancellationToken cancellationToken = default) =>
            inner.SavePendingAsync(checkpoint, leaseToken, cancellationToken);

        public Task<AgentRunLeaseResult> TryAcquireAsync(string runId, AccessContext access, string leaseOwner,
            TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            inner.TryAcquireAsync(runId, access, leaseOwner, leaseDuration, cancellationToken);

        public async Task<bool> RenewAsync(string runId, string leaseToken, string leaseOwner,
            TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _renewalCount);
            return allowRenewal && await inner.RenewAsync(runId, leaseToken, leaseOwner, leaseDuration,
                cancellationToken);
        }

        public Task ReleaseAsync(string runId, string leaseToken, CancellationToken cancellationToken = default) =>
            inner.ReleaseAsync(runId, leaseToken, cancellationToken);

        public Task CompleteAsync(string runId, string leaseToken, CancellationToken cancellationToken = default) =>
            inner.CompleteAsync(runId, leaseToken, cancellationToken);
    }


    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }

    private sealed class ScriptedToolChatClient(string functionName, bool repeat) : IChatClient
    {
        private static readonly ChatClientMetadata Metadata = new("AiMentor.Tests", defaultModelId: "tool-script");
        private int _responses;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
            var responseNumber = Interlocked.Increment(ref _responses);
            if (!repeat && responseNumber > 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "未找到可执行工具。")));
            var call = new FunctionCallContent($"call-{responseNumber}", functionName,
                new Dictionary<string, object?> { ["arguments"] = new Dictionary<string, object?>() });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(Metadata) ? Metadata : null;

        public void Dispose() { }
    }

    private sealed class HangingChatClient : IChatClient
    {
        private static readonly ChatClientMetadata Metadata = new("AiMentor.Tests", defaultModelId: "hanging");

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = await GetResponseAsync(messages, options, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(Metadata) ? Metadata : null;

        public void Dispose() { }
    }
}
