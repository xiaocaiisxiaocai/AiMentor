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
