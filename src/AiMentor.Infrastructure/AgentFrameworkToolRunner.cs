using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AiMentor.Infrastructure;

/// <summary>
/// 使用 Agent Framework 的函数调用循环，但所有函数最终只能进入 IToolExecutor。
/// 模型负责选择，服务器负责授权、预算、执行和终止。
/// </summary>
public sealed class AgentFrameworkToolRunner : IAgentRunner
{
    private readonly ChatClientAgent _agent;
    private readonly IToolRegistry _registry;
    private readonly IToolExecutor _executor;
    private readonly IInputSafetyService _inputSafety;
    private readonly ITraceSink _traceSink;
    private readonly AgentExecutionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IToolApprovalService? _approvalService;
    private readonly ConcurrentDictionary<string, PendingAgentRun> _pendingRuns = new(StringComparer.Ordinal);

    public AgentFrameworkToolRunner(IChatClient chatClient, IToolRegistry registry, IToolExecutor executor,
        IInputSafetyService inputSafety, ITraceSink traceSink, AgentExecutionOptions options, TimeProvider timeProvider,
        IToolApprovalService? approvalService = null)
    {
        ValidateOptions(options);
        var functionClient = new FunctionInvokingChatClient(chatClient)
        {
            MaximumIterationsPerRequest = options.MaximumModelIterations,
            MaximumConsecutiveErrorsPerRequest = 1,
            AllowConcurrentInvocation = false,
            IncludeDetailedErrors = false
        };
        _agent = new ChatClientAgent(functionClient, new ChatClientAgentOptions
        {
            Name = "AiMentorToolAgent",
            Description = "只使用服务器注册且受安全执行器约束的工具完成受限任务。",
            ChatOptions = new ChatOptions
            {
                Instructions = "你是受限企业 Agent。仅在确有必要时选择已提供工具；不得虚构工具或结果；工具失败时明确说明失败；回答必须以本次工具结果为依据。"
            },
            UseProvidedChatClientAsIs = true
        });
        _registry = registry;
        _executor = executor;
        _inputSafety = inputSafety;
        _traceSink = traceSink;
        _options = options;
        _timeProvider = timeProvider;
        _approvalService = approvalService;
    }

    public async Task<AgentRunResult> RunAsync(string input, AccessContext access, string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var runId = NormalizeRunId(correlationId);
        var trace = new List<TraceStep>();
        var inputDecision = _inputSafety.Review(input);
        trace.Add(Step("agent.input_safety", inputDecision.Action.ToString(), new Dictionary<string, object?>
        {
            ["code"] = inputDecision.Code,
            ["policyVersion"] = _inputSafety.PolicyVersion
        }));
        if (inputDecision.Action == SafetyAction.Refuse)
            return await CompleteAsync(runId, AgentRunStatus.Refused, inputDecision.Message, inputDecision, [], trace,
                cancellationToken);

        var guard = new ToolRunGuard(runId, access, _executor, _options, trace, _timeProvider);
        var tools = _registry.Descriptors
            .Select(descriptor => CreateFunction(descriptor, guard))
            .Cast<AITool>()
            .ToList();
        trace.Add(Step("agent.plan.started", "ok", new Dictionary<string, object?>
        {
            ["availableTools"] = tools.Count,
            ["maximumModelIterations"] = _options.MaximumModelIterations,
            ["maximumToolCalls"] = _options.MaximumToolCalls
        }));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.MaximumRunTime);
        try
        {
            var session = await _agent.CreateSessionAsync(timeoutSource.Token);
            var runOptions = new ChatClientAgentRunOptions(new ChatOptions
            {
                Tools = tools,
                AllowMultipleToolCalls = false,
                ToolMode = ChatToolMode.Auto
            });
            var response = await _agent.RunAsync(input.Trim(), session, runOptions, timeoutSource.Token);
            return await ProcessResponseAsync(runId, response, session, runOptions, guard, access, trace,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var decision = new SafetyDecision(SafetyAction.Refuse, "AGENT_RUN_TIMEOUT", "Agent 运行超过服务器总时限。");
            trace.Add(Step("agent.completed", "timeout", new Dictionary<string, object?> { ["code"] = decision.Code }));
            return await CompleteAsync(runId, AgentRunStatus.LimitExceeded, decision.Message, decision,
                guard.Steps, trace, CancellationToken.None);
        }
        catch (AgentRunWorkflowException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (guard.LimitDecision is not null)
            {
                trace.Add(Step("agent.limit_triggered", "limit_exceeded", new Dictionary<string, object?>
                {
                    ["code"] = guard.LimitDecision.Code,
                    ["exceptionType"] = exception.GetType().Name
                }));
                return await CompleteAsync(runId, AgentRunStatus.LimitExceeded, guard.LimitDecision.Message,
                    guard.LimitDecision, guard.Steps, trace, cancellationToken);
            }
            var decision = new SafetyDecision(SafetyAction.Refuse, "AGENT_RUN_FAILED", "Agent 运行失败，未返回不完整结果。");
            trace.Add(Step("agent.completed", "failed", new Dictionary<string, object?>
            {
                ["code"] = decision.Code,
                ["exceptionType"] = exception.GetType().Name
            }));
            return await CompleteAsync(runId, AgentRunStatus.Failed, decision.Message, decision, guard.Steps, trace,
                cancellationToken);
        }
    }

