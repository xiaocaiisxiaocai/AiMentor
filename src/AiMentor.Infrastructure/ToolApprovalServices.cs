using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>
/// 在进程内维护最小审批状态，并强制职责分离、精确调用绑定、过期和一次性消费。
/// 审批失败默认关闭工具执行，服务重启丢失审批不会造成越权。
/// </summary>
public sealed class InMemoryToolApprovalService(
    IToolRegistry registry,
    IToolInvocationSafetyService safety,
    ITraceSink traceSink,
    ToolApprovalOptions options,
    TimeProvider timeProvider) : IToolApprovalService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ToolApprovalRequest> _requests = new(StringComparer.Ordinal);

    public async Task<ToolApprovalRequest> RequestAsync(string toolName, JsonElement arguments, string justification,
        AccessContext requester, CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        ValidateAccess(requester);
        var normalizedToolName = RequiredText(toolName, 128, "TOOL_APPROVAL_TOOL_INVALID", "工具名称无效。");
        var normalizedJustification = RequiredText(justification, 500, "TOOL_APPROVAL_JUSTIFICATION_INVALID",
            "审批理由不能为空且不能超过 500 个字符。");
        if (!registry.TryGet(normalizedToolName, out var tool) || tool is null)
            throw Failure("TOOL_NOT_REGISTERED", "请求的工具未在服务器注册表中。", ToolApprovalErrorKind.NotFound);
        if (tool.Descriptor.Risk == ToolOperationRisk.ReadOnly)
            throw Failure("TOOL_APPROVAL_NOT_REQUIRED", "只读工具不接受修改性操作审批。", ToolApprovalErrorKind.Validation);

        var normalizedArguments = NormalizeArguments(arguments);
        if (Encoding.UTF8.GetByteCount(normalizedArguments.GetRawText()) > options.MaximumArgumentBytes)
            throw Failure("TOOL_ARGUMENTS_TOO_LARGE", "工具参数超过审批服务允许的大小。",
                ToolApprovalErrorKind.Validation);
        var validation = tool.ValidateArguments(normalizedArguments);
        if (validation.Action != SafetyAction.Allow)
            throw Failure(validation.Code, validation.Message, ToolApprovalErrorKind.Validation);

        var invocationArguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(normalizedArguments.GetRawText())
            ?? new Dictionary<string, object?>();
        var safetyDecision = safety.Review(new ToolInvocationRequest(tool.Descriptor.Name, tool.Descriptor.Risk,
            invocationArguments), requester);
        if (safetyDecision.Action == SafetyAction.Refuse)
            throw Failure(safetyDecision.Code, safetyDecision.Message, ToolApprovalErrorKind.Forbidden);
        if (safetyDecision.Action != SafetyAction.RequireApproval)
            throw Failure("TOOL_APPROVAL_POLICY_MISMATCH", "当前安全策略未要求该工具进入审批流程。",
                ToolApprovalErrorKind.Conflict);

        var now = timeProvider.GetUtcNow();
        ToolApprovalRequest request;
        lock (_gate)
        {
            PruneAndExpire(now);
            if (_requests.Count >= options.MaximumEntries)
                throw Failure("TOOL_APPROVAL_CAPACITY_EXCEEDED", "审批记录已达到容量上限，请稍后重试。",
                    ToolApprovalErrorKind.Capacity);
            request = new ToolApprovalRequest(Guid.NewGuid().ToString("N"), requester.TenantId,
                requester.SubjectId, tool.Descriptor.Name, tool.Descriptor.Risk,
                JsonArgumentFingerprint.Create(tool.Descriptor.Name, normalizedArguments, requester),
                normalizedArguments.EnumerateObject().Select(property => property.Name)
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray(), normalizedJustification, now,
                now.Add(options.ApprovalLifetime), ToolApprovalStatus.Pending);
            _requests.Add(request.Id, request);
        }

        await AuditAsync("tool.approval.requested", "pending", requester, request, "TOOL_APPROVAL_PENDING",
            cancellationToken);
        return request;
    }

    public async Task<ToolApprovalRequest> DecideAsync(string approvalId, bool approved, string reason,
        AccessContext approver, CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        ValidateAccess(approver);
        var id = RequiredText(approvalId, 128, "TOOL_APPROVAL_ID_INVALID", "审批标识无效。");
        var normalizedReason = RequiredText(reason, 500, "TOOL_APPROVAL_REASON_INVALID",
            "审批决定理由不能为空且不能超过 500 个字符。");
        if (!approver.Groups.Overlaps(options.ApproverGroups))
            throw Failure("TOOL_APPROVER_ROLE_REQUIRED", "当前用户不属于工具审批人组。", ToolApprovalErrorKind.Forbidden);

        var now = timeProvider.GetUtcNow();
        ToolApprovalRequest decided;
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var current)
                || !string.Equals(current.TenantId, approver.TenantId, StringComparison.Ordinal))
                throw Failure("TOOL_APPROVAL_NOT_FOUND", "没有找到当前租户可裁决的审批。", ToolApprovalErrorKind.NotFound);
            if (string.Equals(current.RequesterSubjectId, approver.SubjectId, StringComparison.Ordinal))
                throw Failure("TOOL_APPROVAL_SELF_DECISION_DENIED", "申请人与审批人必须分离。", ToolApprovalErrorKind.Forbidden);
            current = ExpireIfNeeded(current, now);
            if (current.Status == ToolApprovalStatus.Expired)
            {
                _requests[id] = current;
                throw Failure("TOOL_APPROVAL_EXPIRED", "审批已过有效期，请重新申请。", ToolApprovalErrorKind.Conflict);
            }
            if (current.Status != ToolApprovalStatus.Pending)
                throw Failure("TOOL_APPROVAL_ALREADY_DECIDED", "审批已经裁决，不能重复操作。", ToolApprovalErrorKind.Conflict);
            decided = current with
            {
                Status = approved ? ToolApprovalStatus.Approved : ToolApprovalStatus.Rejected,
                ApproverSubjectId = approver.SubjectId,
                DecidedAt = now,
                DecisionReason = normalizedReason
            };
            _requests[id] = decided;
        }

        await AuditAsync("tool.approval.decided", approved ? "approved" : "rejected", approver, decided,
            approved ? "TOOL_APPROVAL_APPROVED" : "TOOL_APPROVAL_REJECTED", cancellationToken);
        return decided;
    }

    public async Task<IReadOnlyList<ToolApprovalRequest>> ListAsync(AccessContext access,
        ToolApprovalStatus? status = null, CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        ValidateAccess(access);
        ToolApprovalRequest[] result;
        lock (_gate)
        {
            PruneAndExpire(timeProvider.GetUtcNow());
            var canApprove = access.Groups.Overlaps(options.ApproverGroups);
            result = _requests.Values.Where(request =>
                    string.Equals(request.TenantId, access.TenantId, StringComparison.Ordinal)
                    && (canApprove || string.Equals(request.RequesterSubjectId, access.SubjectId, StringComparison.Ordinal))
                    && (status is null || request.Status == status))
                .OrderByDescending(request => request.CreatedAt).ToArray();
        }
        await traceSink.WriteAsync($"tool-approval-{Guid.NewGuid():N}",
            [new TraceStep("tool.approval.list", "ok", timeProvider.GetUtcNow(), new Dictionary<string, object?>
            {
                ["tenant"] = access.TenantId,
                ["subject"] = access.SubjectId,
                ["status"] = status?.ToString(),
                ["resultCount"] = result.Length
            })], cancellationToken);
        return result;
    }

    public async Task<ToolApprovalConsumption> ConsumeAsync(string approvalId, string toolName, JsonElement arguments,
        AccessContext requester, CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        ValidateAccess(requester);
        var id = approvalId?.Trim() ?? string.Empty;
        var now = timeProvider.GetUtcNow();
        ToolApprovalRequest? request = null;
        SafetyDecision decision;
        lock (_gate)
        {
            if (id.Length is 0 or > 128 || !_requests.TryGetValue(id, out var current)
                || !string.Equals(current.TenantId, requester.TenantId, StringComparison.Ordinal)
                || !string.Equals(current.RequesterSubjectId, requester.SubjectId, StringComparison.Ordinal))
            {
                decision = Refuse("TOOL_APPROVAL_NOT_FOUND", "没有找到可供当前申请人使用的审批凭据。");
            }
            else
            {
                current = ExpireIfNeeded(current, now);
                _requests[id] = current;
                request = current;
                if (current.Status == ToolApprovalStatus.Expired)
                    decision = Refuse("TOOL_APPROVAL_EXPIRED", "审批凭据已过期。");
                else if (current.Status == ToolApprovalStatus.Consumed)
                    decision = Refuse("TOOL_APPROVAL_ALREADY_CONSUMED", "审批凭据已经消费，不能重放。");
                else if (current.Status != ToolApprovalStatus.Approved)
                    decision = Refuse("TOOL_APPROVAL_NOT_APPROVED", "审批凭据尚未批准或已被拒绝。");
                else
                {
                    var fingerprint = JsonArgumentFingerprint.Create(toolName, NormalizeArguments(arguments), requester);
                    if (!string.Equals(current.ToolName, toolName, StringComparison.OrdinalIgnoreCase)
                        || !CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(current.ArgumentsHash), Convert.FromHexString(fingerprint)))
                    {
                        decision = Refuse("TOOL_APPROVAL_SCOPE_MISMATCH", "审批凭据与当前工具或参数不匹配。");
                    }
                    else
                    {
                        request = current with { Status = ToolApprovalStatus.Consumed, ConsumedAt = now };
                        _requests[id] = request;
                        decision = new SafetyDecision(SafetyAction.Allow, "TOOL_APPROVAL_CONSUMED",
                            "审批凭据已完成精确匹配并被一次性消费。");
                    }
                }
            }
        }

        if (request is not null)
            await AuditAsync("tool.approval.consumed", decision.Action == SafetyAction.Allow ? "consumed" : "refused",
                requester, request, decision.Code, cancellationToken);
        return new ToolApprovalConsumption(decision.Action == SafetyAction.Allow, decision);
    }

    private void ValidateOptions()
    {
        if (options.ApprovalLifetime <= TimeSpan.Zero || options.MaximumEntries <= 0
            || options.MaximumArgumentBytes <= 0 || options.ApproverGroups.Count == 0)
            throw new InvalidOperationException("工具审批配置无效。");
    }

    private void PruneAndExpire(DateTimeOffset now)
    {
        foreach (var pair in _requests.ToArray())
        {
            var updated = ExpireIfNeeded(pair.Value, now);
            _requests[pair.Key] = updated;
            var terminalAt = updated.ConsumedAt ?? updated.DecidedAt ?? updated.ExpiresAt;
            if (updated.Status is ToolApprovalStatus.Consumed or ToolApprovalStatus.Rejected or ToolApprovalStatus.Expired
                && terminalAt.Add(options.ApprovalLifetime) <= now)
                _requests.Remove(pair.Key);
        }
    }

    private static ToolApprovalRequest ExpireIfNeeded(ToolApprovalRequest request, DateTimeOffset now) =>
        request.Status is ToolApprovalStatus.Pending or ToolApprovalStatus.Approved && request.ExpiresAt <= now
            ? request with { Status = ToolApprovalStatus.Expired }
            : request;

    private Task AuditAsync(string operation, string outcome, AccessContext actor, ToolApprovalRequest request,
        string code, CancellationToken cancellationToken) => traceSink.WriteAsync($"tool-approval-{request.Id}",
        [new TraceStep(operation, outcome, timeProvider.GetUtcNow(), new Dictionary<string, object?>
        {
            ["approvalId"] = request.Id,
            ["tenant"] = actor.TenantId,
            ["actor"] = actor.SubjectId,
            ["requester"] = request.RequesterSubjectId,
            ["tool"] = request.ToolName,
            ["status"] = request.Status.ToString(),
            ["code"] = code
        })], cancellationToken);

    private static JsonElement NormalizeArguments(JsonElement arguments)
    {
        var normalized = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>()) : arguments.Clone();
        if (normalized.ValueKind != JsonValueKind.Object)
            throw Failure("TOOL_ARGUMENTS_MUST_BE_OBJECT", "工具参数必须是 JSON 对象。", ToolApprovalErrorKind.Validation);
        return normalized;
    }

    private static void ValidateAccess(AccessContext access)
    {
        if (string.IsNullOrWhiteSpace(access.TenantId) || string.IsNullOrWhiteSpace(access.SubjectId))
            throw Failure("TOOL_APPROVAL_IDENTITY_INVALID", "缺少有效租户或用户身份。", ToolApprovalErrorKind.Validation);
    }

    private static string RequiredText(string? value, int maximumLength, string code, string message)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength)
            throw Failure(code, message, ToolApprovalErrorKind.Validation);
        return normalized;
    }

    private static ToolApprovalException Failure(string code, string message, ToolApprovalErrorKind kind) =>
        new(code, message, kind);

    private static SafetyDecision Refuse(string code, string message) => new(SafetyAction.Refuse, code, message);
}

/// <summary>生成与 JSON 对象属性顺序无关、与调用身份和工具绑定的参数摘要。</summary>
internal static class JsonArgumentFingerprint
{
    public static string Create(string toolName, JsonElement arguments, AccessContext access)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, arguments);
        var prefix = Encoding.UTF8.GetBytes($"{access.TenantId}\u001f{access.SubjectId}\u001f{toolName.ToLowerInvariant()}\u001f");
        var payload = new byte[prefix.Length + buffer.WrittenCount];
        prefix.CopyTo(payload, 0);
        buffer.WrittenSpan.CopyTo(payload.AsSpan(prefix.Length));
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
        }
    }
}
