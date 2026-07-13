using System.Text.Json;
using AiMentor.Domain;

namespace AiMentor.Application;

/// <summary>定义服务器拥有的工具实现；风险等级只能来自其不可由模型覆盖的描述符。</summary>
public interface IServerTool
{
    ToolDescriptor Descriptor { get; }
    SafetyDecision ValidateArguments(JsonElement arguments);
    Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>向工具传递已验证身份和审计运行标识。</summary>
public sealed record ToolExecutionContext(AccessContext Access, string RunId);

/// <summary>提供唯一的服务器工具白名单和名称解析入口。</summary>
public interface IToolRegistry
{
    IReadOnlyList<ToolDescriptor> Descriptors { get; }
    bool TryGet(string toolName, out IServerTool? tool);
}

/// <summary>在超时、参数、授权、审批和结果预算门禁内执行注册工具。</summary>
public interface IToolExecutor
{
    Task<ToolExecutionResult> ExecuteAsync(string toolName, JsonElement arguments, AccessContext access,
        string? idempotencyKey = null, string? approvalId = null, CancellationToken cancellationToken = default);
}

/// <summary>执行有界 Agent 规划并返回可审计的工具步骤和终止状态。</summary>
public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(string input, AccessContext access, string? correlationId = null,
        CancellationToken cancellationToken = default);
    Task<AgentRunResult> ResumeAsync(string runId, AccessContext access,
        CancellationToken cancellationToken = default);
}

/// <summary>配置模型迭代、工具次数、结果大小和总运行时预算。</summary>
public sealed class AgentExecutionOptions
{
    public int MaximumModelIterations { get; init; } = 4;
    public int MaximumToolCalls { get; init; } = 3;
    public int MaximumCumulativeToolResultBytes { get; init; } = 16 * 1024;
    public int MaximumAnswerCharacters { get; init; } = 4_000;
    public int MaximumPendingApprovalRuns { get; init; } = 1_000;
    public TimeSpan MaximumRunTime { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>区分 Agent 暂停运行恢复时的输入、权限、资源和状态错误。</summary>
public enum AgentRunWorkflowErrorKind { Validation, Forbidden, NotFound, Conflict, Capacity }

/// <summary>携带稳定代码且不泄漏会话或工具参数的 Agent 恢复异常。</summary>
public sealed class AgentRunWorkflowException(string code, string message, AgentRunWorkflowErrorKind kind)
    : Exception(message)
{
    public string Code { get; } = code;
    public AgentRunWorkflowErrorKind Kind { get; } = kind;
}

/// <summary>配置单个工具调用的参数、超时和幂等缓存上限。</summary>
public sealed class ToolExecutorOptions
{
    public int MaximumArgumentBytes { get; init; } = 16 * 1024;
    public TimeSpan MaximumTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdempotencyRetention { get; init; } = TimeSpan.FromHours(1);
    public int MaximumIdempotencyEntries { get; init; } = 10_000;
}