    public async Task<AgentRunResult> ResumeAsync(string runId, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        var normalizedRunId = RequireRunId(runId);
        if (!_pendingRuns.TryGetValue(normalizedRunId, out var pending))
            throw WorkflowFailure("AGENT_RUN_NOT_FOUND", "没有找到可恢复的 Agent 运行。",
                AgentRunWorkflowErrorKind.NotFound);
        if (!SameOwner(pending.Access, access))
            throw WorkflowFailure("AGENT_RUN_RESUME_FORBIDDEN", "只有原始调用者可以恢复 Agent 运行。",
                AgentRunWorkflowErrorKind.Forbidden);

        await pending.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!_pendingRuns.TryGetValue(normalizedRunId, out var current) || !ReferenceEquals(current, pending))
                throw WorkflowFailure("AGENT_RUN_ALREADY_RESUMED", "Agent 运行已经恢复或终止。",
                    AgentRunWorkflowErrorKind.Conflict);
            if (_approvalService is null)
                throw WorkflowFailure("AGENT_APPROVAL_SERVICE_UNAVAILABLE", "审批服务不可用，运行不能恢复。",
                    AgentRunWorkflowErrorKind.Conflict);

            var approval = await _approvalService.GetAsync(pending.Checkpoint.ApprovalId, access, cancellationToken);
            if (approval.Status == ToolApprovalStatus.Pending)
                return CreateAwaitingResult(pending, approval);

            if (!_pendingRuns.TryRemove(new KeyValuePair<string, PendingAgentRun>(normalizedRunId, pending)))
                throw WorkflowFailure("AGENT_RUN_ALREADY_RESUMED", "Agent 运行已经恢复或终止。",
                    AgentRunWorkflowErrorKind.Conflict);

