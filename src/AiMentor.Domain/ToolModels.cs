using System.Text.Json;

namespace AiMentor.Domain;

/// <summary>按副作用和权限敏感度划分服务器工具。</summary>
public enum ToolOperationRisk { ReadOnly, Mutation, Privileged }
/// <summary>描述工具调用经过安全门禁后的终态。</summary>
public enum ToolExecutionStatus { Completed, Rejected, RequiresApproval, TimedOut, ResultTooLarge, Failed, OutcomeUnknown }

/// <summary>服务器维护的工具能力、风险和资源限制元数据。</summary>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    ToolOperationRisk Risk,
    TimeSpan Timeout,
    int MaximumResultBytes,
    bool RequiresIdempotencyKey);

/// <summary>包含安全决策、输出和审计轨迹的工具执行结果。</summary>
public sealed record ToolExecutionResult(
    string RunId,
    string ToolName,
    ToolExecutionStatus Status,
    JsonElement? Output,
    SafetyDecision Safety,
    bool IdempotentReplay,
    IReadOnlyList<TraceStep> Trace);

/// <summary>区分正常完成、等待人工审批、安全拒绝、预算终止和系统失败。</summary>
public enum AgentRunStatus { Completed, AwaitingApproval, Refused, LimitExceeded, Failed }

/// <summary>记录一次真实工具执行的序号、结果大小和独立运行标识。</summary>
public sealed record AgentToolStep(
    int Sequence,
    string ToolName,
    ToolExecutionStatus Status,
    string Code,
    string ToolRunId,
    int ResultBytes);

/// <summary>向调用方公开不含参数值的 Agent 工具审批暂停点。</summary>
public sealed record AgentApprovalCheckpoint(
    string ApprovalId,
    string ToolName,
    ToolOperationRisk Risk,
    IReadOnlyList<string> ArgumentNames,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>返回受限 Agent 的最终回答、安全结论和完整可审计步骤。</summary>
public sealed record AgentRunResult(
    string RunId,
    AgentRunStatus Status,
    string Answer,
    SafetyDecision Safety,
    IReadOnlyList<AgentToolStep> ToolSteps,
    IReadOnlyList<TraceStep> Trace,
    AgentApprovalCheckpoint? Approval = null);
