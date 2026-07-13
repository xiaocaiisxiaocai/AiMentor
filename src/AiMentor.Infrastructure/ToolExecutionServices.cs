using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>构建名称唯一且只读的服务器工具白名单。</summary>
public sealed class ServerToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, IServerTool> _tools;

    public ServerToolRegistry(IEnumerable<IServerTool> tools)
    {
        var registered = new Dictionary<string, IServerTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Descriptor.Name))
                throw new InvalidOperationException("服务器工具名称不能为空。");
            // 修改性工具一旦允许重试就可能重复产生副作用，因此注册阶段即强制幂等键约束。
            if (tool.Descriptor.Risk != ToolOperationRisk.ReadOnly && !tool.Descriptor.RequiresIdempotencyKey)
                throw new InvalidOperationException($"修改性工具必须要求幂等键：{tool.Descriptor.Name}");
            if (!registered.TryAdd(tool.Descriptor.Name, tool))
                throw new InvalidOperationException($"服务器工具名称重复：{tool.Descriptor.Name}");
        }
        _tools = registered;
        Descriptors = registered.Values.Select(tool => tool.Descriptor)
            .OrderBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<ToolDescriptor> Descriptors { get; }

    public bool TryGet(string toolName, out IServerTool? tool) => _tools.TryGetValue(toolName, out tool);
}