            var approved = approval.Status == ToolApprovalStatus.Approved;
            if (approved) pending.Guard.SetApprovalId(approval.Id);
            var reason = approval.DecisionReason ?? (approval.Status == ToolApprovalStatus.Expired
                ? "审批已过期。" : "审批未获批准。");
            pending.Trace.Add(Step("agent.approval.resumed", approved ? "approved" : "rejected",
                new Dictionary<string, object?>
                {
                    ["approvalId"] = approval.Id,
                    ["tool"] = approval.ToolName,
                    ["approvalStatus"] = approval.Status.ToString()
                }));

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_options.MaximumRunTime);
            var frameworkResponse = pending.FrameworkRequest.CreateResponse(approved, reason);
            var response = await _agent.RunAsync(
                [new ChatMessage(ChatRole.User, [frameworkResponse])], pending.Session, pending.RunOptions,
                timeoutSource.Token);

            if (!approved)
            {
                var code = approval.Status == ToolApprovalStatus.Expired
                    ? "AGENT_TOOL_APPROVAL_EXPIRED" : "AGENT_TOOL_APPROVAL_REJECTED";
                var decision = new SafetyDecision(SafetyAction.Refuse, code,
                    approval.Status == ToolApprovalStatus.Expired ? "工具审批已过期，Agent 运行终止。" : "工具审批被拒绝，Agent 运行终止。");
                return await CompleteAsync(normalizedRunId, AgentRunStatus.Refused, decision.Message, decision,
                    pending.Guard.Steps, pending.Trace, cancellationToken);
            }

            return await ProcessResponseAsync(normalizedRunId, response, pending.Session, pending.RunOptions,
                pending.Guard, access, pending.Trace, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var decision = new SafetyDecision(SafetyAction.Refuse, "AGENT_RESUME_TIMEOUT", "Agent 恢复执行超过服务器总时限。");
            return await CompleteAsync(normalizedRunId, AgentRunStatus.LimitExceeded, decision.Message, decision,
                pending.Guard.Steps, pending.Trace, CancellationToken.None);
        }
        catch (AgentRunWorkflowException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _pendingRuns.TryRemove(new KeyValuePair<string, PendingAgentRun>(normalizedRunId, pending));
            var decision = new SafetyDecision(SafetyAction.Refuse, "AGENT_RESUME_FAILED", "Agent 恢复失败，未返回不完整结果。");
            pending.Trace.Add(Step("agent.resume", "failed", new Dictionary<string, object?>
            {
                ["code"] = decision.Code,
                ["exceptionType"] = exception.GetType().Name
            }));
            return await CompleteAsync(normalizedRunId, AgentRunStatus.Failed, decision.Message, decision,
                pending.Guard.Steps, pending.Trace, cancellationToken);
        }
        finally
        {
            pending.Gate.Release();
        }
    }

    private async Task<AgentRunResult> ProcessResponseAsync(string runId, AgentResponse response, AgentSession session,
        ChatClientAgentRunOptions runOptions, ToolRunGuard guard, AccessContext access, List<TraceStep> trace,
        CancellationToken cancellationToken)
    {
        if (guard.LimitDecision is not null)
            return await CompleteAsync(runId, AgentRunStatus.LimitExceeded, guard.LimitDecision.Message,
                guard.LimitDecision, guard.Steps, trace, cancellationToken);

        var approvalRequests = response.Messages.SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>().ToArray();
        if (approvalRequests.Length > 0)
            return await PauseForApprovalAsync(runId, approvalRequests, session, runOptions, guard, access, trace,
                cancellationToken);

        var answer = response.Text?.Trim() ?? string.Empty;
        var outputDecision = ReviewOutput(answer);
        trace.Add(Step("agent.output_safety", outputDecision.Action.ToString(),
            new Dictionary<string, object?> { ["code"] = outputDecision.Code }));
        var status = outputDecision.Action == SafetyAction.Allow ? AgentRunStatus.Completed : AgentRunStatus.Refused;
        return await CompleteAsync(runId, status,
            status == AgentRunStatus.Completed ? answer : outputDecision.Message,
            outputDecision, guard.Steps, trace, cancellationToken);
    }

    private async Task<AgentRunResult> PauseForApprovalAsync(string runId,
        ToolApprovalRequestContent[] requests, AgentSession session, ChatClientAgentRunOptions runOptions,
        ToolRunGuard guard, AccessContext access, List<TraceStep> trace, CancellationToken cancellationToken)
    {
        if (_approvalService is null)
            return await CompleteAsync(runId, AgentRunStatus.Failed, "审批服务不可用，修改性工具保持关闭。",
                new SafetyDecision(SafetyAction.Refuse, "AGENT_APPROVAL_SERVICE_UNAVAILABLE", "审批服务不可用，修改性工具保持关闭。"),
                guard.Steps, trace, cancellationToken);
        if (requests.Length != 1 || requests[0].ToolCall is not FunctionCallContent functionCall)
            return await CompleteAsync(runId, AgentRunStatus.Failed, "Agent 返回了无法安全处理的审批请求。",
                new SafetyDecision(SafetyAction.Refuse, "AGENT_APPROVAL_REQUEST_INVALID", "Agent 返回了无法安全处理的审批请求。"),
                guard.Steps, trace, cancellationToken);

        var descriptor = _registry.Descriptors.FirstOrDefault(item =>
            string.Equals(ToFunctionName(item.Name), functionCall.Name, StringComparison.Ordinal));
        if (descriptor is null || descriptor.Risk == ToolOperationRisk.ReadOnly)
            return await CompleteAsync(runId, AgentRunStatus.Failed, "审批请求没有匹配的修改性服务器工具。",
                new SafetyDecision(SafetyAction.Refuse, "AGENT_APPROVAL_TOOL_MISMATCH", "审批请求没有匹配的修改性服务器工具。"),
                guard.Steps, trace, cancellationToken);

        PruneExpiredPendingRuns();
        if (_pendingRuns.Count >= _options.MaximumPendingApprovalRuns)
            throw WorkflowFailure("AGENT_PENDING_CAPACITY_EXCEEDED", "等待审批的 Agent 运行已达到容量上限。",
                AgentRunWorkflowErrorKind.Capacity);

        var arguments = ExtractToolArguments(functionCall);
        var approval = await _approvalService.RequestAsync(descriptor.Name, arguments,
            $"Agent 运行 {runId} 请求执行修改性工具。", access, cancellationToken);
        var checkpoint = new AgentApprovalCheckpoint(approval.Id, approval.ToolName, approval.Risk,
            approval.ArgumentNames, approval.CreatedAt, approval.ExpiresAt);
        trace.Add(Step("agent.approval.required", "pending", new Dictionary<string, object?>
        {
            ["approvalId"] = approval.Id,
            ["tool"] = approval.ToolName,
            ["expiresAt"] = approval.ExpiresAt
        }));
        var pending = new PendingAgentRun(access, session, runOptions, guard, trace, requests[0], checkpoint);
        if (!_pendingRuns.TryAdd(runId, pending))
            throw WorkflowFailure("AGENT_RUN_ALREADY_PENDING", "相同运行标识已有等待审批的 Agent 运行。",
                AgentRunWorkflowErrorKind.Conflict);
        await _traceSink.WriteAsync(runId, trace, cancellationToken);
        return new AgentRunResult(runId, AgentRunStatus.AwaitingApproval, "工具调用等待独立审批人裁决。",
            new SafetyDecision(SafetyAction.RequireApproval, "AGENT_TOOL_APPROVAL_REQUIRED", "工具调用等待独立审批人裁决。"),
            guard.Steps.ToArray(), trace.ToArray(), checkpoint);
    }

    private static AIFunction CreateFunction(ToolDescriptor descriptor, ToolRunGuard guard)
    {
        var functionOptions = new AIFunctionFactoryOptions
        {
            Name = ToFunctionName(descriptor.Name),
            Description = $"{descriptor.Description} 服务器工具标识：{descriptor.Name}。参数必须放在 arguments JSON 对象中。"
                + (descriptor.Risk == ToolOperationRisk.ReadOnly
                    ? " 只读工具无需 approvalId。"
                    : " 修改性工具调用会暂停并进入服务器审批流程，模型不得尝试自行批准。")
        };
        Func<JsonElement, CancellationToken, Task<string>> callback =
            (arguments, cancellationToken) =>
                guard.ExecuteAsync(descriptor.Name, arguments, guard.ActiveApprovalId, cancellationToken);
        var function = AIFunctionFactory.Create(callback, functionOptions);
        // 这里只声明单次原生暂停；长期“永远批准”会扩大授权范围，故不接入 ToolApprovalAgent 规则。
        return descriptor.Risk == ToolOperationRisk.ReadOnly ? function : new ApprovalRequiredAIFunction(function);
    }

    private SafetyDecision ReviewOutput(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return new SafetyDecision(SafetyAction.Refuse, "AGENT_OUTPUT_EMPTY", "Agent 未形成有效回答。");
        if (answer.Length > _options.MaximumAnswerCharacters)
            return new SafetyDecision(SafetyAction.Refuse, "AGENT_OUTPUT_TOO_LARGE", "Agent 回答超过服务器允许的长度。");
        var decision = _inputSafety.Review(answer);
        return decision.Action == SafetyAction.Refuse
            ? new SafetyDecision(SafetyAction.Refuse, "AGENT_OUTPUT_UNSAFE", "Agent 回答未通过安全审核。")
            : new SafetyDecision(SafetyAction.Allow, "AGENT_OUTPUT_SAFE", "Agent 回答通过安全审核。");
    }

    private async Task<AgentRunResult> CompleteAsync(string runId, AgentRunStatus status, string answer,
        SafetyDecision safety, List<AgentToolStep> steps, List<TraceStep> trace,
        CancellationToken cancellationToken)
    {
        trace.Add(Step("agent.completed", status.ToString(), new Dictionary<string, object?>
        {
            ["code"] = safety.Code,
            ["toolCalls"] = steps.Count
        }));
        await _traceSink.WriteAsync(runId, trace, cancellationToken);
        return new AgentRunResult(runId, status, answer, safety, steps.ToArray(), trace.ToArray());
    }

    private TraceStep Step(string name, string outcome, IReadOnlyDictionary<string, object?> details) =>
        new(name, outcome, _timeProvider.GetUtcNow(), details);

    private static string ToFunctionName(string toolName) => toolName.Replace('.', '_').Replace('-', '_');

    private static string NormalizeRunId(string? value)
    {
        var normalized = value?.Trim();
        return normalized is { Length: > 0 and <= 128 }
            && normalized.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
                ? normalized : Guid.NewGuid().ToString("N");
    }

    private static void ValidateOptions(AgentExecutionOptions options)
    {
        if (options.MaximumModelIterations < 2 || options.MaximumToolCalls <= 0
            || options.MaximumCumulativeToolResultBytes <= 0 || options.MaximumAnswerCharacters <= 0
            || options.MaximumPendingApprovalRuns <= 0
            || options.MaximumRunTime <= TimeSpan.Zero)
            throw new InvalidOperationException("Agent 执行预算配置无效。");
    }

    private sealed class ToolRunGuard(string agentRunId, AccessContext access, IToolExecutor executor,
        AgentExecutionOptions options, List<TraceStep> trace, TimeProvider timeProvider)
    {
        private readonly HashSet<string> _fingerprints = new(StringComparer.Ordinal);
        private int _cumulativeResultBytes;

        public List<AgentToolStep> Steps { get; } = [];
        public SafetyDecision? LimitDecision { get; private set; }
        public string? ActiveApprovalId { get; private set; }
        public string AgentRunId => agentRunId;

        public void SetApprovalId(string approvalId) => ActiveApprovalId = approvalId;

        public async Task<string> ExecuteAsync(string toolName, JsonElement arguments, string? approvalId,
            CancellationToken cancellationToken)
        {
            var normalized = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>())
                : arguments.Clone();
            if (Steps.Count >= options.MaximumToolCalls)
                return RejectLimit("AGENT_TOOL_CALL_LIMIT", "Agent 工具调用次数超过服务器预算。", toolName);

            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{toolName}\u001f{normalized.GetRawText()}")));
            if (!_fingerprints.Add(fingerprint))
                return RejectLimit("AGENT_REPEATED_TOOL_CALL", "Agent 产生重复工具调用，已终止执行。", toolName);

            var result = await executor.ExecuteAsync(toolName, normalized, access,
                $"agent-{agentRunId}-{Steps.Count + 1}", approvalId, cancellationToken);
            var resultBytes = result.Output is null ? 0 : Encoding.UTF8.GetByteCount(result.Output.Value.GetRawText());
            if (_cumulativeResultBytes + resultBytes > options.MaximumCumulativeToolResultBytes)
            {
                LimitDecision = new SafetyDecision(SafetyAction.Refuse, "AGENT_TOOL_RESULT_BUDGET",
                    "Agent 工具结果累计大小超过服务器预算。");
                Steps.Add(new AgentToolStep(Steps.Count + 1, result.ToolName, ToolExecutionStatus.ResultTooLarge,
                    LimitDecision.Code, result.RunId, resultBytes));
                trace.Add(NewStep("agent.tool_result", "rejected", toolName, LimitDecision.Code, Steps.Count));
                return SerializeEnvelope(ToolExecutionStatus.ResultTooLarge, LimitDecision.Code, null);
            }
            _cumulativeResultBytes += resultBytes;
            Steps.Add(new AgentToolStep(Steps.Count + 1, result.ToolName, result.Status, result.Safety.Code,
                result.RunId, resultBytes));
            trace.Add(NewStep("agent.tool", result.Status.ToString(), result.ToolName, result.Safety.Code, Steps.Count));
            return SerializeEnvelope(result.Status, result.Safety.Code, result.Output);
        }

        private string RejectLimit(string code, string message, string toolName)
        {
            LimitDecision = new SafetyDecision(SafetyAction.Refuse, code, message);
            trace.Add(NewStep("agent.tool", "limit_exceeded", toolName, code, Steps.Count + 1));
            return SerializeEnvelope(ToolExecutionStatus.Rejected, code, null);
        }

        private TraceStep NewStep(string name, string outcome, string toolName, string code, int sequence) =>
            new(name, outcome, timeProvider.GetUtcNow(), new Dictionary<string, object?>
            {
                ["tool"] = toolName,
                ["code"] = code,
                ["sequence"] = sequence
            });

        private static string SerializeEnvelope(ToolExecutionStatus status, string code, JsonElement? output) =>
            JsonSerializer.Serialize(new { status = status.ToString(), code, output });
    }

    private static AgentRunResult CreateAwaitingResult(PendingAgentRun pending, ToolApprovalRequest approval) =>
        new(pending.Guard.AgentRunId, AgentRunStatus.AwaitingApproval, "工具调用仍在等待独立审批人裁决。",
            new SafetyDecision(SafetyAction.RequireApproval, "AGENT_TOOL_APPROVAL_PENDING", "工具调用仍在等待独立审批人裁决。"),
            pending.Guard.Steps.ToArray(), pending.Trace.ToArray(), pending.Checkpoint with
            {
                ExpiresAt = approval.ExpiresAt
            });

    private void PruneExpiredPendingRuns()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var item in _pendingRuns)
        {
            if (item.Value.Checkpoint.ExpiresAt <= now)
                _pendingRuns.TryRemove(new KeyValuePair<string, PendingAgentRun>(item.Key, item.Value));
        }
    }

    private static JsonElement ExtractToolArguments(FunctionCallContent functionCall)
    {
        if (functionCall.Arguments is null || !functionCall.Arguments.TryGetValue("arguments", out var value)
            || value is null)
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        var element = value is JsonElement json ? json.Clone() : JsonSerializer.SerializeToElement(value);
        return element.ValueKind == JsonValueKind.Object
            ? element : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
    }

    private static string RequireRunId(string? value)
    {
        var normalized = value?.Trim();
        if (normalized is not { Length: > 0 and <= 128 }
            || !normalized.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))
            throw WorkflowFailure("AGENT_RUN_ID_INVALID", "Agent 运行标识无效。", AgentRunWorkflowErrorKind.Validation);
        return normalized;
    }

    private static bool SameOwner(AccessContext left, AccessContext right) =>
        string.Equals(left.TenantId, right.TenantId, StringComparison.Ordinal)
        && string.Equals(left.SubjectId, right.SubjectId, StringComparison.Ordinal);

    private static AgentRunWorkflowException WorkflowFailure(string code, string message,
        AgentRunWorkflowErrorKind kind) => new(code, message, kind);

    private sealed record PendingAgentRun(
        AccessContext Access,
        AgentSession Session,
        ChatClientAgentRunOptions RunOptions,
        ToolRunGuard Guard,
        List<TraceStep> Trace,
        ToolApprovalRequestContent FrameworkRequest,
        AgentApprovalCheckpoint Checkpoint)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
