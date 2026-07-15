using System.Security.Cryptography;
using System.Text;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>
/// 运营动作的唯一授权边界。前端只能提出意图；第二人原子占位成功且目标 ETag 未变化后才调用原工作流。
/// </summary>
public sealed class OperationsActionService(
    IOperationsActionStore store,
    IOperationsTaskService tasks,
    IToolApprovalService approvals,
    IToolCompensationService compensations,
    ToolApprovalOptions approvalOptions,
    ToolCompensationOptions compensationOptions,
    OperationsActionOptions options,
    TimeProvider timeProvider) : IOperationsActionService
{
    public async Task<OperationsActionSummary> RequestAsync(string targetType, string targetId, string action,
        string targetETag, string reason, string idempotencyKey, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(); ValidateAccess(access);
        var type = Required(targetType, 32, "OPERATIONS_TARGET_TYPE_INVALID", "运营任务类型无效。").ToLowerInvariant();
        var id = Required(targetId, 128, "OPERATIONS_TARGET_ID_INVALID", "运营任务标识无效。");
        var normalizedAction = Required(action, 32, "OPERATIONS_ACTION_INVALID", "运营动作无效。").ToLowerInvariant();
        var etag = Required(targetETag, 80, "OPERATIONS_ETAG_REQUIRED", "必须提交当前任务的 If-Match 标记。");
        var normalizedReason = Required(reason, 500, "OPERATIONS_REASON_INVALID", "动作说明不能为空且不能超过 500 个字符。");
        var key = Required(idempotencyKey, 128, "OPERATIONS_IDEMPOTENCY_KEY_REQUIRED",
            "必须提交 8 到 128 个字符的 Idempotency-Key。");
        if (key.Length < 8)
            throw Failure("OPERATIONS_IDEMPOTENCY_KEY_REQUIRED", "必须提交 8 到 128 个字符的 Idempotency-Key。",
                OperationsActionErrorKind.Validation);
        EnsureActionRole(type, normalizedAction, access);
        var reasonHash = Hash(normalizedReason);
        var idempotencyHash = Hash($"{access.TenantId}\u001f{key}");
        var fingerprint = Hash($"{access.TenantId}\u001f{access.SubjectId}\u001f{type}\u001f{id}\u001f" +
            $"{normalizedAction}\u001f{etag}\u001f{reasonHash}");
        var replay = await store.GetByIdempotencyAsync(access.TenantId, idempotencyHash, cancellationToken);
        if (replay is not null)
        {
            if (replay.RequestFingerprint != fingerprint)
                throw Failure("OPERATIONS_IDEMPOTENCY_KEY_REUSED", "Idempotency-Key 已绑定到不同运营动作。",
                    OperationsActionErrorKind.Conflict);
            return Summary(replay, true);
        }

        var target = await FindTaskAsync(type, id, access, cancellationToken);
        if (!FixedEquals(target.ETag, etag))
            throw Failure("OPERATIONS_TARGET_VERSION_CONFLICT", "任务已经变化，请刷新后重新提出动作。",
                OperationsActionErrorKind.Conflict);
        if (!target.AllowedActions.Contains(normalizedAction, StringComparer.Ordinal))
            throw Failure("OPERATIONS_ACTION_NOT_ALLOWED", "当前任务状态或身份不允许该动作。",
                OperationsActionErrorKind.Forbidden);

        var now = timeProvider.GetUtcNow();
        var record = new OperationsActionRecord(Guid.NewGuid().ToString("N"), access.TenantId, type, id,
            normalizedAction, etag, fingerprint, idempotencyHash, access.SubjectId, reasonHash,
            OperationsActionStatus.AwaitingReview, 1, now, now + options.ReviewLifetime);
        var created = await store.CreateAsync(record, options.MaximumEntries, cancellationToken);
        return created.Status switch
        {
            OperationsActionCreateStatus.Created => Summary(created.Record!, false),
            OperationsActionCreateStatus.Replay => Summary(created.Record!, true),
            OperationsActionCreateStatus.IdempotencyConflict => throw Failure(
                "OPERATIONS_IDEMPOTENCY_KEY_REUSED", "Idempotency-Key 已绑定到不同运营动作。",
                OperationsActionErrorKind.Conflict),
            OperationsActionCreateStatus.TargetBusy => throw Failure(
                "OPERATIONS_TARGET_ACTION_PENDING", "该任务已有待复核、执行中或结果不确定的运营动作。",
                OperationsActionErrorKind.Conflict),
            _ => throw Failure("OPERATIONS_ACTION_CAPACITY_EXCEEDED", "运营动作队列已达到容量上限。",
                OperationsActionErrorKind.Capacity)
        };
    }

    public async Task<OperationsActionSummary> ReviewAsync(string requestId, long expectedVersion,
        string expectedETag, bool approved, string reason, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(); ValidateAccess(access);
        var id = Required(requestId, 64, "OPERATIONS_ACTION_ID_INVALID", "运营动作请求标识无效。");
        if (expectedVersion <= 0)
            throw Failure("OPERATIONS_ACTION_VERSION_INVALID", "ExpectedVersion 必须大于 0。",
                OperationsActionErrorKind.Validation);
        var normalizedReason = Required(reason, 500, "OPERATIONS_REVIEW_REASON_INVALID",
            "复核说明不能为空且不能超过 500 个字符。");
        var current = await store.GetAsync(access.TenantId, id, cancellationToken)
            ?? throw Failure("OPERATIONS_ACTION_NOT_FOUND", "没有找到当前租户可复核的运营动作。",
                OperationsActionErrorKind.NotFound);
        EnsureActionRole(current.TargetType, current.Action, access);
        if (current.Status == OperationsActionStatus.Expired)
            throw Failure("OPERATIONS_ACTION_EXPIRED", "运营动作复核期限已过。",
                OperationsActionErrorKind.Conflict);
        if (current.Status != OperationsActionStatus.AwaitingReview
            && current.ReviewerSubjectId == access.SubjectId)
        {
            var sameDecision = approved == (current.Status != OperationsActionStatus.Rejected);
            var sameReason = current.ReviewReasonHash == Hash(normalizedReason);
            if (!sameDecision || !sameReason)
                throw Failure("OPERATIONS_REVIEW_REPLAY_MISMATCH", "复核重放与首次复核内容不一致。",
                    OperationsActionErrorKind.Conflict);
            if (current.Status == OperationsActionStatus.Executing)
                throw Failure("OPERATIONS_ACTION_IN_PROGRESS", "运营动作正在由已获胜的复核请求执行。",
                    OperationsActionErrorKind.Conflict);
            return Summary(current, true);
        }
        if (!FixedEquals(ETag(current), Required(expectedETag, 80, "OPERATIONS_ETAG_REQUIRED",
                "必须提交当前动作请求的 If-Match 标记。")))
            throw Failure("OPERATIONS_ACTION_VERSION_CONFLICT", "运营动作已经被其他复核人处理。",
                OperationsActionErrorKind.Conflict);
        var acquired = await store.TryAcquireReviewAsync(access.TenantId, id, expectedVersion, access.SubjectId,
            approved, Hash(normalizedReason), timeProvider.GetUtcNow(), cancellationToken);
        switch (acquired.Status)
        {
            case OperationsActionAcquireStatus.Rejected:
                return Summary(acquired.Record!, false);
            case OperationsActionAcquireStatus.Replay when acquired.Record!.Status != OperationsActionStatus.Executing:
                return Summary(acquired.Record, true);
            case OperationsActionAcquireStatus.ReviewerMustDiffer:
                throw Failure("OPERATIONS_REVIEWER_MUST_DIFFER", "动作提出人与复核人必须分离。",
                    OperationsActionErrorKind.Forbidden);
            case OperationsActionAcquireStatus.NotFound:
                throw Failure("OPERATIONS_ACTION_NOT_FOUND", "没有找到当前租户可复核的运营动作。",
                    OperationsActionErrorKind.NotFound);
            case OperationsActionAcquireStatus.Expired:
                throw Failure("OPERATIONS_ACTION_EXPIRED", "运营动作复核期限已过。",
                    OperationsActionErrorKind.Conflict);
            case OperationsActionAcquireStatus.VersionConflict:
                throw Failure("OPERATIONS_ACTION_VERSION_CONFLICT", "运营动作已经被其他复核人处理。",
                    OperationsActionErrorKind.Conflict);
            case OperationsActionAcquireStatus.Replay:
                throw Failure("OPERATIONS_ACTION_IN_PROGRESS", "运营动作正在由已获胜的复核请求执行。",
                    OperationsActionErrorKind.Conflict);
            case OperationsActionAcquireStatus.Acquired:
                break;
            default:
                throw Failure("OPERATIONS_ACTION_STATE_INVALID", "运营动作状态无效。",
                    OperationsActionErrorKind.Conflict);
        }

        var executing = acquired.Record!;
        var finalized = false;
        try
        {
            // SLA 升级只提交与首次授权快照绑定的运营标记，不读取或修改目标载荷，也不冒充目标所有者。
            // 其他动作会改变底层工作流，仍必须以实际复核人身份重新校验目标版本与访问权。
            if (executing.Action != "escalate")
            {
                var target = await FindTaskAsync(executing.TargetType, executing.TargetId, access, cancellationToken);
                if (!FixedEquals(target.ETag, executing.TargetETag))
                {
                    await FinishAsync(executing, OperationsActionStatus.Failed,
                        "OPERATIONS_TARGET_VERSION_CONFLICT");
                    finalized = true;
                    throw Failure("OPERATIONS_TARGET_VERSION_CONFLICT", "复核前任务已经变化，动作未执行。",
                        OperationsActionErrorKind.Conflict);
                }
            }
            var code = await DispatchAsync(executing, access, cancellationToken);
            var completed = await FinishAsync(executing, OperationsActionStatus.Completed, code);
            finalized = true;
            return Summary(completed, false);
        }
        catch (OperationsActionException exception)
        {
            if (!finalized)
                await FinishAsync(executing, OperationsActionStatus.Failed, exception.Code);
            throw;
        }
        catch (ToolApprovalException exception)
        {
            await FinishAsync(executing, OperationsActionStatus.Failed, exception.Code);
            throw Failure(exception.Code, exception.Message, Map(exception.Kind));
        }
        catch (ToolCompensationException exception)
        {
            await FinishAsync(executing, OperationsActionStatus.Failed, exception.Code);
            throw Failure(exception.Code, exception.Message, Map(exception.Kind));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FinishAsync(executing, OperationsActionStatus.OutcomeUnknown,
                "OPERATIONS_ACTION_OUTCOME_UNKNOWN");
            throw Failure("OPERATIONS_ACTION_OUTCOME_UNKNOWN",
                "底层动作结果无法确认，审计记录已冻结，禁止自动重放。", OperationsActionErrorKind.Unavailable);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(executing, OperationsActionStatus.OutcomeUnknown,
                "OPERATIONS_ACTION_CANCELLED_OUTCOME_UNKNOWN");
            throw;
        }
    }

    public async Task<IReadOnlyList<OperationsActionSummary>> ListAsync(AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        if (!access.Groups.Overlaps(options.ReviewerGroups) && !access.Groups.Overlaps(options.EscalatorGroups))
            throw Failure("OPERATIONS_REVIEWER_ROLE_REQUIRED", "当前身份无权访问运营动作审计。",
                OperationsActionErrorKind.Forbidden);
        return (await store.ListAsync(access.TenantId, 500, cancellationToken))
            .Where(item => CanAccessAudit(item, access))
            .Select(item => Summary(item, false)).ToArray();
    }

    private async Task<string> DispatchAsync(OperationsActionRecord record, AccessContext access,
        CancellationToken cancellationToken)
    {
        if (record.Action == "escalate") return "OPERATIONS_SLA_ESCALATED";
        // 原始复核说明只进入不可逆摘要；目标工作流使用审计记录 ID 建立可追踪关联。
        var auditReference = $"运营动作 {record.Id} 已通过独立复核。";
        if (record.TargetType == "approval")
        {
            await approvals.DecideAsync(record.TargetId, record.Action == "approve", auditReference, access,
                cancellationToken);
            return record.Action == "approve" ? "OPERATIONS_APPROVAL_APPROVED" : "OPERATIONS_APPROVAL_REJECTED";
        }
        if (record.TargetType == "compensation")
        {
            var target = (await compensations.ListAsync(access, cancellationToken: cancellationToken))
                .SingleOrDefault(item => item.Id == record.TargetId)
                ?? throw Failure("OPERATIONS_TARGET_NOT_FOUND", "没有找到当前租户可操作的补偿任务。",
                    OperationsActionErrorKind.NotFound);
            if (string.IsNullOrWhiteSpace(target.ApprovalId))
                throw Failure("OPERATIONS_COMPENSATION_APPROVAL_MISSING", "补偿任务缺少有效审批标识。",
                    OperationsActionErrorKind.Conflict);
            await compensations.DecideAsync(record.TargetId, target.ApprovalId, record.Action == "approve",
                auditReference, access, cancellationToken);
            return record.Action == "approve" ? "OPERATIONS_COMPENSATION_APPROVED"
                : "OPERATIONS_COMPENSATION_REJECTED";
        }
        throw Failure("OPERATIONS_ACTION_NOT_IMPLEMENTED", "该任务类型尚未注册安全的运营动作适配器。",
            OperationsActionErrorKind.Validation);
    }

    private async Task<OperationsTaskSummary> FindTaskAsync(string type, string id, AccessContext access,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await tasks.ListAsync(access, type, null, cursor, 100, cancellationToken);
            var found = page.Items.SingleOrDefault(item => item.Type == type && item.Id == id);
            if (found is not null) return found;
            cursor = page.NextCursor;
        } while (cursor is not null);
        throw Failure("OPERATIONS_TARGET_NOT_FOUND", "没有找到当前身份可访问的运营任务。",
            OperationsActionErrorKind.NotFound);
    }

    private void EnsureActionRole(string type, string action, AccessContext access)
    {
        if (action == "escalate")
        {
            if (!access.Groups.Overlaps(options.EscalatorGroups))
                throw Failure("OPERATIONS_ESCALATOR_ROLE_REQUIRED", "当前用户不属于 SLA 升级组。",
                    OperationsActionErrorKind.Forbidden);
            return;
        }
        if (action is not ("approve" or "reject") || type is not ("approval" or "compensation"))
            throw Failure("OPERATIONS_ACTION_NOT_IMPLEMENTED", "该任务类型尚未注册安全的运营动作适配器。",
                OperationsActionErrorKind.Validation);
        if (!access.Groups.Overlaps(options.ReviewerGroups))
            throw Failure("OPERATIONS_REVIEWER_ROLE_REQUIRED", "当前用户不属于运营复核组。",
                OperationsActionErrorKind.Forbidden);
        var workflowGroups = type == "approval" ? approvalOptions.ApproverGroups : compensationOptions.ApproverGroups;
        if (!access.Groups.Overlaps(workflowGroups))
            throw Failure("OPERATIONS_TARGET_APPROVER_ROLE_REQUIRED", "当前用户不属于目标工作流审批组。",
                OperationsActionErrorKind.Forbidden);
    }

    private bool CanAccessAudit(OperationsActionRecord record, AccessContext access)
    {
        if (record.Action == "escalate") return access.Groups.Overlaps(options.EscalatorGroups);
        if (!access.Groups.Overlaps(options.ReviewerGroups)) return false;
        return record.TargetType switch
        {
            "approval" => access.Groups.Overlaps(approvalOptions.ApproverGroups),
            "compensation" => access.Groups.Overlaps(compensationOptions.ApproverGroups),
            _ => false
        };
    }

    private async Task<OperationsActionRecord> FinishAsync(OperationsActionRecord executing,
        OperationsActionStatus status, string code) => await store.CompleteAsync(executing.TenantId, executing.Id,
        executing.Version, status, code, timeProvider.GetUtcNow(), CancellationToken.None);

    private static OperationsActionSummary Summary(OperationsActionRecord record, bool replay) => new(
        record.Id, record.TargetType, record.TargetId, record.Action, record.Status, record.Version,
        record.CreatedAt, record.ExpiresAt, record.CompletedAt, record.OutcomeCode, replay, ETag(record));

    public static string ETag(OperationsActionRecord record) =>
        $"\"{Hash($"{record.Id}\u001f{record.Version}\u001f{record.Status}")}\"";

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left); var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Required(string? value, int max, string code, string message)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > max)
            throw Failure(code, message, OperationsActionErrorKind.Validation);
        return normalized;
    }
    private void ValidateOptions()
    {
        if (options.ReviewLifetime <= TimeSpan.Zero || options.MaximumEntries <= 0
            || options.ReviewerGroups.Count == 0 || options.EscalatorGroups.Count == 0)
            throw new InvalidOperationException("运营动作配置无效。");
    }
    private static void ValidateAccess(AccessContext access)
    {
        if (string.IsNullOrWhiteSpace(access.TenantId) || string.IsNullOrWhiteSpace(access.SubjectId))
            throw Failure("OPERATIONS_IDENTITY_INVALID", "缺少有效租户或用户身份。",
                OperationsActionErrorKind.Validation);
    }
    private static OperationsActionErrorKind Map(ToolApprovalErrorKind kind) => kind switch
    {
        ToolApprovalErrorKind.Validation => OperationsActionErrorKind.Validation,
        ToolApprovalErrorKind.Forbidden => OperationsActionErrorKind.Forbidden,
        ToolApprovalErrorKind.NotFound => OperationsActionErrorKind.NotFound,
        ToolApprovalErrorKind.Conflict => OperationsActionErrorKind.Conflict,
        _ => OperationsActionErrorKind.Capacity
    };
    private static OperationsActionErrorKind Map(ToolCompensationErrorKind kind) => kind switch
    {
        ToolCompensationErrorKind.Validation => OperationsActionErrorKind.Validation,
        ToolCompensationErrorKind.Forbidden => OperationsActionErrorKind.Forbidden,
        ToolCompensationErrorKind.NotFound => OperationsActionErrorKind.NotFound,
        ToolCompensationErrorKind.Conflict => OperationsActionErrorKind.Conflict,
        ToolCompensationErrorKind.Capacity => OperationsActionErrorKind.Capacity,
        _ => OperationsActionErrorKind.Unavailable
    };
    private static OperationsActionException Failure(string code, string message, OperationsActionErrorKind kind) =>
        new(code, message, kind);
}
