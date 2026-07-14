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

/// <summary>
/// 在持久化账本进入 Executing 后、真实工具产生副作用前提供执行边界。
/// 默认实现必须立即返回；阻塞实现仅用于受控故障验收，不能承载业务逻辑。
/// </summary>
public interface IToolExecutionBarrier
{
    Task WaitAfterExecutingAsync(string executionKey, string toolName,
        CancellationToken cancellationToken = default);
}

/// <summary>表示幂等执行账本的占位、回放、冲突、忙碌或结果不确定结论。</summary>
public enum IdempotencyAcquireStatus
{
    Acquired, Replay, InProgress, OutcomeUnknown, ReconciledApplied, FingerprintMismatch, Capacity
}

/// <summary>返回幂等账本原子占位结果、租约令牌或已完成的加密回放结果。</summary>
public sealed record IdempotencyAcquireResult(
    IdempotencyAcquireStatus Status,
    string? LeaseToken = null,
    ToolExecutionResult? ReplayResult = null);

/// <summary>携带建立幂等占位所需的哈希标识和最小化租户审计元数据，不保存原始幂等键或工具参数。</summary>
public sealed record ToolExecutionLedgerRequest(
    string ExecutionKey,
    string RequestFingerprint,
    string RunId,
    string TenantId,
    string SubjectId,
    string ToolName);

