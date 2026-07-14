using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>
/// 提供单实例开发模式的加密补偿闭环。所有状态转换均在同一锁内完成；该实现绝不用于多实例 SQL Server 模式。
/// </summary>
public sealed class InMemoryToolCompensationService(
    IToolRegistry registry,
    IWorkflowStateCipher cipher,
    ITraceSink traceSink,
    ToolCompensationOptions options,
    TimeProvider timeProvider,
    IToolExecutionBarrier? executionBarrier = null,
    IEnumerable<IToolCompensationOutcomeProbe>? outcomeProbes = null) :
    IToolCompensationService, IToolCompensationReconciliationService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _forwardExecutions = new(StringComparer.Ordinal);
    private readonly IToolExecutionBarrier _executionBarrier =
        executionBarrier ?? NoOpToolExecutionBarrier.Instance;
    private readonly Dictionary<string, IToolCompensationOutcomeProbe> _outcomeProbes =
        (outcomeProbes ?? []).ToDictionary(probe => probe.CompensationToolName, StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable => true;

    public async Task<ToolCompensationPreparation> PrepareForwardAsync(string executionKey,
        ICompensableServerTool tool, ToolExecutionContext context, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        ValidateAccess(context.Access);
        if (executionKey.Length != 64 || !executionKey.All(Uri.IsHexDigit))
            throw Failure("TOOL_COMPENSATION_FORWARD_KEY_INVALID", "正向执行标识无效。",
                ToolCompensationErrorKind.Validation);

        // 快照必须在正向副作用前采集，且进入状态表前立即使用带上下文的工作流密钥加密。
        var snapshot = await tool.CaptureCompensationStateAsync(context, arguments, cancellationToken);
        var snapshotJson = snapshot.GetRawText();
        if (Encoding.UTF8.GetByteCount(snapshotJson) > options.MaximumSnapshotBytes)
            throw Failure("TOOL_COMPENSATION_SNAPSHOT_TOO_LARGE", "补偿快照超过服务器允许的大小。",
                ToolCompensationErrorKind.Validation);

        var now = timeProvider.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        var preparationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var protectedSnapshot = cipher.Protect(snapshotJson, SnapshotContext(id, tool.Descriptor.Name));
        lock (_gate)
        {
            Prune(now);
            if (_entries.Count >= options.MaximumEntries)
                throw Failure("TOOL_COMPENSATION_CAPACITY_EXCEEDED", "补偿记录已达到容量上限。",
                    ToolCompensationErrorKind.Capacity);
            if (_forwardExecutions.ContainsKey(executionKey))
                throw Failure("TOOL_COMPENSATION_FORWARD_ALREADY_PREPARED", "正向执行已建立补偿占位。",
                    ToolCompensationErrorKind.Conflict);
            var entry = new Entry
            {
                Id = id,
                ForwardExecutionKey = executionKey,
                TenantId = context.Access.TenantId,
                RequesterSubjectId = context.Access.SubjectId,
                ForwardToolName = tool.Descriptor.Name,
                CompensationToolName = tool.CompensationToolName,
                Status = ToolCompensationStatus.Prepared,
                PreparationToken = preparationToken,
                KeyVersion = protectedSnapshot.KeyVersion,
                SnapshotCipher = protectedSnapshot.Ciphertext,
                CreatedAt = now,
                ExpiresAt = now.Add(options.CompensationLifetime)
            };
            _entries.Add(id, entry);
            _forwardExecutions.Add(executionKey, id);
        }
        await AuditAsync("tool.compensation.prepared", "prepared", context.Access, id, tool.Descriptor.Name,
            "TOOL_COMPENSATION_PREPARED", cancellationToken);
        return new ToolCompensationPreparation(id, preparationToken);
    }

    public async Task ActivateAsync(ToolCompensationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_gate)
        {
            entry = RequirePreparation(preparation);
            entry.Status = ToolCompensationStatus.Available;
            entry.PreparationToken = null;
        }
        await AuditAsync("tool.compensation.available", "available", Access(entry), entry.Id,
            entry.ForwardToolName, "TOOL_COMPENSATION_AVAILABLE", cancellationToken);
    }

    public Task DiscardAsync(ToolCompensationPreparation preparation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = RequirePreparation(preparation);
            _entries.Remove(entry.Id);
            _forwardExecutions.Remove(entry.ForwardExecutionKey);
        }
        return Task.CompletedTask;
    }

    public async Task MarkForwardOutcomeUnknownAsync(ToolCompensationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(preparation.Id, out entry!))
                throw Failure("TOOL_COMPENSATION_PREPARATION_LOST", "补偿准备记录不存在。",
                    ToolCompensationErrorKind.Conflict);
            if (entry.Status == ToolCompensationStatus.ForwardOutcomeUnknown) return;
            if (entry.Status == ToolCompensationStatus.Prepared
                && !FixedEquals(entry.PreparationToken, preparation.PreparationToken))
                throw Failure("TOOL_COMPENSATION_PREPARATION_LOST", "补偿准备令牌无效或已失效。",
                    ToolCompensationErrorKind.Conflict);
            if (entry.Status is not (ToolCompensationStatus.Prepared or ToolCompensationStatus.Available))
                throw Failure("TOOL_COMPENSATION_PREPARATION_LOST", "补偿准备状态已经变化。",
                    ToolCompensationErrorKind.Conflict);
            entry.Status = ToolCompensationStatus.ForwardOutcomeUnknown;
            entry.PreparationToken = null;
        }
        await AuditAsync("tool.compensation.forward", "outcome_unknown", Access(entry), entry.Id,
            entry.ForwardToolName, "TOOL_COMPENSATION_FORWARD_OUTCOME_UNKNOWN", cancellationToken);
    }

    public Task<IReadOnlyList<ToolCompensationSummary>> ListAsync(AccessContext access,
        ToolCompensationStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        if (status.HasValue && !Enum.IsDefined(status.Value))
            throw Failure("TOOL_COMPENSATION_STATUS_INVALID", "补偿状态过滤值无效。",
                ToolCompensationErrorKind.Validation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Prune(timeProvider.GetUtcNow());
            var canApprove = access.Groups.Overlaps(options.ApproverGroups);
            return Task.FromResult<IReadOnlyList<ToolCompensationSummary>>(_entries.Values
                .Where(entry => entry.Status != ToolCompensationStatus.Prepared
                    && (!status.HasValue || entry.Status == status.Value)
                    && string.Equals(entry.TenantId, access.TenantId, StringComparison.Ordinal)
                    && (canApprove || string.Equals(entry.RequesterSubjectId, access.SubjectId,
                        StringComparison.Ordinal)))
                .OrderByDescending(entry => entry.CreatedAt).Select(ToSummary).ToArray());
        }
    }

    public async Task<ToolCompensationSummary> RequestApprovalAsync(string compensationId, string justification,
        AccessContext requester, CancellationToken cancellationToken = default)
    {
        ValidateAccess(requester);
        var id = RequiredText(compensationId, 128, "TOOL_COMPENSATION_ID_INVALID", "补偿标识无效。");
        var normalizedJustification = RequiredText(justification, 500, "TOOL_COMPENSATION_JUSTIFICATION_INVALID",
            "补偿审批理由不能为空且不能超过 500 个字符。");
        Entry entry;
        lock (_gate)
        {
            Prune(timeProvider.GetUtcNow());
            entry = RequireOwned(id, requester);
            if (entry.Status is not (ToolCompensationStatus.Available or ToolCompensationStatus.Rejected))
                throw Failure("TOOL_COMPENSATION_NOT_APPROVABLE", "当前补偿状态不能申请审批。",
                    ToolCompensationErrorKind.Conflict);
            entry.Status = ToolCompensationStatus.AwaitingApproval;
            entry.ApprovalId = Guid.NewGuid().ToString("N");
            entry.Justification = normalizedJustification;
            entry.ApprovalExpiresAt = timeProvider.GetUtcNow().Add(options.ApprovalLifetime);
            entry.ApproverSubjectId = null;
        }
        await AuditAsync("tool.compensation.approval", "pending", requester, entry.Id,
            entry.CompensationToolName, "TOOL_COMPENSATION_APPROVAL_PENDING", cancellationToken);
        return ToSummary(entry);
    }

    public async Task<ToolCompensationSummary> DecideAsync(string compensationId, string approvalId, bool approved,
        string reason, AccessContext approver, CancellationToken cancellationToken = default)
    {
        ValidateAccess(approver);
        var id = RequiredText(compensationId, 128, "TOOL_COMPENSATION_ID_INVALID", "补偿标识无效。");
        var normalizedApprovalId = RequiredText(approvalId, 128, "TOOL_COMPENSATION_APPROVAL_ID_INVALID",
            "补偿审批标识无效。");
        var normalizedReason = RequiredText(reason, 500, "TOOL_COMPENSATION_REASON_INVALID",
            "审批理由不能为空且不能超过 500 个字符。");
        if (!approver.Groups.Overlaps(options.ApproverGroups))
            throw Failure("TOOL_COMPENSATION_APPROVER_ROLE_REQUIRED", "当前用户不属于工具审批人组。",
                ToolCompensationErrorKind.Forbidden);

        Entry entry;
        lock (_gate)
        {
            Prune(timeProvider.GetUtcNow());
            if (!_entries.TryGetValue(id, out entry!)
                || !string.Equals(entry.TenantId, approver.TenantId, StringComparison.Ordinal))
                throw Failure("TOOL_COMPENSATION_NOT_FOUND", "没有找到当前租户可裁决的补偿。",
                    ToolCompensationErrorKind.NotFound);
            if (string.Equals(entry.RequesterSubjectId, approver.SubjectId, StringComparison.Ordinal))
                throw Failure("TOOL_COMPENSATION_SELF_DECISION_DENIED", "补偿申请人与审批人必须分离。",
                    ToolCompensationErrorKind.Forbidden);
            if (entry.Status != ToolCompensationStatus.AwaitingApproval
                || !FixedEquals(entry.ApprovalId, normalizedApprovalId))
                throw Failure("TOOL_COMPENSATION_APPROVAL_MISMATCH", "补偿审批不存在、已过期或状态不匹配。",
                    ToolCompensationErrorKind.Conflict);
            entry.Status = approved ? ToolCompensationStatus.Approved : ToolCompensationStatus.Rejected;
            entry.ApproverSubjectId = approver.SubjectId;
            // 裁决理由只保留不可逆摘要，避免审批表和普通诊断面泄漏业务说明原文。
            entry.DecisionReasonHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedReason)));
        }
        await AuditAsync("tool.compensation.approval", approved ? "approved" : "rejected", approver, entry.Id,
            entry.CompensationToolName, approved ? "TOOL_COMPENSATION_APPROVED" : "TOOL_COMPENSATION_REJECTED",
            cancellationToken);
        return ToSummary(entry);
    }

    public async Task<ToolCompensationExecutionResult> ExecuteAsync(string compensationId, string approvalId,
        string idempotencyKey, AccessContext requester, CancellationToken cancellationToken = default)
    {
        ValidateAccess(requester);
        var id = RequiredText(compensationId, 128, "TOOL_COMPENSATION_ID_INVALID", "补偿标识无效。");
        var normalizedApprovalId = RequiredText(approvalId, 128, "TOOL_COMPENSATION_APPROVAL_ID_INVALID",
            "补偿审批标识无效。");
        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey)
            ?? throw Failure("TOOL_COMPENSATION_IDEMPOTENCY_KEY_INVALID", "补偿执行必须提供合法的独立幂等键。",
                ToolCompensationErrorKind.Validation);
        var executionKey = ExecutionKey(id, requester, normalizedIdempotencyKey);
        Entry entry;
        ICompensableServerTool tool;
        lock (_gate)
        {
            Prune(timeProvider.GetUtcNow());
            entry = RequireOwned(id, requester);
            if (entry.Status == ToolCompensationStatus.Completed)
            {
                if (!FixedEquals(entry.CompensationExecutionKey, executionKey))
                    throw Failure("TOOL_COMPENSATION_ALREADY_COMPLETED", "该正向执行已经完成补偿。",
                        ToolCompensationErrorKind.Conflict);
                return new ToolCompensationExecutionResult(id, entry.Status, "TOOL_COMPENSATION_COMPLETED", true,
                    entry.CompletedAt);
            }
            if (entry.Status == ToolCompensationStatus.Executing)
                throw Failure("TOOL_COMPENSATION_EXECUTION_IN_PROGRESS", "相同补偿正在执行。",
                    ToolCompensationErrorKind.Conflict);
            if (entry.Status is ToolCompensationStatus.OutcomeUnknown or ToolCompensationStatus.ForwardOutcomeUnknown)
                throw Failure("TOOL_COMPENSATION_OUTCOME_UNKNOWN", "正向或补偿结果不确定，禁止自动重试。",
                    ToolCompensationErrorKind.Conflict);
            if (entry.Status != ToolCompensationStatus.Approved
                || !FixedEquals(entry.ApprovalId, normalizedApprovalId))
                throw Failure("TOOL_COMPENSATION_APPROVAL_REQUIRED", "补偿执行需要匹配的独立批准凭据。",
                    ToolCompensationErrorKind.Forbidden);
            if (!registry.TryGet(entry.ForwardToolName, out var registered)
                || registered is not ICompensableServerTool resolvedTool
                || !string.Equals(resolvedTool.CompensationToolName, entry.CompensationToolName,
                    StringComparison.Ordinal))
                throw Failure("TOOL_COMPENSATION_CONTRACT_CHANGED", "补偿工具契约已变化，未进入反向执行。",
                    ToolCompensationErrorKind.Conflict);
            if (options.ExecutionLeaseDuration <= resolvedTool.Descriptor.Timeout)
                throw Failure("TOOL_COMPENSATION_CONFIGURATION_INVALID",
                    "补偿执行租约必须长于反向工具超时。", ToolCompensationErrorKind.Unavailable);
            tool = resolvedTool;
            entry.Status = ToolCompensationStatus.Executing;
            entry.CompensationExecutionKey = executionKey;
            entry.ExecutionLeaseExpiresAt = timeProvider.GetUtcNow().Add(options.ExecutionLeaseDuration);
        }

        try
        {
            // 状态已持久化为 Executing，但补偿快照尚未解密、反向工具尚未产生副作用；仅测试屏障会阻塞。
            await _executionBarrier.WaitAfterExecutingAsync(executionKey, entry.CompensationToolName,
                cancellationToken);
            var snapshotJson = cipher.Unprotect(entry.KeyVersion, entry.SnapshotCipher,
                SnapshotContext(entry.Id, entry.ForwardToolName));
            using var snapshotDocument = JsonDocument.Parse(snapshotJson);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(tool.Descriptor.Timeout);
            await tool.CompensateAsync(new ToolExecutionContext(requester, $"comp-{entry.Id}"),
                snapshotDocument.RootElement.Clone(), timeout.Token);

            var completedAt = timeProvider.GetUtcNow();
            lock (_gate)
            {
                if (!_entries.TryGetValue(entry.Id, out var current)
                    || current.Status != ToolCompensationStatus.Executing
                    || !FixedEquals(current.CompensationExecutionKey, executionKey))
                    throw Failure("TOOL_COMPENSATION_LEASE_LOST", "补偿执行状态已变化，结果保持不确定。",
                        ToolCompensationErrorKind.Conflict);
                current.Status = ToolCompensationStatus.Completed;
                current.CompletedAt = completedAt;
                current.ExecutionLeaseExpiresAt = null;
            }
            await AuditAsync("tool.compensation.executed", "completed", requester, entry.Id,
                entry.CompensationToolName, "TOOL_COMPENSATION_COMPLETED", CancellationToken.None);
            return new ToolCompensationExecutionResult(entry.Id, ToolCompensationStatus.Completed,
                "TOOL_COMPENSATION_COMPLETED", false, completedAt);
        }
        catch (ToolCompensationException)
        {
            MarkOutcomeUnknown(entry.Id, executionKey);
            throw;
        }
        catch (Exception exception)
        {
            MarkOutcomeUnknown(entry.Id, executionKey);
            await AuditAsync("tool.compensation.executed", "outcome_unknown", requester, entry.Id,
                entry.CompensationToolName, $"TOOL_COMPENSATION_OUTCOME_UNKNOWN_{exception.GetType().Name}",
                CancellationToken.None);
            throw Failure("TOOL_COMPENSATION_OUTCOME_UNKNOWN",
                "补偿调用可能已经产生副作用但结果未可靠写回，禁止自动重试。",
                ToolCompensationErrorKind.Conflict);
        }
    }

    /// <inheritdoc />
    public async Task<ToolCompensationOutcomeProbeResult> ProbeOutcomeAsync(string compensationId,
        AccessContext reconciler, CancellationToken cancellationToken = default)
    {
        ValidateReconciler(reconciler);
        var id = RequiredText(compensationId, 128, "TOOL_COMPENSATION_ID_INVALID", "补偿标识无效。");
        Entry entry;
        IToolCompensationOutcomeProbe probe;
        string snapshotJson;
        lock (_gate)
        {
            Prune(timeProvider.GetUtcNow());
            entry = RequireOutcomeUnknown(id, reconciler);
            if (!_outcomeProbes.TryGetValue(entry.CompensationToolName, out probe!))
                throw Failure("TOOL_COMPENSATION_OUTCOME_PROBE_NOT_SUPPORTED",
                    "该反向工具尚未提供目标状态探测器。", ToolCompensationErrorKind.Validation);
            // 快照只在服务端解密并直接传给专属探测器，绝不经由 API 或审计详情返回。
            snapshotJson = cipher.Unprotect(entry.KeyVersion, entry.SnapshotCipher,
                SnapshotContext(entry.Id, entry.ForwardToolName));
        }

        using var snapshot = JsonDocument.Parse(snapshotJson);
        var probeResult = await probe.ProbeAsync(entry.Id, Access(entry), snapshot.RootElement.Clone(), cancellationToken);
        var result = ToolCompensationProbeResultValidator.Validate(probeResult, entry.Id,
            entry.CompensationToolName, timeProvider.GetUtcNow());
        await AuditAsync("tool.compensation.reconciliation.probe", result.State.ToString(), reconciler, entry.Id,
            entry.CompensationToolName, result.Code, cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public async Task<ToolCompensationReconciliationReviewResult> ReviewOutcomeAsync(string compensationId,
        bool confirmed, string reason, AccessContext reconciler, CancellationToken cancellationToken = default)
    {
        ValidateReconciler(reconciler);
        var normalizedReason = RequiredText(reason, 500, "TOOL_COMPENSATION_RECONCILIATION_REASON_INVALID",
            "补偿对账理由必须为 1 到 500 个字符。");
        var evidence = await ProbeOutcomeAsync(compensationId, reconciler, cancellationToken);
        if (confirmed && evidence.State == ToolOutcomeProbeState.Indeterminate)
            throw Failure("TOOL_COMPENSATION_RECONCILIATION_EVIDENCE_INDETERMINATE",
                "不确定证据不能用于结案或授权重试。", ToolCompensationErrorKind.Validation);

        ToolCompensationReconciliationReviewResult result;
        lock (_gate)
        {
            var entry = RequireOutcomeUnknown(evidence.CompensationId, reconciler);
            var expiresAt = evidence.ObservedAt.Add(options.ReconciliationEvidenceLifetime);
            var reasonHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedReason)));
            if (!confirmed)
            {
                entry.ClearReview();
                result = new(entry.Id, ToolReconciliationReviewStatus.Rejected, evidence.State, expiresAt);
            }
            else if (entry.ReviewExpiresAt is null)
            {
                result = FirstReview(entry, reconciler, evidence, expiresAt, reasonHash);
            }
            else if (entry.ReviewExpiresAt <= timeProvider.GetUtcNow())
            {
                entry.ClearReview();
                result = new(entry.Id, ToolReconciliationReviewStatus.EvidenceExpired,
                    evidence.State, expiresAt);
            }
            else if (entry.ReviewEvidenceState != evidence.State
                     || !string.Equals(entry.ReviewEvidenceCode, evidence.Code, StringComparison.Ordinal))
            {
                entry.ClearReview();
                result = new(entry.Id, ToolReconciliationReviewStatus.EvidenceChanged,
                    evidence.State, expiresAt);
            }
            else if (string.Equals(entry.FirstReviewerSubjectId, reconciler.SubjectId, StringComparison.Ordinal))
            {
                result = new(entry.Id, ToolReconciliationReviewStatus.ReviewerMustDiffer,
                    evidence.State, entry.ReviewExpiresAt!.Value);
            }
            else
            {
                // 两名不同人员确认同一份仍有效证据后才改变冻结状态。
                entry.SecondReviewerSubjectId = reconciler.SubjectId;
                entry.SecondReviewReasonHash = reasonHash;
                entry.Status = evidence.State == ToolOutcomeProbeState.Applied
                    ? ToolCompensationStatus.Completed : ToolCompensationStatus.Available;
                if (entry.Status == ToolCompensationStatus.Completed)
                    entry.CompletedAt = timeProvider.GetUtcNow();
                else
                {
                    // 未恢复只证明旧反向调用没有生效；重新执行仍须新的独立业务审批和幂等键。
                    entry.ApprovalId = null;
                    entry.ApprovalExpiresAt = null;
                    entry.ApproverSubjectId = null;
                    entry.DecisionReasonHash = null;
                    entry.CompensationExecutionKey = null;
                }
                result = new(entry.Id, evidence.State == ToolOutcomeProbeState.Applied
                        ? ToolReconciliationReviewStatus.ResolvedApplied
                        : ToolReconciliationReviewStatus.RetryAuthorized,
                    evidence.State, entry.ReviewExpiresAt!.Value);
            }
        }
        await AuditAsync("tool.compensation.reconciliation.review", result.Status.ToString(), reconciler,
            evidence.CompensationId, evidence.CompensationToolName,
            $"TOOL_COMPENSATION_RECONCILIATION_{result.Status.ToString().ToUpperInvariant()}", cancellationToken);
        return result;
    }

    private static ToolCompensationReconciliationReviewResult FirstReview(Entry entry, AccessContext reconciler,
        ToolCompensationOutcomeProbeResult evidence, DateTimeOffset expiresAt, string reasonHash)
    {
        entry.FirstReviewerSubjectId = reconciler.SubjectId;
        entry.FirstReviewReasonHash = reasonHash;
        entry.ReviewEvidenceState = evidence.State;
        entry.ReviewEvidenceCode = evidence.Code;
        entry.ReviewObservedAt = evidence.ObservedAt;
        entry.ReviewExpiresAt = expiresAt;
        return new(entry.Id, ToolReconciliationReviewStatus.AwaitingSecondReviewer,
            evidence.State, expiresAt);
    }

    private Entry RequireOutcomeUnknown(string id, AccessContext reconciler)
    {
        if (!_entries.TryGetValue(id, out var entry)
            || !string.Equals(entry.TenantId, reconciler.TenantId, StringComparison.Ordinal)
            || entry.Status != ToolCompensationStatus.OutcomeUnknown)
            throw Failure("TOOL_COMPENSATION_OUTCOME_UNKNOWN_NOT_FOUND",
                "当前租户不存在可对账的结果不确定补偿。", ToolCompensationErrorKind.NotFound);
        return entry;
    }

    private Entry RequirePreparation(ToolCompensationPreparation preparation)
    {
        if (!_entries.TryGetValue(preparation.Id, out var entry)
            || entry.Status != ToolCompensationStatus.Prepared
            || !FixedEquals(entry.PreparationToken, preparation.PreparationToken))
            throw Failure("TOOL_COMPENSATION_PREPARATION_LOST", "补偿准备令牌无效或已失效。",
                ToolCompensationErrorKind.Conflict);
        return entry;
    }

    private Entry RequireOwned(string id, AccessContext access)
    {
        if (!_entries.TryGetValue(id, out var entry)
            || !string.Equals(entry.TenantId, access.TenantId, StringComparison.Ordinal)
            || !string.Equals(entry.RequesterSubjectId, access.SubjectId, StringComparison.Ordinal))
            throw Failure("TOOL_COMPENSATION_NOT_FOUND", "没有找到当前用户可访问的补偿。",
                ToolCompensationErrorKind.NotFound);
        return entry;
    }

    private void MarkOutcomeUnknown(string id, string executionKey)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry)
                && entry.Status == ToolCompensationStatus.Executing
                && FixedEquals(entry.CompensationExecutionKey, executionKey))
            {
                entry.Status = ToolCompensationStatus.OutcomeUnknown;
                entry.ExecutionLeaseExpiresAt = null;
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Status == ToolCompensationStatus.Executing && entry.ExecutionLeaseExpiresAt <= now)
            {
                entry.Status = ToolCompensationStatus.OutcomeUnknown;
                entry.ExecutionLeaseExpiresAt = null;
            }
            if (entry.Status is ToolCompensationStatus.AwaitingApproval or ToolCompensationStatus.Approved
                && entry.ApprovalExpiresAt <= now)
            {
                entry.Status = ToolCompensationStatus.Available;
                entry.ApprovalId = null;
                entry.ApprovalExpiresAt = null;
            }
            if (entry.ExpiresAt <= now && entry.Status is not (ToolCompensationStatus.Completed
                    or ToolCompensationStatus.OutcomeUnknown or ToolCompensationStatus.ForwardOutcomeUnknown))
                entry.Status = ToolCompensationStatus.Expired;
        }
        foreach (var entry in _entries.Values.Where(item => item.ExpiresAt.Add(options.CompensationLifetime) <= now)
                     .ToArray())
        {
            _entries.Remove(entry.Id);
            _forwardExecutions.Remove(entry.ForwardExecutionKey);
        }
    }

    private void ValidateOptions()
    {
        if (options.CompensationLifetime <= TimeSpan.Zero || options.ApprovalLifetime <= TimeSpan.Zero
            || options.ExecutionLeaseDuration <= TimeSpan.Zero || options.MaximumEntries <= 0
            || options.CompensationLifetime <= options.ApprovalLifetime
            || options.CompensationLifetime <= options.ExecutionLeaseDuration
            || options.MaximumSnapshotBytes <= 0 || options.ApproverGroups.Count == 0
            || options.ReconcilerGroups.Count == 0 || options.ReconciliationEvidenceLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("工具补偿配置无效。");
    }

    private static void ValidateAccess(AccessContext access)
    {
        if (string.IsNullOrWhiteSpace(access.TenantId) || string.IsNullOrWhiteSpace(access.SubjectId))
            throw Failure("TOOL_COMPENSATION_ACCESS_INVALID", "补偿访问身份无效。",
                ToolCompensationErrorKind.Validation);
    }

    private void ValidateReconciler(AccessContext access)
    {
        ValidateAccess(access);
        if (!access.Groups.Overlaps(options.ReconcilerGroups))
            throw Failure("TOOL_COMPENSATION_RECONCILER_ROLE_REQUIRED",
                "只有工具对账人员可以核验结果不确定补偿。", ToolCompensationErrorKind.Forbidden);
    }

    private static string RequiredText(string? value, int maximumLength, string code, string message)
    {
        var normalized = value?.Trim();
        return normalized is { Length: > 0 } && normalized.Length <= maximumLength
            ? normalized : throw Failure(code, message, ToolCompensationErrorKind.Validation);
    }

    private static string? NormalizeIdempotencyKey(string? value)
    {
        var normalized = value?.Trim();
        return normalized is { Length: > 0 and <= 128 }
            && normalized.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
                ? normalized : null;
    }

    private static string ExecutionKey(string id, AccessContext access, string idempotencyKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{id}\u001f{access.TenantId}\u001f{access.SubjectId}\u001f{idempotencyKey}")));

    private static string SnapshotContext(string id, string toolName) => $"tool-compensation:{id}:{toolName}";

    private static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }

    private static AccessContext Access(Entry entry) =>
        AccessContext.Create(entry.TenantId, entry.RequesterSubjectId, []);

    private static ToolCompensationSummary ToSummary(Entry entry) => new(entry.Id, entry.ForwardToolName,
        entry.CompensationToolName, entry.Status, entry.CreatedAt, entry.ExpiresAt, entry.ApprovalId,
        entry.Justification, entry.CompletedAt);

    private async Task AuditAsync(string name, string outcome, AccessContext access, string id, string toolName,
        string code, CancellationToken cancellationToken) =>
        await traceSink.WriteAsync($"tool-compensation-{id}",
            [new TraceStep(name, outcome, timeProvider.GetUtcNow(), new Dictionary<string, object?>
            {
                ["tenant"] = access.TenantId,
                ["subject"] = access.SubjectId,
                ["compensationId"] = id,
                ["tool"] = toolName,
                ["code"] = code
            })], cancellationToken);

    private static ToolCompensationException Failure(string code, string message, ToolCompensationErrorKind kind) =>
        new(code, message, kind);

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string ForwardExecutionKey { get; init; }
        public required string TenantId { get; init; }
        public required string RequesterSubjectId { get; init; }
        public required string ForwardToolName { get; init; }
        public required string CompensationToolName { get; init; }
        public required string KeyVersion { get; init; }
        public required string SnapshotCipher { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public ToolCompensationStatus Status { get; set; }
        public string? PreparationToken { get; set; }
        public string? ApprovalId { get; set; }
        public string? Justification { get; set; }
        public DateTimeOffset? ApprovalExpiresAt { get; set; }
        public string? ApproverSubjectId { get; set; }
        public string? DecisionReasonHash { get; set; }
        public string? CompensationExecutionKey { get; set; }
        public DateTimeOffset? ExecutionLeaseExpiresAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? FirstReviewerSubjectId { get; set; }
        public string? FirstReviewReasonHash { get; set; }
        public string? SecondReviewerSubjectId { get; set; }
        public string? SecondReviewReasonHash { get; set; }
        public ToolOutcomeProbeState? ReviewEvidenceState { get; set; }
        public string? ReviewEvidenceCode { get; set; }
        public DateTimeOffset? ReviewObservedAt { get; set; }
        public DateTimeOffset? ReviewExpiresAt { get; set; }

        /// <summary>证据被拒绝、变化或过期时清除第一人复核，防止旧证据与新观察拼接。</summary>
        public void ClearReview()
        {
            FirstReviewerSubjectId = null;
            FirstReviewReasonHash = null;
            SecondReviewerSubjectId = null;
            SecondReviewReasonHash = null;
            ReviewEvidenceState = null;
            ReviewEvidenceCode = null;
            ReviewObservedAt = null;
            ReviewExpiresAt = null;
        }
    }
}