/// <summary>在工具实现运行前后执行身份、风险、参数、超时、幂等和结果大小门禁。</summary>
public sealed class SafeToolExecutor(
    IToolRegistry registry,
    IToolInvocationSafetyService safety,
    ITraceSink traceSink,
    ToolExecutorOptions options,
    TimeProvider timeProvider,
    IToolApprovalService? approvalService = null) : IToolExecutor
{
    private readonly ConcurrentDictionary<string, IdempotentExecution> _idempotentExecutions = new(StringComparer.Ordinal);

    public async Task<ToolExecutionResult> ExecuteAsync(string toolName, JsonElement arguments, AccessContext access,
        string? idempotencyKey = null, string? approvalId = null, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(toolName) || !registry.TryGet(toolName.Trim(), out var tool) || tool is null)
            return await RejectAsync(runId, toolName, "TOOL_NOT_REGISTERED", "请求的工具未在服务器注册表中。", cancellationToken);

        var normalizedArguments = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>())
            : arguments.Clone();
        if (normalizedArguments.ValueKind != JsonValueKind.Object)
            return await RejectAsync(runId, tool.Descriptor.Name, "TOOL_ARGUMENTS_MUST_BE_OBJECT", "工具参数必须是 JSON 对象。", cancellationToken);
        if (Encoding.UTF8.GetByteCount(normalizedArguments.GetRawText()) > options.MaximumArgumentBytes)
            return await RejectAsync(runId, tool.Descriptor.Name, "TOOL_ARGUMENTS_TOO_LARGE", "工具参数超过服务器允许的大小。", cancellationToken);

        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey);
        if (tool.Descriptor.RequiresIdempotencyKey && normalizedIdempotencyKey is null)
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_KEY_REQUIRED", "该工具必须提供合法的 Idempotency-Key。", cancellationToken);
        if (!string.IsNullOrWhiteSpace(idempotencyKey) && normalizedIdempotencyKey is null)
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_KEY_INVALID", "Idempotency-Key 格式无效。", cancellationToken);

        if (!tool.Descriptor.RequiresIdempotencyKey)
            return await ExecuteCoreAsync(runId, tool, normalizedArguments, access, approvalId, cancellationToken);

        var cacheKey = string.Join('\u001f', access.TenantId, access.SubjectId, tool.Descriptor.Name, normalizedIdempotencyKey);
        if (options.IdempotencyRetention <= TimeSpan.Zero || options.MaximumIdempotencyEntries <= 0)
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_CONFIGURATION_INVALID",
                "服务器幂等缓存配置无效。", cancellationToken);
        PruneIdempotencyCache(timeProvider.GetUtcNow());
        if (_idempotentExecutions.Count >= options.MaximumIdempotencyEntries
            && !_idempotentExecutions.ContainsKey(cacheKey))
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_CAPACITY_EXCEEDED",
                "服务器幂等缓存已达到容量上限，请稍后重试。", cancellationToken);

        var requestFingerprint = JsonArgumentFingerprint.Create(tool.Descriptor.Name, normalizedArguments, access);
        var candidate = new IdempotentExecution(timeProvider.GetUtcNow(), requestFingerprint,
            new Lazy<Task<ToolExecutionResult>>(
            () => ExecuteCoreAsync(runId, tool, normalizedArguments, access, approvalId, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var execution = _idempotentExecutions.GetOrAdd(cacheKey, candidate);
        var replay = !ReferenceEquals(candidate, execution);
        if (replay && !string.Equals(execution.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_KEY_REUSED_WITH_DIFFERENT_REQUEST",
                "相同 Idempotency-Key 已绑定其他工具参数。", cancellationToken);
        var result = await execution.Task.Value.WaitAsync(cancellationToken);
        if (result.Status != ToolExecutionStatus.Completed) _idempotentExecutions.TryRemove(cacheKey, out _);
        return result with { IdempotentReplay = replay };
    }

    private async Task<ToolExecutionResult> ExecuteCoreAsync(string runId, IServerTool tool, JsonElement arguments,
        AccessContext access, string? approvalId, CancellationToken cancellationToken)
    {
        var trace = new List<TraceStep>
        {
            Step("tool.started", "ok", new Dictionary<string, object?>
            {
                ["tool"] = tool.Descriptor.Name,
                ["risk"] = tool.Descriptor.Risk.ToString()
            })
        };
        var invocationArguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.GetRawText())
            ?? new Dictionary<string, object?>();
        var safetyDecision = safety.Review(new ToolInvocationRequest(tool.Descriptor.Name, tool.Descriptor.Risk,
            invocationArguments), access);
        trace.Add(Step("tool.safety", safetyDecision.Action.ToString(), new Dictionary<string, object?>
        {
            ["code"] = safetyDecision.Code,
            ["policyVersion"] = safety.PolicyVersion
        }));
        if (safetyDecision.Action == SafetyAction.Refuse)
        {
            return await CompleteAsync(runId, tool.Descriptor.Name, ToolExecutionStatus.Rejected, null,
                safetyDecision, false, trace, cancellationToken);
        }

        var validation = tool.ValidateArguments(arguments);
        trace.Add(Step("tool.arguments", validation.Action.ToString(), new Dictionary<string, object?> { ["code"] = validation.Code }));
        if (validation.Action != SafetyAction.Allow)
            return await CompleteAsync(runId, tool.Descriptor.Name, ToolExecutionStatus.Rejected, null, validation,
                false, trace, cancellationToken);

        var timeout = tool.Descriptor.Timeout <= options.MaximumTimeout ? tool.Descriptor.Timeout : options.MaximumTimeout;
        if (timeout <= TimeSpan.Zero || tool.Descriptor.MaximumResultBytes <= 0)
            return await RejectWithTraceAsync(runId, tool.Descriptor.Name, "TOOL_CONFIGURATION_INVALID",
                "工具的超时或结果大小配置无效。", trace, cancellationToken);

        if (safetyDecision.Action == SafetyAction.RequireApproval)
        {
            if (string.IsNullOrWhiteSpace(approvalId))
                return await CompleteAsync(runId, tool.Descriptor.Name, ToolExecutionStatus.RequiresApproval, null,
                    safetyDecision, false, trace, cancellationToken);
            if (approvalService is null)
                return await RejectWithTraceAsync(runId, tool.Descriptor.Name, "TOOL_APPROVAL_SERVICE_UNAVAILABLE",
                    "审批服务不可用，修改性工具保持关闭。", trace, cancellationToken);

            var consumption = await approvalService.ConsumeAsync(approvalId, tool.Descriptor.Name, arguments, access,
                cancellationToken);
            trace.Add(Step("tool.approval", consumption.Allowed ? "consumed" : "refused",
                new Dictionary<string, object?> { ["code"] = consumption.Decision.Code }));
            if (!consumption.Allowed)
                return await CompleteAsync(runId, tool.Descriptor.Name, ToolExecutionStatus.Rejected, null,
                    consumption.Decision, false, trace, cancellationToken);
            safetyDecision = consumption.Decision;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var output = await tool.ExecuteAsync(new ToolExecutionContext(access, runId), arguments, timeoutSource.Token);
            if (Encoding.UTF8.GetByteCount(output.GetRawText()) > tool.Descriptor.MaximumResultBytes)
            {
                var decision = new SafetyDecision(SafetyAction.Refuse, "TOOL_RESULT_TOO_LARGE", "工具结果超过服务器允许的大小，未返回给调用方。");
                trace.Add(Step("tool.result", "rejected", new Dictionary<string, object?> { ["code"] = decision.Code }));
                return await CompleteAsync(runId, tool.Descriptor.Name, ToolExecutionStatus.ResultTooLarge, null,
                    decision, false, trace, cancellationToken);
            }

            trace.Add(Step("tool.completed", "ok", new Dictionary<string, object?>
            {
                ["resultBytes"] = Encoding.UTF8.GetByteCount(output.GetRawText())
            }));
            return await CompleteAsync(runId, tool.Descriptor.Name, ToolExecutionStatus.Completed, output.Clone(),
                new SafetyDecision(SafetyAction.Allow, "TOOL_EXECUTION_SAFE", "工具已在安全边界内执行。"), false, trace, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var decision = new SafetyDecision(SafetyAction.Refuse, "TOOL_TIMEOUT", "工具执行超过服务器超时限制。");
            trace.Add(Step("tool.completed", "timeout", new Dictionary<string, object?> { ["code"] = decision.Code }));
            return await CompleteAsync(runId, tool.Descriptor.Name, ToolExecutionStatus.TimedOut, null, decision,
                false, trace, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var decision = new SafetyDecision(SafetyAction.Refuse, "TOOL_EXECUTION_FAILED", "工具执行失败，未返回不完整结果。");
            trace.Add(Step("tool.completed", "failed", new Dictionary<string, object?>
            {
                ["code"] = decision.Code,
                ["exceptionType"] = exception.GetType().Name
            }));
            return await CompleteAsync(runId, tool.Descriptor.Name, ToolExecutionStatus.Failed, null, decision,
                false, trace, cancellationToken);
        }
    }

    private Task<ToolExecutionResult> RejectAsync(string runId, string toolName, string code, string message,
        CancellationToken cancellationToken) => RejectWithTraceAsync(runId, toolName, code, message, [], cancellationToken);

    private Task<ToolExecutionResult> RejectWithTraceAsync(string runId, string toolName, string code, string message,
        List<TraceStep> trace, CancellationToken cancellationToken)
    {
        var decision = new SafetyDecision(SafetyAction.Refuse, code, message);
        trace.Add(Step("tool.rejected", "refused", new Dictionary<string, object?> { ["code"] = code }));
        return CompleteAsync(runId, toolName, ToolExecutionStatus.Rejected, null, decision, false, trace, cancellationToken);
    }

    private async Task<ToolExecutionResult> CompleteAsync(string runId, string toolName, ToolExecutionStatus status,
        JsonElement? output, SafetyDecision decision, bool replay, IReadOnlyList<TraceStep> trace,
        CancellationToken cancellationToken)
    {
        await traceSink.WriteAsync(runId, trace, cancellationToken);
        return new ToolExecutionResult(runId, toolName, status, output, decision, replay, trace);
    }

    private static string? NormalizeIdempotencyKey(string? value)
    {
        var normalized = value?.Trim();
        return normalized is { Length: > 0 and <= 128 }
            && normalized.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
                ? normalized : null;
    }

    private void PruneIdempotencyCache(DateTimeOffset now)
    {
        foreach (var item in _idempotentExecutions)
        {
            if (item.Value.CreatedAt.Add(options.IdempotencyRetention) <= now
                && item.Value.Task.IsValueCreated && item.Value.Task.Value.IsCompleted)
                _idempotentExecutions.TryRemove(item.Key, out _);
        }
    }

    private TraceStep Step(string name, string outcome, IReadOnlyDictionary<string, object?> details) =>
        new(name, outcome, timeProvider.GetUtcNow(), details);

    private sealed record IdempotentExecution(DateTimeOffset CreatedAt, string RequestFingerprint,
        Lazy<Task<ToolExecutionResult>> Task);
}

/// <summary>返回不含文档正文的只读知识库规模统计。</summary>
public sealed class KnowledgeStatisticsTool(IKnowledgeRepository repository) : IServerTool
{
    public ToolDescriptor Descriptor { get; } = new("knowledge.stats", "返回当前知识库文档和分块数量。",
        ToolOperationRisk.ReadOnly, TimeSpan.FromSeconds(2), 4 * 1024, false);

    public SafetyDecision ValidateArguments(JsonElement arguments) => arguments.EnumerateObject().Any()
        ? new SafetyDecision(SafetyAction.Refuse, "TOOL_ARGUMENTS_NOT_SUPPORTED", "knowledge.stats 不接受参数。")
        : new SafetyDecision(SafetyAction.Allow, "TOOL_ARGUMENTS_VALID", "工具参数通过校验。");

    public async Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = arguments;
        await repository.InitializeAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(new
        {
            documents = repository.Statistics.Documents,
            chunks = repository.Statistics.Chunks
        });
    }
}

/// <summary>
/// 通过既有记忆工作流删除当前用户的记忆；真正执行前仍需安全执行器消费独立审批。
/// </summary>
public sealed class MemoryDeleteTool(IMemoryWorkflowService memoryWorkflow) : IServerTool
{
    public ToolDescriptor Descriptor { get; } = new("memory.delete", "按标识和预期版本删除当前用户的记忆。",
        ToolOperationRisk.Mutation, TimeSpan.FromSeconds(3), 2 * 1024, true);

    public SafetyDecision ValidateArguments(JsonElement arguments)
    {
        var properties = arguments.EnumerateObject().ToArray();
        if (properties.Length != 2 || properties.Any(property =>
                property.Name is not ("memoryId" or "expectedVersion")))
            return new SafetyDecision(SafetyAction.Refuse, "MEMORY_DELETE_ARGUMENTS_INVALID",
                "memory.delete 只接受 memoryId 和 expectedVersion。");
        if (!arguments.TryGetProperty("memoryId", out var memoryId)
            || memoryId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(memoryId.GetString()) || memoryId.GetString()!.Length > 128)
            return new SafetyDecision(SafetyAction.Refuse, "MEMORY_ID_INVALID", "memoryId 格式无效。");
        if (!arguments.TryGetProperty("expectedVersion", out var expectedVersion)
            || expectedVersion.ValueKind != JsonValueKind.Number
            || !expectedVersion.TryGetInt32(out var version) || version <= 0)
            return new SafetyDecision(SafetyAction.Refuse, "MEMORY_VERSION_INVALID", "expectedVersion 必须大于 0。");
        return new SafetyDecision(SafetyAction.Allow, "TOOL_ARGUMENTS_VALID", "记忆删除参数通过校验。");
    }

    public async Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var memoryId = arguments.GetProperty("memoryId").GetString()!.Trim();
        var expectedVersion = arguments.GetProperty("expectedVersion").GetInt32();
        await memoryWorkflow.DeleteAsync(memoryId, expectedVersion, context.Access, cancellationToken);
        return JsonSerializer.SerializeToElement(new { deleted = true, memoryId });
    }
}