/// <summary>公开给授权对账人员的结果不确定记录摘要，不包含工具参数、审批内容或执行结果。</summary>
public sealed record OutcomeUnknownToolExecution(
    string ExecutionKey,
    string TenantId,
    string SubjectId,
    string ToolName,
    string RunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>供内部目标状态探测使用的结果不确定记录，参数摘要不会由 API 返回。</summary>
public sealed record OutcomeUnknownToolExecutionDetail(
    OutcomeUnknownToolExecution Execution,
    string RequestFingerprint);

/// <summary>表示目标系统对一次结果不确定操作给出的只读核验结论。</summary>
public enum ToolOutcomeProbeState { Applied, NotApplied, Indeterminate }

/// <summary>返回不含目标正文的工具处置证据，可供后续双人裁决使用。</summary>
public sealed record ToolOutcomeProbeResult(
    string ExecutionKey,
    string ToolName,
    ToolOutcomeProbeState State,
    string Code,
    string Explanation,
    DateTimeOffset ObservedAt);

/// <summary>区分第一人复核、拒绝、已确认生效和已授权重新执行等裁决结果。</summary>
public enum ToolReconciliationReviewStatus
{
    AwaitingSecondReviewer, Rejected, ResolvedApplied, RetryAuthorized,
    ReviewerMustDiffer, EvidenceChanged, EvidenceExpired, NotFound
}

/// <summary>携带一次原子复核所需的证据摘要和不可逆理由摘要，不保存候选参数或理由原文。</summary>
public sealed record ToolReconciliationReview(
    string ExecutionKey,
    string TenantId,
    string ReviewerSubjectId,
    ToolOutcomeProbeState EvidenceState,
    string EvidenceCode,
    DateTimeOffset EvidenceObservedAt,
    DateTimeOffset EvidenceExpiresAt,
    bool Confirmed,
    string ReasonHash);

/// <summary>返回双人裁决状态以及当前证据有效期，供调用方决定是否等待第二人复核。</summary>
public sealed record ToolReconciliationReviewResult(
    string ExecutionKey,
    ToolReconciliationReviewStatus Status,
    ToolOutcomeProbeState EvidenceState,
    DateTimeOffset EvidenceExpiresAt);

/// <summary>在副作用执行前建立持久化占位，并区分可安全重试和结果不确定状态。</summary>
public interface IToolExecutionLedger
{
    Task<IdempotencyAcquireResult> TryAcquireAsync(ToolExecutionLedgerRequest request,
        TimeSpan leaseDuration, TimeSpan retention, int maximumEntries,
        CancellationToken cancellationToken = default);
    Task MarkExecutingAsync(string executionKey, string leaseToken, CancellationToken cancellationToken = default);
    Task CompleteAsync(string executionKey, string leaseToken, ToolExecutionResult result,
        CancellationToken cancellationToken = default);
    Task MarkOutcomeUnknownAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default);
    Task AbandonAsync(string executionKey, string leaseToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(string tenantId, int limit,
        CancellationToken cancellationToken = default);
    Task<OutcomeUnknownToolExecutionDetail?> GetOutcomeUnknownAsync(string tenantId, string executionKey,
        CancellationToken cancellationToken = default);
    Task<ToolReconciliationReviewResult> SubmitReconciliationReviewAsync(ToolReconciliationReview review,
        CancellationToken cancellationToken = default);
}

/// <summary>按当前访问者租户查询需要外部核验的工具执行，不提供自动重放或清除能力。</summary>
public interface IToolExecutionReconciliationService
{
    Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(AccessContext access, int limit,
        CancellationToken cancellationToken = default);
    Task<ToolOutcomeProbeResult> ProbeOutcomeAsync(AccessContext access, string executionKey, JsonElement arguments,
        CancellationToken cancellationToken = default);
    Task<ToolReconciliationReviewResult> ReviewOutcomeAsync(AccessContext access, string executionKey,
        JsonElement arguments, bool confirmed, string reason, CancellationToken cancellationToken = default);
}

/// <summary>由具体工具实现只读目标状态核验，禁止在探测过程中产生补偿副作用。</summary>
public interface IToolOutcomeProbe
{
    string ToolName { get; }
    Task<ToolOutcomeProbeResult> ProbeAsync(OutcomeUnknownToolExecution execution, JsonElement arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>配置工具执行对账角色和单次查询上限。</summary>
public sealed class ToolExecutionReconciliationOptions
{
    public IReadOnlySet<string> ReconcilerGroups { get; init; } =
        new HashSet<string>(["tool-reconcilers"], StringComparer.OrdinalIgnoreCase);
    public int MaximumPageSize { get; init; } = 100;
    public TimeSpan EvidenceLifetime { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>区分工具执行对账请求的输入错误和授权失败。</summary>
public enum ToolExecutionReconciliationErrorKind { Validation, Forbidden, NotFound }

/// <summary>携带可安全返回给 API 调用方的稳定对账错误代码。</summary>
public sealed class ToolExecutionReconciliationException(
    string code, string message, ToolExecutionReconciliationErrorKind kind) : Exception(message)
{
    public string Code { get; } = code;
    public ToolExecutionReconciliationErrorKind Kind { get; } = kind;
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
    public TimeSpan ResumeLeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ResumeLeaseRenewalInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>持久化 Agent 原生会话、审批请求和执行预算，供其他实例安全恢复。</summary>
public sealed record AgentRunCheckpoint(
    string RunId,
    AccessContext Access,
    string ApprovalId,
    string FrameworkRequestId,
    string FunctionCallId,
    string FunctionName,
    Dictionary<string, object?> FunctionArguments,
    JsonElement SessionState,
    IReadOnlyList<AgentToolStep> ToolSteps,
    IReadOnlyList<TraceStep> Trace,
    IReadOnlyList<string> ToolFingerprints,
    int CumulativeResultBytes,
    AgentApprovalCheckpoint Approval,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>描述原子获取恢复租约后的结果；只有 Acquired 才携带可执行检查点。</summary>
public sealed record AgentRunLeaseResult(
    AgentRunLeaseStatus Status,
    string? LeaseToken = null,
    AgentRunCheckpoint? Checkpoint = null);

/// <summary>区分恢复租约成功、越权、忙碌、过期和不存在。</summary>
public enum AgentRunLeaseStatus { Acquired, Forbidden, Busy, Expired, NotFound }

/// <summary>为暂停运行提供可替换的持久化存储，并通过短租约阻止多实例重复恢复。</summary>
public interface IAgentRunCheckpointStore
{
    Task SavePendingAsync(AgentRunCheckpoint checkpoint, string? leaseToken = null,
        CancellationToken cancellationToken = default);
    Task<AgentRunLeaseResult> TryAcquireAsync(string runId, AccessContext access, string leaseOwner,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    Task<bool> RenewAsync(string runId, string leaseToken, string leaseOwner, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
    Task ReleaseAsync(string runId, string leaseToken, CancellationToken cancellationToken = default);
    Task CompleteAsync(string runId, string leaseToken, CancellationToken cancellationToken = default);
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
    public TimeSpan IdempotencyLeaseDuration { get; init; } = TimeSpan.FromSeconds(45);
}