/// <summary>在尚未配置耐久补偿账本的多实例模式中失败关闭所有补偿准备和执行请求。</summary>
public sealed class UnavailableToolCompensationService : IToolCompensationService
{
    public bool IsAvailable => false;

    public Task<ToolCompensationPreparation> PrepareForwardAsync(string executionKey, ICompensableServerTool tool,
        ToolExecutionContext context, JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.FromException<ToolCompensationPreparation>(Unavailable());
    public Task ActivateAsync(ToolCompensationPreparation preparation, CancellationToken cancellationToken = default) =>
        Task.FromException(Unavailable());
    public Task DiscardAsync(ToolCompensationPreparation preparation, CancellationToken cancellationToken = default) =>
        Task.FromException(Unavailable());
    public Task MarkForwardOutcomeUnknownAsync(ToolCompensationPreparation preparation,
        CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
    public Task<IReadOnlyList<ToolCompensationSummary>> ListAsync(AccessContext access,
        ToolCompensationStatus? status = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<ToolCompensationSummary>>(Unavailable());
    public Task<ToolCompensationSummary> RequestApprovalAsync(string compensationId, string justification,
        AccessContext requester, CancellationToken cancellationToken = default) =>
        Task.FromException<ToolCompensationSummary>(Unavailable());
    public Task<ToolCompensationSummary> DecideAsync(string compensationId, string approvalId, bool approved,
        string reason, AccessContext approver, CancellationToken cancellationToken = default) =>
        Task.FromException<ToolCompensationSummary>(Unavailable());
    public Task<ToolCompensationExecutionResult> ExecuteAsync(string compensationId, string approvalId,
        string idempotencyKey, AccessContext requester, CancellationToken cancellationToken = default) =>
        Task.FromException<ToolCompensationExecutionResult>(Unavailable());

    private static ToolCompensationException Unavailable() => new("TOOL_COMPENSATION_DURABLE_STORE_UNAVAILABLE",
        "当前部署尚未配置耐久补偿账本，补偿性修改和反向执行均保持关闭。", ToolCompensationErrorKind.Unavailable);
}
