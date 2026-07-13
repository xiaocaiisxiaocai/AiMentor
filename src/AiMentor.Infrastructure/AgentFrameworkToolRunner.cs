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

    public AgentFrameworkToolRunner(IChatClient chatClient, IToolRegistry registry, IToolExecutor executor,
        IInputSafetyService inputSafety, ITraceSink traceSink, AgentExecutionOptions options, TimeProvider timeProvider)
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

            if (guard.LimitDecision is not null)
                return await CompleteAsync(runId, AgentRunStatus.LimitExceeded, guard.LimitDecision.Message,
                    guard.LimitDecision, guard.Steps, trace, cancellationToken);

            var answer = response.Text?.Trim() ?? string.Empty;
            var outputDecision = ReviewOutput(answer);
            trace.Add(Step("agent.output_safety", outputDecision.Action.ToString(),
                new Dictionary<string, object?> { ["code"] = outputDecision.Code }));
            var status = outputDecision.Action == SafetyAction.Allow ? AgentRunStatus.Completed : AgentRunStatus.Refused;
            return await CompleteAsync(runId, status,
                status == AgentRunStatus.Completed ? answer : outputDecision.Message,
                outputDecision, guard.Steps, trace, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var decision = new SafetyDecision(SafetyAction.Refuse, "AGENT_RUN_TIMEOUT", "Agent 运行超过服务器总时限。");
            trace.Add(Step("agent.completed", "timeout", new Dictionary<string, object?> { ["code"] = decision.Code }));
            return await CompleteAsync(runId, AgentRunStatus.LimitExceeded, decision.Message, decision,
                guard.Steps, trace, CancellationToken.None);
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

    private static AIFunction CreateFunction(ToolDescriptor descriptor, ToolRunGuard guard)
    {
        var functionOptions = new AIFunctionFactoryOptions
        {
            Name = ToFunctionName(descriptor.Name),
            Description = $"{descriptor.Description} 服务器工具标识：{descriptor.Name}。参数必须放在 arguments JSON 对象中。"
                + (descriptor.Risk == ToolOperationRisk.ReadOnly
                    ? " 只读工具无需 approvalId。"
                    : " 修改性工具必须提供由服务器审批 API 签发的 approvalId，模型不得自行生成。")
        };
        if (descriptor.Risk == ToolOperationRisk.ReadOnly)
        {
            Func<JsonElement, CancellationToken, Task<string>> readOnlyCallback =
                (arguments, cancellationToken) => guard.ExecuteAsync(descriptor.Name, arguments, null, cancellationToken);
            return AIFunctionFactory.Create(readOnlyCallback, functionOptions);
        }

        Func<JsonElement, string?, CancellationToken, Task<string>> approvalCallback =
            (arguments, approvalId, cancellationToken) =>
                guard.ExecuteAsync(descriptor.Name, arguments, approvalId, cancellationToken);
        return AIFunctionFactory.Create(approvalCallback, functionOptions);
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
}
