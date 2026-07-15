using AiMentor.Domain;

namespace AiMentor.Application;

/// <summary>强制执行“先提案、后批准”以及租户隔离、内容审核和保留期约束。</summary>
public sealed class MemoryWorkflowService(
    IMemoryStore store,
    IMemoryContentSafetyService contentSafety,
    ITraceSink traceSink,
    TimeProvider timeProvider,
    MemoryWorkflowOptions options) : IMemoryWorkflowService
{
    public async Task<MemoryProposal> ProposeAsync(ProposeMemoryCommand command, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        var key = RequiredText(command.Key, 128, "MEMORY_KEY_INVALID", "记忆键不能为空且不能超过 128 个字符。");
        var value = RequiredText(command.Value, 1_000, "MEMORY_VALUE_INVALID", "记忆值不能为空且不能超过 1000 个字符。");
        var sessionId = ValidateSession(command.Scope, command.SessionId);
        var safety = contentSafety.Review(key, value);
        if (safety.Action != SafetyAction.Allow)
        {
            await AuditAsync("memory.propose", "refused", access, command.Scope, safety.Code, null, cancellationToken);
            throw new MemoryWorkflowException(safety.Code, safety.Message, MemoryWorkflowErrorKind.Unsafe);
        }

        var now = timeProvider.GetUtcNow();
        var memoryExpiresAt = ResolveExpiration(command.Scope, command.ExpiresAt, now);
        var proposal = new MemoryProposal(Guid.NewGuid().ToString("N"), access.TenantId, access.SubjectId,
            command.Scope, sessionId, key, value, now, now.Add(options.ApprovalWindow), memoryExpiresAt,
            MemoryProposalStatus.PendingApproval);
        try
        {
            await store.SaveProposalAsync(proposal, cancellationToken);
        }
        catch (MemoryStoreCapacityException)
        {
            await AuditAsync("memory.propose", "capacity", access, command.Scope,
                "MEMORY_PROPOSAL_CAPACITY_EXCEEDED", null, cancellationToken);
            throw new MemoryWorkflowException("MEMORY_PROPOSAL_CAPACITY_EXCEEDED",
                "当前租户待批准记忆提案已达到容量上限，请等待过期或完成现有提案。",
                MemoryWorkflowErrorKind.Capacity);
        }
        await AuditAsync("memory.propose", "pending_approval", access, command.Scope, "MEMORY_APPROVAL_REQUIRED",
            proposal.Id, cancellationToken);
        return proposal;
    }

    public async Task<MemoryRecord> ApproveAsync(string proposalId, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        var id = RequiredId(proposalId, "MEMORY_PROPOSAL_ID_INVALID");
        var result = await store.ApproveAsync(id, access, timeProvider.GetUtcNow(), cancellationToken);
        if (result.Status == MemoryStoreStatus.Success)
        {
            await AuditAsync("memory.approve", "approved", access, result.Value!.Scope, "MEMORY_APPROVED", id, cancellationToken);
            return result.Value;
        }

        var (code, message, kind) = result.Status switch
        {
            MemoryStoreStatus.Expired => ("MEMORY_PROPOSAL_EXPIRED", "记忆提案已过批准期限，请重新提交。", MemoryWorkflowErrorKind.Conflict),
            MemoryStoreStatus.Conflict => ("MEMORY_PROPOSAL_ALREADY_APPROVED", "记忆提案已经批准，不能重复执行。", MemoryWorkflowErrorKind.Conflict),
            MemoryStoreStatus.AlreadyExists => ("MEMORY_KEY_ALREADY_EXISTS", "相同范围和键已有有效记忆，请使用更正接口。", MemoryWorkflowErrorKind.Conflict),
            _ => ("MEMORY_PROPOSAL_NOT_FOUND", "没有找到当前用户可批准的记忆提案。", MemoryWorkflowErrorKind.NotFound)
        };
        await AuditAsync("memory.approve", "rejected", access, null, code, id, cancellationToken);
        throw new MemoryWorkflowException(code, message, kind);
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListAsync(AccessContext access, MemoryScope? scope = null, string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        var result = await store.ListActiveAsync(access, scope, normalizedSessionId, timeProvider.GetUtcNow(), cancellationToken);
        var details = new Dictionary<string, object?>
        {
            ["tenant"] = access.TenantId,
            ["subject"] = access.SubjectId,
            ["scope"] = scope?.ToString(),
            ["resultCount"] = result.Count,
            ["code"] = "MEMORY_LISTED"
        };
        await traceSink.WriteAsync($"memory-{Guid.NewGuid():N}",
            [new TraceStep("memory.list", "ok", timeProvider.GetUtcNow(), details)], cancellationToken);
        return result;
    }

    public async Task<MemoryRecord> CorrectAsync(string memoryId, CorrectMemoryCommand command, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        var id = RequiredId(memoryId, "MEMORY_ID_INVALID");
        if (command.ExpectedVersion <= 0)
            throw new MemoryWorkflowException("MEMORY_VERSION_INVALID", "ExpectedVersion 必须大于 0。", MemoryWorkflowErrorKind.Validation);
        var value = RequiredText(command.Value, 1_000, "MEMORY_VALUE_INVALID", "记忆值不能为空且不能超过 1000 个字符。");
        var safety = contentSafety.Review(string.Empty, value);
        if (safety.Action != SafetyAction.Allow)
        {
            await AuditAsync("memory.correct", "refused", access, null, safety.Code, id, cancellationToken);
            throw new MemoryWorkflowException(safety.Code, safety.Message, MemoryWorkflowErrorKind.Unsafe);
        }

        var now = timeProvider.GetUtcNow();
        if (command.ExpiresAt is { } expiresAt && expiresAt <= now)
            throw new MemoryWorkflowException("MEMORY_EXPIRATION_INVALID", "更正后的过期时间必须晚于当前时间。", MemoryWorkflowErrorKind.Validation);
        var result = await store.UpdateAsync(id, access, command.ExpectedVersion, value, command.ExpiresAt, now, cancellationToken);
        if (result.Status == MemoryStoreStatus.Success)
        {
            await AuditAsync("memory.correct", "updated", access, result.Value!.Scope, "MEMORY_UPDATED", id, cancellationToken);
            return result.Value;
        }

        var failure = CreateMutationFailure(result.Status);
        await AuditAsync("memory.correct", "rejected", access, null, failure.Code, id, cancellationToken);
        throw failure;
    }

    public async Task DeleteAsync(string memoryId, int expectedVersion, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        var id = RequiredId(memoryId, "MEMORY_ID_INVALID");
        if (expectedVersion <= 0)
            throw new MemoryWorkflowException("MEMORY_VERSION_INVALID", "ExpectedVersion 必须大于 0。", MemoryWorkflowErrorKind.Validation);
        var result = await store.DeleteAsync(id, access, expectedVersion, cancellationToken);
        if (result.Status == MemoryStoreStatus.Success)
        {
            await AuditAsync("memory.delete", "deleted", access, null, "MEMORY_DELETED", id, cancellationToken);
            return;
        }

        var failure = CreateMutationFailure(result.Status);
        await AuditAsync("memory.delete", "rejected", access, null, failure.Code, id, cancellationToken);
        throw failure;
    }

    private static MemoryWorkflowException CreateMutationFailure(MemoryStoreStatus status) => status switch
    {
        MemoryStoreStatus.Conflict => new MemoryWorkflowException("MEMORY_VERSION_CONFLICT", "记忆已被其他操作更新，请重新读取后再提交。", MemoryWorkflowErrorKind.Conflict),
        MemoryStoreStatus.RetentionExceeded => new MemoryWorkflowException("MEMORY_RETENTION_EXTENSION_DENIED", "更正操作只能缩短保留期，延长保留期必须重新提案并批准。", MemoryWorkflowErrorKind.Validation),
        _ => new MemoryWorkflowException("MEMORY_NOT_FOUND", "没有找到当前用户可操作的记忆。", MemoryWorkflowErrorKind.NotFound)
    };

    private DateTimeOffset ResolveExpiration(MemoryScope scope, DateTimeOffset? requested, DateTimeOffset now)
    {
        var (defaultLifetime, maximumLifetime) = scope switch
        {
            MemoryScope.Session => (options.DefaultSessionLifetime, options.MaximumSessionLifetime),
            MemoryScope.UserPreference => (options.DefaultPreferenceLifetime, options.MaximumPreferenceLifetime),
            MemoryScope.LongTermFact => (options.DefaultFactLifetime, options.MaximumFactLifetime),
            _ => throw new MemoryWorkflowException("MEMORY_SCOPE_INVALID", "不支持的记忆范围。", MemoryWorkflowErrorKind.Validation)
        };
        var expiresAt = requested ?? now.Add(defaultLifetime);
        if (expiresAt <= now || expiresAt > now.Add(maximumLifetime))
            throw new MemoryWorkflowException("MEMORY_EXPIRATION_INVALID", "记忆过期时间超出该范围允许的保留期限。", MemoryWorkflowErrorKind.Validation);
        return expiresAt;
    }

    private static string? ValidateSession(MemoryScope scope, string? sessionId)
    {
        if (scope == MemoryScope.Session)
            return RequiredText(sessionId, 128, "MEMORY_SESSION_ID_INVALID", "会话记忆必须提供不超过 128 个字符的 SessionId。");
        if (!string.IsNullOrWhiteSpace(sessionId))
            throw new MemoryWorkflowException("MEMORY_SESSION_SCOPE_MISMATCH", "只有会话记忆可以指定 SessionId。", MemoryWorkflowErrorKind.Validation);
        return null;
    }

    private static void ValidateAccess(AccessContext access)
    {
        if (string.IsNullOrWhiteSpace(access.TenantId) || string.IsNullOrWhiteSpace(access.SubjectId))
            throw new MemoryWorkflowException("MEMORY_IDENTITY_INVALID", "缺少有效租户或用户身份。", MemoryWorkflowErrorKind.Validation);
    }

    private static string RequiredId(string value, string code) =>
        RequiredText(value, 128, code, "资源标识不能为空且不能超过 128 个字符。");

    private static string RequiredText(string? value, int maximumLength, string code, string message)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength)
            throw new MemoryWorkflowException(code, message, MemoryWorkflowErrorKind.Validation);
        return normalized;
    }

    private Task AuditAsync(string operation, string outcome, AccessContext access, MemoryScope? scope, string code,
        string? resourceId, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, object?>
        {
            ["tenant"] = access.TenantId,
            ["subject"] = access.SubjectId,
            ["scope"] = scope?.ToString(),
            ["code"] = code,
            ["resourceId"] = resourceId
        };
        return traceSink.WriteAsync($"memory-{Guid.NewGuid():N}",
            [new TraceStep(operation, outcome, timeProvider.GetUtcNow(), details)], cancellationToken);
    }
}
