using System.Globalization;
using System.Security.Cryptography;
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
            if (tool is ICompensableServerTool compensable)
            {
                if (tool.Descriptor.Risk == ToolOperationRisk.ReadOnly)
                    throw new InvalidOperationException($"只读工具不得声明补偿契约：{tool.Descriptor.Name}");
                if (string.IsNullOrWhiteSpace(compensable.CompensationToolName)
                    || string.Equals(compensable.CompensationToolName, tool.Descriptor.Name,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"补偿工具必须使用独立且非空的服务器工具名：{tool.Descriptor.Name}");
            }
            if (tool is IManualReconciliationServerTool manual)
            {
                if (tool.Descriptor.Risk == ToolOperationRisk.ReadOnly)
                    throw new InvalidOperationException($"只读工具不得声明人工补偿对账：{tool.Descriptor.Name}");
                if (tool is ICompensableServerTool)
                    throw new InvalidOperationException($"工具不能同时声明自动补偿和人工补偿对账：{tool.Descriptor.Name}");
                if (string.IsNullOrWhiteSpace(manual.CompensationUnavailableCode)
                    || string.IsNullOrWhiteSpace(manual.CompensationUnavailableExplanation))
                    throw new InvalidOperationException($"人工补偿对账必须提供稳定原因码和说明：{tool.Descriptor.Name}");
            }
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

/// <summary>
/// 只发布服务器实现显式声明的补偿能力；未实现任何补偿契约的修改工具默认失败关闭。
/// </summary>
public sealed class ToolCompensationCatalog(IToolRegistry registry) : IToolCompensationCatalog
{
    public bool TryDescribe(string toolName, out ToolCompensationDescriptor? descriptor)
    {
        descriptor = null;
        if (string.IsNullOrWhiteSpace(toolName) || !registry.TryGet(toolName.Trim(), out var tool) || tool is null)
            return false;

        if (tool.Descriptor.Risk == ToolOperationRisk.ReadOnly)
        {
            descriptor = new ToolCompensationDescriptor(tool.Descriptor.Name,
                ToolCompensationCapability.NotApplicable, "TOOL_COMPENSATION_NOT_APPLICABLE",
                "只读工具不产生需要反向补偿的业务副作用。");
            return true;
        }
        if (tool is ICompensableServerTool compensable)
        {
            descriptor = new ToolCompensationDescriptor(tool.Descriptor.Name,
                ToolCompensationCapability.Compensable, "TOOL_COMPENSATION_CONTRACT_AVAILABLE",
                "工具声明了补偿快照和反向执行契约；实际补偿仍必须经过加密账本、独立审批和幂等执行。",
                compensable.CompensationToolName);
            return true;
        }
        if (tool is IManualReconciliationServerTool manual)
        {
            descriptor = new ToolCompensationDescriptor(tool.Descriptor.Name,
                ToolCompensationCapability.ManualReconciliation, manual.CompensationUnavailableCode,
                manual.CompensationUnavailableExplanation);
            return true;
        }

        descriptor = new ToolCompensationDescriptor(tool.Descriptor.Name,
            ToolCompensationCapability.NotSupported, "TOOL_COMPENSATION_NOT_SUPPORTED",
            "该修改工具尚未实现服务器补偿契约，系统不会提供通用回滚入口。");
        return true;
    }
}

/// <summary>在工具实现运行前后执行身份、风险、参数、超时、幂等和结果大小门禁。</summary>
public sealed class SafeToolExecutor(
    IToolRegistry registry,
    IToolInvocationSafetyService safety,
    ITraceSink traceSink,
    ToolExecutorOptions options,
    TimeProvider timeProvider,
    IToolApprovalService? approvalService = null,
    IToolExecutionLedger? executionLedger = null,
    IToolExecutionBarrier? executionBarrier = null,
    IToolCompensationService? compensationService = null) : IToolExecutor
{
    private readonly IToolExecutionLedger _executionLedger = executionLedger ?? new InMemoryToolExecutionLedger(timeProvider);
    private readonly IToolExecutionBarrier _executionBarrier = executionBarrier ?? NoOpToolExecutionBarrier.Instance;

    public async Task<ToolExecutionResult> ExecuteAsync(string toolName, JsonElement arguments, AccessContext access,
        string? idempotencyKey = null, string? approvalId = null, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(toolName) || !registry.TryGet(toolName.Trim(), out var tool) || tool is null)
            return await RejectAsync(runId, toolName, "TOOL_NOT_REGISTERED", "请求的工具未在服务器注册表中。", cancellationToken);
        if (tool is ICompensableServerTool && (compensationService is null || !compensationService.IsAvailable))
            return await RejectAsync(runId, tool.Descriptor.Name, "TOOL_COMPENSATION_DURABLE_STORE_UNAVAILABLE",
                "当前部署没有可用的补偿账本，可补偿修改保持关闭。", cancellationToken);

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
            return await ExecuteCoreAsync(runId, tool, normalizedArguments, access, approvalId, null, cancellationToken);

        if (options.IdempotencyRetention <= TimeSpan.Zero || options.MaximumIdempotencyEntries <= 0
            || options.IdempotencyLeaseDuration <= options.MaximumTimeout)
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_CONFIGURATION_INVALID",
                "服务器幂等执行账本配置无效。", cancellationToken);

        var requestFingerprint = JsonArgumentFingerprint.Create(tool.Descriptor.Name, normalizedArguments, access);
        var executionKey = CreateExecutionKey(access, tool.Descriptor.Name, normalizedIdempotencyKey!);
        var acquired = await _executionLedger.TryAcquireAsync(new ToolExecutionLedgerRequest(
                executionKey, requestFingerprint, runId, access.TenantId, access.SubjectId, tool.Descriptor.Name),
            options.IdempotencyLeaseDuration, options.IdempotencyRetention, options.MaximumIdempotencyEntries,
            cancellationToken);
        if (acquired.Status == IdempotencyAcquireStatus.Replay && acquired.ReplayResult is not null)
            return acquired.ReplayResult with { IdempotentReplay = true };
        if (acquired.Status == IdempotencyAcquireStatus.FingerprintMismatch)
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_KEY_REUSED_WITH_DIFFERENT_REQUEST",
                "相同 Idempotency-Key 已绑定其他工具参数。", cancellationToken);
        if (acquired.Status == IdempotencyAcquireStatus.InProgress)
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_EXECUTION_IN_PROGRESS",
                "相同幂等请求正在执行，请稍后查询或重试。", cancellationToken);
        if (acquired.Status == IdempotencyAcquireStatus.OutcomeUnknown)
            return await OutcomeUnknownAsync(runId, tool.Descriptor.Name, cancellationToken);
        if (acquired.Status == IdempotencyAcquireStatus.ReconciledApplied)
            return await ReconciledAsync(runId, tool.Descriptor.Name, cancellationToken);
        if (acquired.Status == IdempotencyAcquireStatus.Capacity)
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_CAPACITY_EXCEEDED",
                "服务器幂等执行账本已达到容量上限，请稍后重试。", cancellationToken);
        if (acquired.Status != IdempotencyAcquireStatus.Acquired || acquired.LeaseToken is null)
            return await RejectAsync(runId, tool.Descriptor.Name, "IDEMPOTENCY_LEDGER_FAILED",
                "服务器无法建立幂等执行占位。", cancellationToken);

        var sideEffectStarted = false;
        ToolCompensationPreparation? compensationPreparation = null;
        try
        {
            var result = await ExecuteCoreAsync(runId, tool, normalizedArguments, access, approvalId,
                async token =>
                {
                    if (tool is ICompensableServerTool compensable)
                    {
                        // 快照先于 Executing 和真实副作用建立；任何准备失败都会让正向修改失败关闭。
                        compensationPreparation = await compensationService!.PrepareForwardAsync(executionKey,
                            compensable, new ToolExecutionContext(access, runId), normalizedArguments, token);
                    }
                    await _executionLedger.MarkExecutingAsync(executionKey, acquired.LeaseToken, token);
                    sideEffectStarted = true;
                    // 屏障位于持久化状态变更和真实副作用之间，使进程强杀验收拥有精确窗口。
                    await _executionBarrier.WaitAfterExecutingAsync(executionKey, tool.Descriptor.Name, token);
                }, cancellationToken);
            if (!sideEffectStarted)
            {
                if (compensationPreparation is not null)
                    await compensationService!.DiscardAsync(compensationPreparation, CancellationToken.None);
                await _executionLedger.AbandonAsync(executionKey, acquired.LeaseToken, CancellationToken.None);
                return result;
            }
            if (result.Status is ToolExecutionStatus.Completed or ToolExecutionStatus.ResultTooLarge)
            {
                if (compensationPreparation is not null)
                {
                    await compensationService!.ActivateAsync(compensationPreparation, CancellationToken.None);
                    result = result with { CompensationId = compensationPreparation.Id };
                }
                await _executionLedger.CompleteAsync(executionKey, acquired.LeaseToken, result,
                    CancellationToken.None);
                return result;
            }
            if (compensationPreparation is not null)
                await compensationService!.MarkForwardOutcomeUnknownAsync(compensationPreparation,
                    CancellationToken.None);
            await _executionLedger.MarkOutcomeUnknownAsync(executionKey, acquired.LeaseToken, CancellationToken.None);
            return await OutcomeUnknownAsync(runId, tool.Descriptor.Name, CancellationToken.None);
        }
        catch
        {
            if (compensationPreparation is not null)
            {
                if (sideEffectStarted)
                    await compensationService!.MarkForwardOutcomeUnknownAsync(compensationPreparation,
                        CancellationToken.None);
                else
                    await compensationService!.DiscardAsync(compensationPreparation, CancellationToken.None);
            }
            if (sideEffectStarted)
                await _executionLedger.MarkOutcomeUnknownAsync(executionKey, acquired.LeaseToken, CancellationToken.None);
            else
                await _executionLedger.AbandonAsync(executionKey, acquired.LeaseToken, CancellationToken.None);
            throw;
        }
    }

    private async Task<ToolExecutionResult> ExecuteCoreAsync(string runId, IServerTool tool, JsonElement arguments,
        AccessContext access, string? approvalId, Func<CancellationToken, Task>? beforeExecute,
        CancellationToken cancellationToken)
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
            if (beforeExecute is not null) await beforeExecute(cancellationToken);
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

    private Task<ToolExecutionResult> OutcomeUnknownAsync(string runId, string toolName,
        CancellationToken cancellationToken)
    {
        var decision = new SafetyDecision(SafetyAction.Refuse, "TOOL_EXECUTION_OUTCOME_UNKNOWN",
            "上一次工具执行可能已经产生副作用，系统不会自动重试，请人工核对目标状态。");
        var trace = new List<TraceStep>
        {
            Step("tool.idempotency", "outcome_unknown", new Dictionary<string, object?> { ["code"] = decision.Code })
        };
        return CompleteAsync(runId, toolName, ToolExecutionStatus.OutcomeUnknown, null, decision, false, trace,
            cancellationToken);
    }

    private Task<ToolExecutionResult> ReconciledAsync(string runId, string toolName,
        CancellationToken cancellationToken)
    {
        var decision = new SafetyDecision(SafetyAction.Allow, "TOOL_OUTCOME_RECONCILED_APPLIED",
            "两名独立对账人员已根据目标状态证据确认操作生效。");
        return CompleteAsync(runId, toolName, ToolExecutionStatus.Reconciled, null, decision, true,
            [Step("tool.idempotency", "reconciled", new Dictionary<string, object?> { ["code"] = decision.Code })],
            cancellationToken);
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

    private TraceStep Step(string name, string outcome, IReadOnlyDictionary<string, object?> details) =>
        new(name, outcome, timeProvider.GetUtcNow(), details);

    private static string CreateExecutionKey(AccessContext access, string toolName, string idempotencyKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{access.TenantId}\u001f{access.SubjectId}\u001f{toolName.ToLowerInvariant()}\u001f{idempotencyKey}")));
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
public sealed class MemoryDeleteTool(IMemoryWorkflowService memoryWorkflow) : IManualReconciliationServerTool
{
    public ToolDescriptor Descriptor { get; } = new("memory.delete", "按标识和预期版本删除当前用户的记忆。",
        ToolOperationRisk.Mutation, TimeSpan.FromSeconds(3), 2 * 1024, true);

    public string CompensationUnavailableCode => "TOOL_COMPENSATION_SOURCE_NOT_RETAINED";

    public string CompensationUnavailableExplanation =>
        "删除前记忆正文、版本和有效期未进入补偿快照，不能安全自动恢复；必须核对目标状态并人工处置。";

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

/// <summary>
/// 使用乐观版本更正当前用户记忆，并在正向执行前捕获旧值、版本和到期时间作为加密补偿快照。
/// </summary>
public sealed class MemoryCorrectTool(IMemoryWorkflowService memoryWorkflow) : ICompensableServerTool
{
    public ToolDescriptor Descriptor { get; } = new("memory.correct", "更正当前用户拥有的版本化记忆。",
        ToolOperationRisk.Mutation, TimeSpan.FromSeconds(3), 2 * 1024, true);

    public string CompensationToolName => "memory.correct.restore";

    public SafetyDecision ValidateArguments(JsonElement arguments)
    {
        var properties = arguments.EnumerateObject().ToArray();
        if (properties.Length is < 3 or > 4 || properties.Any(property =>
                property.Name is not ("memoryId" or "expectedVersion" or "value" or "expiresAt")))
            return Refuse("MEMORY_CORRECT_ARGUMENTS_INVALID",
                "memory.correct 只接受 memoryId、expectedVersion、value 和可选 expiresAt。");
        if (!arguments.TryGetProperty("memoryId", out var memoryId) || memoryId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(memoryId.GetString()) || memoryId.GetString()!.Length > 128)
            return Refuse("MEMORY_ID_INVALID", "memoryId 格式无效。");
        if (!arguments.TryGetProperty("expectedVersion", out var expectedVersion)
            || expectedVersion.ValueKind != JsonValueKind.Number || !expectedVersion.TryGetInt32(out var version)
            || version <= 0)
            return Refuse("MEMORY_VERSION_INVALID", "expectedVersion 必须大于 0。");
        if (!arguments.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 1_000)
            return Refuse("MEMORY_VALUE_INVALID", "value 不能为空且不能超过 1000 个字符。");
        if (arguments.TryGetProperty("expiresAt", out var expiresAt)
            && expiresAt.ValueKind is not JsonValueKind.Null
            && (expiresAt.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(expiresAt.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out _)))
            return Refuse("MEMORY_EXPIRATION_INVALID", "expiresAt 必须是合法的 ISO-8601 时间或 null。");
        return new SafetyDecision(SafetyAction.Allow, "TOOL_ARGUMENTS_VALID", "记忆更正参数通过校验。");
    }

    public async Task<JsonElement> CaptureCompensationStateAsync(ToolExecutionContext context, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var memoryId = arguments.GetProperty("memoryId").GetString()!.Trim();
        var expectedVersion = arguments.GetProperty("expectedVersion").GetInt32();
        var record = (await memoryWorkflow.ListAsync(context.Access, cancellationToken: cancellationToken))
            .SingleOrDefault(item => string.Equals(item.Id, memoryId, StringComparison.Ordinal));
        if (record is null)
            throw new MemoryWorkflowException("MEMORY_NOT_FOUND", "没有找到当前用户可更正的记忆。",
                MemoryWorkflowErrorKind.NotFound);
        if (record.Version != expectedVersion)
            throw new MemoryWorkflowException("MEMORY_VERSION_CONFLICT", "记忆版本已经变化，请重新读取后再操作。",
                MemoryWorkflowErrorKind.Conflict);

        return JsonSerializer.SerializeToElement(new MemoryCorrectionSnapshot(record.Id, record.Value,
            record.Version + 1, record.ExpiresAt));
    }

    public async Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var corrected = await memoryWorkflow.CorrectAsync(arguments.GetProperty("memoryId").GetString()!.Trim(),
            new CorrectMemoryCommand(arguments.GetProperty("value").GetString()!,
                arguments.GetProperty("expectedVersion").GetInt32(), ReadExpiresAt(arguments)),
            context.Access, cancellationToken);
        return JsonSerializer.SerializeToElement(new { corrected = true, memoryId = corrected.Id, corrected.Version });
    }

    public async Task<JsonElement> CompensateAsync(ToolExecutionContext context, JsonElement compensationState,
        CancellationToken cancellationToken = default)
    {
        var snapshot = compensationState.Deserialize<MemoryCorrectionSnapshot>()
            ?? throw new InvalidOperationException("记忆更正补偿快照无效。");
        var restored = await memoryWorkflow.CorrectAsync(snapshot.MemoryId,
            new CorrectMemoryCommand(snapshot.PreviousValue, snapshot.ExpectedVersionAfterForward,
                snapshot.PreviousExpiresAt), context.Access, cancellationToken);
        return JsonSerializer.SerializeToElement(new { restored = true, memoryId = restored.Id, restored.Version });
    }

    private static DateTimeOffset? ReadExpiresAt(JsonElement arguments) =>
        arguments.TryGetProperty("expiresAt", out var expiresAt) && expiresAt.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(expiresAt.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    private static SafetyDecision Refuse(string code, string message) =>
        new(SafetyAction.Refuse, code, message);

    private sealed record MemoryCorrectionSnapshot(
        string MemoryId,
        string PreviousValue,
        int ExpectedVersionAfterForward,
        DateTimeOffset PreviousExpiresAt);
}
