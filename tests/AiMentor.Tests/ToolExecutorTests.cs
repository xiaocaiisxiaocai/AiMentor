using System.Text.Json;
using AiMentor.Api;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class ToolExecutorTests
{
    private static readonly AccessContext Access = AccessContext.Create("tenant-a", "user-a", ["all-rnd"]);
    private static readonly JsonElement EmptyArguments = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ApiContractShouldMaterializeNestedToolArgumentsIndependently()
    {
        var request = JsonSerializer.Deserialize<ExecuteToolRequest>(
            "{\"arguments\":{\"endpoint\":\"https://127.0.0.1/admin\"}}", WebJsonOptions);

        Assert.NotNull(request?.Arguments);
        Assert.Equal("https://127.0.0.1/admin", request.Arguments["endpoint"].GetString());
    }

    [Fact]
    public async Task RegisteredKnowledgeToolShouldExecuteThroughSafetyBoundary()
    {
        var tool = new KnowledgeStatisticsTool(new StubKnowledgeRepository(new KnowledgeStatistics(23, 74)));
        var executor = CreateExecutor(tool);

        // ASP.NET 在请求头缺失时会传入空字符串，必须与“未提供”保持同一语义。
        var result = await executor.ExecuteAsync("knowledge.stats", EmptyArguments, Access, string.Empty);

        Assert.Equal(ToolExecutionStatus.Completed, result.Status);
        Assert.Equal(23, result.Output!.Value.GetProperty("documents").GetInt32());
        Assert.Equal(74, result.Output.Value.GetProperty("chunks").GetInt32());
        Assert.Contains(result.Trace, step => step.Name == "tool.safety" && Equals(step.Details["code"], "TOOL_ARGUMENTS_SAFE"));
    }

    [Fact]
    public async Task ServerRiskShouldForceApprovalBeforeMutationToolRuns()
    {
        var tool = new FakeTool("memory.mutate", ToolOperationRisk.Mutation, requiresIdempotencyKey: true);
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteAsync(tool.Descriptor.Name, EmptyArguments, Access, "mutation-001");

        Assert.Equal(ToolExecutionStatus.RequiresApproval, result.Status);
        Assert.Equal("TOOL_OPERATION_REQUIRES_APPROVAL", result.Safety.Code);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task CrossTenantAndPrivateNetworkArgumentsShouldBeRejectedBeforeExecution()
    {
        var tool = new FakeTool("external.lookup", ToolOperationRisk.ReadOnly);
        var executor = CreateExecutor(tool);
        var crossTenant = JsonSerializer.SerializeToElement(new { tenantId = "tenant-b" });
        var privateTarget = JsonSerializer.SerializeToElement(new { endpoint = "https://127.0.0.1/admin" });

        var tenantResult = await executor.ExecuteAsync(tool.Descriptor.Name, crossTenant, Access);
        var networkResult = await executor.ExecuteAsync(tool.Descriptor.Name, privateTarget, Access);

        Assert.Equal("CROSS_TENANT_TOOL_ARGUMENT", tenantResult.Safety.Code);
        Assert.Equal("TOOL_NETWORK_TARGET_INVALID", networkResult.Safety.Code);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task TimeoutAndOversizedResultShouldReturnNoOutput()
    {
        var slowTool = new FakeTool("slow.read", ToolOperationRisk.ReadOnly, timeout: TimeSpan.FromMilliseconds(20),
            execute: async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                return JsonSerializer.SerializeToElement(new { done = true });
            });
        var largeTool = new FakeTool("large.read", ToolOperationRisk.ReadOnly, maximumResultBytes: 20,
            execute: _ => Task.FromResult(JsonSerializer.SerializeToElement(new { value = new string('x', 200) })));

        var timeoutResult = await CreateExecutor(slowTool).ExecuteAsync(slowTool.Descriptor.Name, EmptyArguments, Access);
        var largeResult = await CreateExecutor(largeTool).ExecuteAsync(largeTool.Descriptor.Name, EmptyArguments, Access);

        Assert.Equal(ToolExecutionStatus.TimedOut, timeoutResult.Status);
        Assert.Null(timeoutResult.Output);
        Assert.Equal(ToolExecutionStatus.ResultTooLarge, largeResult.Status);
        Assert.Null(largeResult.Output);
    }

    [Fact]
    public async Task IdempotencyKeyShouldReplayCompletedExecutionWithoutRunningToolTwice()
    {
        var tool = new FakeTool("stable.read", ToolOperationRisk.ReadOnly, requiresIdempotencyKey: true);
        var executor = CreateExecutor(tool);

        var first = await executor.ExecuteAsync(tool.Descriptor.Name, EmptyArguments, Access, "request-001");
        var second = await executor.ExecuteAsync(tool.Descriptor.Name, EmptyArguments, Access, "request-001");

        Assert.Equal(ToolExecutionStatus.Completed, first.Status);
        Assert.False(first.IdempotentReplay);
        Assert.True(second.IdempotentReplay);
        Assert.Equal(first.RunId, second.RunId);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task IdempotencyKeyShouldRejectDifferentArgumentsInsteadOfReplayingWrongResult()
    {
        var tool = new FakeTool("stable.read", ToolOperationRisk.ReadOnly, requiresIdempotencyKey: true);
        var executor = CreateExecutor(tool);

        await executor.ExecuteAsync(tool.Descriptor.Name, JsonSerializer.SerializeToElement(new { value = 1 }),
            Access, "request-002");
        var result = await executor.ExecuteAsync(tool.Descriptor.Name,
            JsonSerializer.SerializeToElement(new { value = 2 }), Access, "request-002");

        Assert.Equal(ToolExecutionStatus.Rejected, result.Status);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED_WITH_DIFFERENT_REQUEST", result.Safety.Code);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task OutcomeUnknownLedgerStateShouldNeverInvokeToolAgain()
    {
        var tool = new FakeTool("stable.mutate", ToolOperationRisk.Mutation, requiresIdempotencyKey: true);
        var executor = CreateExecutor(tool, new OutcomeUnknownLedger());

        var result = await executor.ExecuteAsync(tool.Descriptor.Name, EmptyArguments, Access, "request-unknown");

        Assert.Equal(ToolExecutionStatus.OutcomeUnknown, result.Status);
        Assert.Equal("TOOL_EXECUTION_OUTCOME_UNKNOWN", result.Safety.Code);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public void MutationToolWithoutIdempotencyRequirementShouldFailRegistration()
    {
        var tool = new FakeTool("unsafe.mutate", ToolOperationRisk.Mutation);

        var exception = Assert.Throws<InvalidOperationException>(() => new ServerToolRegistry([tool]));

        Assert.Contains("必须要求幂等键", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownToolShouldNotReachAnyRegisteredImplementation()
    {
        var tool = new FakeTool("known.read", ToolOperationRisk.ReadOnly);
        var result = await CreateExecutor(tool).ExecuteAsync("unknown.read", EmptyArguments, Access);

        Assert.Equal(ToolExecutionStatus.Rejected, result.Status);
        Assert.Equal("TOOL_NOT_REGISTERED", result.Safety.Code);
        Assert.Equal(0, tool.ExecutionCount);
    }

    private static SafeToolExecutor CreateExecutor(IServerTool tool, IToolExecutionLedger? ledger = null)
    {
        var safety = new RuleBasedToolInvocationSafetyService(new ToolSafetyOptions
        {
            AllowedTools = new HashSet<string>([tool.Descriptor.Name], StringComparer.OrdinalIgnoreCase)
        });
        return new SafeToolExecutor(new ServerToolRegistry([tool]), safety, new InMemoryTraceSink(),
            new ToolExecutorOptions(), TimeProvider.System, executionLedger: ledger);
    }

    private sealed class OutcomeUnknownLedger : IToolExecutionLedger
    {
        public Task<IdempotencyAcquireResult> TryAcquireAsync(ToolExecutionLedgerRequest request,
            TimeSpan leaseDuration, TimeSpan retention, int maximumEntries,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.OutcomeUnknown));
        public Task MarkExecutingAsync(string executionKey, string leaseToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAsync(string executionKey, string leaseToken, ToolExecutionResult result, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkOutcomeUnknownAsync(string executionKey, string leaseToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AbandonAsync(string executionKey, string leaseToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(string tenantId, int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OutcomeUnknownToolExecutionDetail?> GetOutcomeUnknownAsync(string tenantId, string executionKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeTool(
        string name,
        ToolOperationRisk risk,
        TimeSpan? timeout = null,
        int maximumResultBytes = 4_096,
        bool requiresIdempotencyKey = false,
        Func<CancellationToken, Task<JsonElement>>? execute = null) : IServerTool
    {
        private int _executionCount;
        public int ExecutionCount => _executionCount;
        public ToolDescriptor Descriptor { get; } = new(name, "测试工具", risk, timeout ?? TimeSpan.FromSeconds(1),
            maximumResultBytes, requiresIdempotencyKey);

        public SafetyDecision ValidateArguments(JsonElement arguments) =>
            new(SafetyAction.Allow, "TOOL_ARGUMENTS_VALID", "测试参数有效。");

        public async Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            _ = arguments;
            Interlocked.Increment(ref _executionCount);
            return execute is null
                ? JsonSerializer.SerializeToElement(new { ok = true })
                : await execute(cancellationToken);
        }
    }

    private sealed class StubKnowledgeRepository(KnowledgeStatistics statistics) : IKnowledgeRepository
    {
        public KnowledgeStatistics Statistics { get; } = statistics;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Evidence>> SearchAsync(string query, AccessContext access, int limit,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Evidence>>([]);
    }
}
