using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AiMentor.Domain;

namespace AiMentor.Api;

/// <summary>提交问题及可选会话标识；身份始终由认证上下文提供。</summary>
public sealed record AskV1Request(
    [property: Required, StringLength(4_000, MinimumLength = 1)] string Question,
    [property: StringLength(128, MinimumLength = 1)] string? SessionId = null);

/// <summary>仅供非生产迁移使用的旧版请求，允许显式身份字段。</summary>
public sealed record LegacyAskRequest(
    [property: Required, StringLength(4_000, MinimumLength = 1)] string Question,
    [property: Required, StringLength(128, MinimumLength = 1)] string TenantId,
    [property: Required, StringLength(128, MinimumLength = 1)] string SubjectId,
    [property: Required, MinLength(1), MaxLength(64)] IReadOnlyList<string> Groups);

/// <summary>创建尚未生效的记忆提案。</summary>
public sealed record ProposeMemoryRequest(
    MemoryScope Scope,
    [property: Required, StringLength(128, MinimumLength = 1)] string Key,
    [property: Required, StringLength(1_000, MinimumLength = 1)] string Value,
    [property: StringLength(128, MinimumLength = 1)] string? SessionId = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>携带乐观版本号更正已批准记忆。</summary>
public sealed record CorrectMemoryRequest(
    [property: Required, StringLength(1_000, MinimumLength = 1)] string Value,
    [property: Range(1, int.MaxValue)] int ExpectedVersion,
    DateTimeOffset? ExpiresAt = null);

/// <summary>提交工具参数和可选审批凭据；风险等级不允许由客户端传入。</summary>
public sealed record ExecuteToolRequest(
    Dictionary<string, JsonElement>? Arguments = null,
    [property: StringLength(128, MinimumLength = 1)] string? ApprovalId = null);

/// <summary>为精确的修改性工具参数申请短期、一次性审批。</summary>
public sealed record RequestToolApprovalRequest(
    [property: Required, StringLength(128, MinimumLength = 1)] string ToolName,
    Dictionary<string, JsonElement>? Arguments,
    [property: Required, StringLength(500, MinimumLength = 1)] string Justification);

/// <summary>由独立审批人批准或拒绝工具调用，并记录裁决理由。</summary>
public sealed record DecideToolApprovalRequest(
    bool Approved,
    [property: Required, StringLength(500, MinimumLength = 1)] string Reason);

/// <summary>提交与原调用指纹完全匹配的候选参数，用于只读核验目标系统状态。</summary>
public sealed record ProbeToolOutcomeRequest(
    [property: Required] Dictionary<string, JsonElement> Arguments);

/// <summary>提交当前目标证据、确认意见和理由；理由只以不可逆摘要进入裁决账本。</summary>
public sealed record ReviewToolOutcomeRequest(
    [property: Required] Dictionary<string, JsonElement> Arguments,
    bool Confirmed,
    [property: Required, StringLength(500, MinimumLength = 1)] string Reason);

/// <summary>提交给受限 Agent 的自然语言目标。</summary>
public sealed record RunAgentRequest(
    [property: Required, StringLength(4_000, MinimumLength = 1)] string Input);

/// <summary>请求取消等待审批或正在恢复的 Agent 运行；理由只以摘要形式持久化。</summary>
public sealed record CancelAgentRunRequest(
    [property: Required, StringLength(500, MinimumLength = 1)] string Reason);
