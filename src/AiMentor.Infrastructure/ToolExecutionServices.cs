using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

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

public sealed class SafeToolExecutor(
    IToolRegistry registry,
    IToolInvocationSafetyService safety,
    ITraceSink traceSink,
    ToolExecutorOptions options,
    TimeProvider timeProvider) : IToolExecutor
{
    private readonly ConcurrentDictionary<string, IdempotentExecution> _idempotentExecutions = new(StringComparer.Ordinal);

    public async Task<ToolExecutionResult> ExecuteAsync(string toolName, JsonElement arguments, AccessContext access,
        string? idempotencyKey = null, CancellationToken cancellationToken = default)
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
            return await ExecuteCoreAsync(runId, tool, normalizedArguments, access, cancellationToken);

        var cacheKey = string.Join('\u001f', access.TenantId, access.SubjectId, tool.Descriptor.Name, normalizedIdempotencyKey);
        if (options.IdempotencyRetention <= TimeSpan.Zero || options.MaximumIdempotencyEntries <= 0)
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_CONFIGURATION_INVALID",
                "服务器幂等缓存配置无效。", cancellationToken);
        PruneIdempotencyCache(timeProvider.GetUtcNow());
        if (_idempotentExecutions.Count >= options.MaximumIdempotencyEntries
            && !_idempotentExecutions.ContainsKey(cacheKey))
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_CAPACITY_EXCEEDED",
                "服务器幂等缓存已达到容量上限，请稍后重试。", cancellationToken);

        var candidate = new IdempotentExecution(timeProvider.GetUtcNow(), new Lazy<Task<ToolExecutionResult>>(
            () => ExecuteCoreAsync(runId, tool, normalizedArguments, access, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var execution = _idempotentExecutions.GetOrAdd(cacheKey, candidate);
        var replay = !ReferenceEquals(candidate, execution);
        var result = await execution.Task.Value.WaitAsync(cancellationToken);
        if (result.Status != ToolExecutionStatus.Completed) _idempotentExecutions.TryRemove(cacheKey, out _);
        return result with { IdempotentReplay = replay };
    }

    private async Task<ToolExecutionResult> ExecuteCoreAsync(string runId, IServerTool tool, JsonElement arguments,
        AccessContext access, CancellationToken cancellationToken)
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
        if (safetyDecision.Action != SafetyAction.Allow)
        {
            var status = safetyDecision.Action == SafetyAction.RequireApproval
                ? ToolExecutionStatus.RequiresApproval : ToolExecutionStatus.Rejected;
            return await CompleteAsync(runId, tool.Descriptor.Name, status, null, safetyDecision, false, trace, cancellationToken);
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

    private sealed record IdempotentExecution(DateTimeOffset CreatedAt, Lazy<Task<ToolExecutionResult>> Task);
}

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
