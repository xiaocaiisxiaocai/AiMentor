using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.Data.SqlClient;

namespace AiMentor.Infrastructure;

/// <summary>
/// 使用 SQL Server 串行化事务持久化加密补偿快照、独立审批和反向执行租约。
/// 已进入反向工具的过期租约只会冻结为 OutcomeUnknown，禁止其他实例自动接管重放。
/// </summary>
public sealed class SqlServerToolCompensationService(
    IToolRegistry registry,
    IWorkflowStateCipher cipher,
    ITraceSink traceSink,
    ToolCompensationOptions compensationOptions,
    SqlServerWorkflowOptions sqlOptions,
    TimeProvider timeProvider,
    IToolExecutionBarrier? executionBarrier = null,
    IEnumerable<IToolCompensationOutcomeProbe>? outcomeProbes = null) :
    IToolCompensationService, IToolCompensationReconciliationService, IDisposable
{
    private const string TableName = "AiMentorToolCompensations";
    private const string ReconciliationTableName = "AiMentorToolCompensationReconciliations";
    private const string Columns = "Id,ForwardExecutionKey,TenantId,RequesterSubjectId,ForwardToolName," +
        "CompensationToolName,Status,PreparationTokenHash,KeyVersion,SnapshotCipher,CreatedAt,ExpiresAt," +
        "ApprovalId,Justification,ApprovalExpiresAt,ApproverSubjectId,DecisionReasonHash," +
        "CompensationExecutionKey,ExecutionLeaseToken,ExecutionLeaseExpiresAt,CompletedAt";
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly IToolExecutionBarrier _executionBarrier =
        executionBarrier ?? NoOpToolExecutionBarrier.Instance;
    private readonly Dictionary<string, IToolCompensationOutcomeProbe> _outcomeProbes =
        (outcomeProbes ?? []).ToDictionary(probe => probe.CompensationToolName, StringComparer.OrdinalIgnoreCase);
    private volatile bool _initialized;

    public bool IsAvailable => true;

    public async Task<ToolCompensationPreparation> PrepareForwardAsync(string executionKey,
        ICompensableServerTool tool, ToolExecutionContext context, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        ValidateAccess(context.Access);
        if (executionKey.Length != 64 || !executionKey.All(Uri.IsHexDigit))
            throw Failure("TOOL_COMPENSATION_FORWARD_KEY_INVALID", "正向执行标识无效。",
                ToolCompensationErrorKind.Validation);
        var snapshot = await tool.CaptureCompensationStateAsync(context, arguments, cancellationToken);
        var snapshotJson = snapshot.GetRawText();
        if (Encoding.UTF8.GetByteCount(snapshotJson) > compensationOptions.MaximumSnapshotBytes)
            throw Failure("TOOL_COMPENSATION_SNAPSHOT_TOO_LARGE", "补偿快照超过服务器允许的大小。",
                ToolCompensationErrorKind.Validation);

        await EnsureInitializedAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var protectedSnapshot = cipher.Protect(snapshotJson, SnapshotContext(id, tool.Descriptor.Name));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        await NormalizeAndPruneAsync(connection, transaction, now, cancellationToken);
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = $"SELECT COUNT_BIG(1) FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK);";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
                >= compensationOptions.MaximumEntries)
                throw Failure("TOOL_COMPENSATION_CAPACITY_EXCEEDED", "补偿记录已达到容量上限。",
                    ToolCompensationErrorKind.Capacity);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT dbo.{TableName}
                  (Id,ForwardExecutionKey,TenantId,RequesterSubjectId,ForwardToolName,CompensationToolName,
                   Status,PreparationTokenHash,KeyVersion,SnapshotCipher,CreatedAt,ExpiresAt,UpdatedAt)
                VALUES(@id,@forward,@tenant,@requester,@forwardTool,@reverseTool,@status,@tokenHash,
                       @version,@snapshot,@created,@expires,@created);
                """;
            AddString(insert, "@id", 64, id); AddAnsiString(insert, "@forward", 64, executionKey);
            AddString(insert, "@tenant", 128, context.Access.TenantId);
            AddString(insert, "@requester", 256, context.Access.SubjectId);
            AddString(insert, "@forwardTool", 128, tool.Descriptor.Name);
            AddString(insert, "@reverseTool", 128, tool.CompensationToolName);
            AddByte(insert, "@status", ToolCompensationStatus.Prepared);
            AddAnsiString(insert, "@tokenHash", 64, Hash(token));
            AddString(insert, "@version", 64, protectedSnapshot.KeyVersion);
            AddString(insert, "@snapshot", -1, protectedSnapshot.Ciphertext);
            AddDate(insert, "@created", now); AddDate(insert, "@expires", now.Add(compensationOptions.CompensationLifetime));
            try { await insert.ExecuteNonQueryAsync(cancellationToken); }
            catch (SqlException exception) when (exception.Number is 2601 or 2627)
            {
                throw Failure("TOOL_COMPENSATION_FORWARD_ALREADY_PREPARED", "正向执行已建立补偿占位。",
                    ToolCompensationErrorKind.Conflict);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        await AuditAsync("tool.compensation.prepared", "prepared", context.Access, id, tool.Descriptor.Name,
            "TOOL_COMPENSATION_PREPARED", cancellationToken);
        return new ToolCompensationPreparation(id, token);
    }

    public Task ActivateAsync(ToolCompensationPreparation preparation,
        CancellationToken cancellationToken = default) =>
        TransitionPreparationAsync(preparation, ToolCompensationStatus.Available, "available",
            "TOOL_COMPENSATION_AVAILABLE", cancellationToken);

    public async Task DiscardAsync(ToolCompensationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE dbo.{TableName} WHERE Id COLLATE Latin1_General_100_BIN2=@id " +
            "AND Status=@prepared AND PreparationTokenHash=@hash;";
        AddString(command, "@id", 64, preparation.Id); AddByte(command, "@prepared", ToolCompensationStatus.Prepared);
        AddAnsiString(command, "@hash", 64, Hash(preparation.PreparationToken));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw PreparationLost();
    }

    public async Task MarkForwardOutcomeUnknownAsync(ToolCompensationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=@unknown,UpdatedAt=@now
            WHERE Id COLLATE Latin1_General_100_BIN2=@id
              AND PreparationTokenHash=@hash AND Status IN (@prepared,@available);
            IF @@ROWCOUNT=0 AND NOT EXISTS(SELECT 1 FROM dbo.{TableName}
                WHERE Id COLLATE Latin1_General_100_BIN2=@id AND Status=@unknown)
                THROW 51001,'补偿准备状态已经变化。',1;
            """;
        AddByte(command, "@unknown", ToolCompensationStatus.ForwardOutcomeUnknown);
        AddDate(command, "@now", timeProvider.GetUtcNow()); AddString(command, "@id", 64, preparation.Id);
        AddAnsiString(command, "@hash", 64, Hash(preparation.PreparationToken));
        AddByte(command, "@prepared", ToolCompensationStatus.Prepared);
        AddByte(command, "@available", ToolCompensationStatus.Available);
        await command.ExecuteNonQueryAsync(cancellationToken);
        var audit = await ReadAuditIdentityAsync(preparation.Id, cancellationToken);
        await AuditAsync("tool.compensation.forward", "outcome_unknown",
            AccessContext.Create(audit.TenantId, audit.SubjectId, []), preparation.Id, audit.ToolName,
            "TOOL_COMPENSATION_FORWARD_OUTCOME_UNKNOWN", cancellationToken);
    }

    public async Task<IReadOnlyList<ToolCompensationSummary>> ListAsync(AccessContext access,
        ToolCompensationStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        if (status.HasValue && !Enum.IsDefined(status.Value))
            throw Failure("TOOL_COMPENSATION_STATUS_INVALID", "补偿状态过滤值无效。",
                ToolCompensationErrorKind.Validation);
        await EnsureInitializedAsync(cancellationToken);
        await NormalizeDueAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM dbo.{TableName}
            WHERE TenantId COLLATE Latin1_General_100_BIN2=@tenant
                AND Status<>@prepared AND (@status IS NULL OR Status=@status)
                AND (@canApprove=1 OR RequesterSubjectId COLLATE Latin1_General_100_BIN2=@subject)
            ORDER BY CreatedAt DESC;
            """;
        AddString(command, "@tenant", 128, access.TenantId); AddString(command, "@subject", 256, access.SubjectId);
        command.Parameters.Add("@canApprove", SqlDbType.Bit).Value = access.Groups.Overlaps(compensationOptions.ApproverGroups);
        AddByte(command, "@prepared", ToolCompensationStatus.Prepared);
        command.Parameters.Add("@status", SqlDbType.TinyInt).Value = status.HasValue
            ? (byte)status.Value
            : DBNull.Value;
        var result = new List<ToolCompensationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ToSummary(Read(reader)));
        return result;
    }

    public async Task<ToolCompensationSummary> RequestApprovalAsync(string compensationId, string justification,
        AccessContext requester, CancellationToken cancellationToken = default)
    {
        ValidateAccess(requester);
        var id = RequiredText(compensationId, 64, "TOOL_COMPENSATION_ID_INVALID", "补偿标识无效。");
        var normalizedJustification = RequiredText(justification, 500, "TOOL_COMPENSATION_JUSTIFICATION_INVALID",
            "补偿审批理由不能为空且不能超过 500 个字符。");
        await EnsureInitializedAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        await NormalizeAndPruneAsync(connection, transaction, now, cancellationToken);
        var row = await ReadForUpdateAsync(connection, transaction, id, cancellationToken);
        EnsureOwned(row, requester);
        if (row!.Status is not (ToolCompensationStatus.Available or ToolCompensationStatus.Rejected))
            throw Failure("TOOL_COMPENSATION_NOT_APPROVABLE", "当前补偿状态不能申请审批。",
                ToolCompensationErrorKind.Conflict);
        var approvalId = Guid.NewGuid().ToString("N");
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE dbo.{TableName} SET Status=@pending,ApprovalId=@approval,Justification=@justification,
                    ApprovalExpiresAt=@approvalExpires,ApproverSubjectId=NULL,DecisionReasonHash=NULL,UpdatedAt=@now
                WHERE Id COLLATE Latin1_General_100_BIN2=@id AND Status IN (@available,@rejected);
                """;
            AddByte(update, "@pending", ToolCompensationStatus.AwaitingApproval);
            AddString(update, "@approval", 64, approvalId); AddString(update, "@justification", 500, normalizedJustification);
            AddDate(update, "@approvalExpires", now.Add(compensationOptions.ApprovalLifetime)); AddDate(update, "@now", now);
            AddString(update, "@id", 64, id); AddByte(update, "@available", ToolCompensationStatus.Available);
            AddByte(update, "@rejected", ToolCompensationStatus.Rejected);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw Failure("TOOL_COMPENSATION_NOT_APPROVABLE", "当前补偿状态不能申请审批。",
                    ToolCompensationErrorKind.Conflict);
        }
        await transaction.CommitAsync(cancellationToken);
        var summary = ToSummary(row with
        {
            Status = ToolCompensationStatus.AwaitingApproval,
            ApprovalId = approvalId,
            Justification = normalizedJustification,
            ApprovalExpiresAt = now.Add(compensationOptions.ApprovalLifetime)
        });
        await AuditAsync("tool.compensation.approval", "pending", requester, id, row.CompensationToolName,
            "TOOL_COMPENSATION_APPROVAL_PENDING", cancellationToken);
        return summary;
    }

    public async Task<ToolCompensationSummary> DecideAsync(string compensationId, string approvalId, bool approved,
        string reason, AccessContext approver, CancellationToken cancellationToken = default)
    {
        ValidateAccess(approver);
        var id = RequiredText(compensationId, 64, "TOOL_COMPENSATION_ID_INVALID", "补偿标识无效。");
        var normalizedApproval = RequiredText(approvalId, 64, "TOOL_COMPENSATION_APPROVAL_ID_INVALID", "补偿审批标识无效。");
        var normalizedReason = RequiredText(reason, 500, "TOOL_COMPENSATION_REASON_INVALID", "审批理由不能为空且不能超过 500 个字符。");
        if (!approver.Groups.Overlaps(compensationOptions.ApproverGroups))
            throw Failure("TOOL_COMPENSATION_APPROVER_ROLE_REQUIRED", "当前用户不属于工具审批人组。",
                ToolCompensationErrorKind.Forbidden);
        await EnsureInitializedAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        await NormalizeAndPruneAsync(connection, transaction, now, cancellationToken);
        var row = await ReadForUpdateAsync(connection, transaction, id, cancellationToken);
        if (row is null || !string.Equals(row.TenantId, approver.TenantId, StringComparison.Ordinal))
            throw NotFound();
        if (string.Equals(row.RequesterSubjectId, approver.SubjectId, StringComparison.Ordinal))
            throw Failure("TOOL_COMPENSATION_SELF_DECISION_DENIED", "补偿申请人与审批人必须分离。",
                ToolCompensationErrorKind.Forbidden);
        if (row.Status != ToolCompensationStatus.AwaitingApproval || !FixedEquals(row.ApprovalId, normalizedApproval))
            throw Failure("TOOL_COMPENSATION_APPROVAL_MISMATCH", "补偿审批不存在、已过期或状态不匹配。",
                ToolCompensationErrorKind.Conflict);
        var next = approved ? ToolCompensationStatus.Approved : ToolCompensationStatus.Rejected;
        var reasonHash = Hash(normalizedReason);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE dbo.{TableName} SET Status=@next,ApproverSubjectId=@approver,DecisionReasonHash=@reason,UpdatedAt=@now
                WHERE Id COLLATE Latin1_General_100_BIN2=@id AND Status=@pending
                  AND ApprovalId COLLATE Latin1_General_100_BIN2=@approval;
                """;
            AddByte(update, "@next", next); AddString(update, "@approver", 256, approver.SubjectId);
            AddAnsiString(update, "@reason", 64, reasonHash); AddDate(update, "@now", now);
            AddString(update, "@id", 64, id); AddByte(update, "@pending", ToolCompensationStatus.AwaitingApproval);
            AddString(update, "@approval", 64, normalizedApproval);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw Failure("TOOL_COMPENSATION_APPROVAL_MISMATCH", "补偿审批状态已经变化。",
                    ToolCompensationErrorKind.Conflict);
        }
        await transaction.CommitAsync(cancellationToken);
        await AuditAsync("tool.compensation.approval", approved ? "approved" : "rejected", approver, id,
            row.CompensationToolName, approved ? "TOOL_COMPENSATION_APPROVED" : "TOOL_COMPENSATION_REJECTED",
            cancellationToken);
        return ToSummary(row with
        {
            Status = next,
            ApproverSubjectId = approver.SubjectId,
            DecisionReasonHash = reasonHash
        });
    }

    public async Task<ToolCompensationExecutionResult> ExecuteAsync(string compensationId, string approvalId,
        string idempotencyKey, AccessContext requester, CancellationToken cancellationToken = default)
    {
        ValidateAccess(requester);
        var id = RequiredText(compensationId, 64, "TOOL_COMPENSATION_ID_INVALID", "补偿标识无效。");
        var normalizedApproval = RequiredText(approvalId, 64, "TOOL_COMPENSATION_APPROVAL_ID_INVALID", "补偿审批标识无效。");
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey)
            ?? throw Failure("TOOL_COMPENSATION_IDEMPOTENCY_KEY_INVALID", "补偿执行必须提供合法的独立幂等键。",
                ToolCompensationErrorKind.Validation);
        var executionKey = ExecutionKey(id, requester, normalizedKey);
        await EnsureInitializedAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var leaseToken = Guid.NewGuid().ToString("N");
        CompensationRow row;
        ICompensableServerTool tool;
        await using (var connection = await OpenAsync(cancellationToken))
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
                         cancellationToken))
        {
            await NormalizeAndPruneAsync(connection, transaction, now, cancellationToken);
            row = await ReadForUpdateAsync(connection, transaction, id, cancellationToken) ?? throw NotFound();
            EnsureOwned(row, requester);
            if (row.Status == ToolCompensationStatus.Completed)
            {
                if (!FixedEquals(row.CompensationExecutionKey, executionKey))
                    throw Failure("TOOL_COMPENSATION_ALREADY_COMPLETED", "该正向执行已经完成补偿。",
                        ToolCompensationErrorKind.Conflict);
                await transaction.CommitAsync(cancellationToken);
                return new ToolCompensationExecutionResult(id, row.Status, "TOOL_COMPENSATION_COMPLETED", true,
                    row.CompletedAt);
            }
            if (row.Status == ToolCompensationStatus.Executing)
                throw Failure("TOOL_COMPENSATION_EXECUTION_IN_PROGRESS", "相同补偿正在执行。",
                    ToolCompensationErrorKind.Conflict);
            if (row.Status is ToolCompensationStatus.OutcomeUnknown or ToolCompensationStatus.ForwardOutcomeUnknown)
                throw OutcomeUnknown();
            if (row.Status != ToolCompensationStatus.Approved || !FixedEquals(row.ApprovalId, normalizedApproval))
                throw Failure("TOOL_COMPENSATION_APPROVAL_REQUIRED", "补偿执行需要匹配的独立批准凭据。",
                    ToolCompensationErrorKind.Forbidden);
            if (!registry.TryGet(row.ForwardToolName, out var registered)
                || registered is not ICompensableServerTool resolvedTool
                || !string.Equals(resolvedTool.CompensationToolName, row.CompensationToolName,
                    StringComparison.Ordinal))
                throw Failure("TOOL_COMPENSATION_CONTRACT_CHANGED", "补偿工具契约已变化，未进入反向执行。",
                    ToolCompensationErrorKind.Conflict);
            if (compensationOptions.ExecutionLeaseDuration <= resolvedTool.Descriptor.Timeout)
                throw Failure("TOOL_COMPENSATION_CONFIGURATION_INVALID",
                    "补偿执行租约必须长于反向工具超时。", ToolCompensationErrorKind.Unavailable);
            tool = resolvedTool;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE dbo.{TableName} SET Status=@executing,CompensationExecutionKey=@execution,
                    ExecutionLeaseToken=@token,ExecutionLeaseExpiresAt=@leaseExpires,UpdatedAt=@now
                WHERE Id COLLATE Latin1_General_100_BIN2=@id AND Status=@approved
                  AND ApprovalId COLLATE Latin1_General_100_BIN2=@approval;
                """;
            AddByte(update, "@executing", ToolCompensationStatus.Executing);
            AddAnsiString(update, "@execution", 64, executionKey); AddString(update, "@token", 64, leaseToken);
            AddDate(update, "@leaseExpires", now.Add(compensationOptions.ExecutionLeaseDuration)); AddDate(update, "@now", now);
            AddString(update, "@id", 64, id); AddByte(update, "@approved", ToolCompensationStatus.Approved);
            AddString(update, "@approval", 64, normalizedApproval);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw Failure("TOOL_COMPENSATION_EXECUTION_IN_PROGRESS", "补偿状态已被其他实例占用。",
                    ToolCompensationErrorKind.Conflict);
            await transaction.CommitAsync(cancellationToken);
        }

        try
        {
            // 事务已提交 Executing 和租约；屏障位于解密及反向副作用之前，供受控强杀验收使用。
            await _executionBarrier.WaitAfterExecutingAsync(executionKey, row.CompensationToolName,
                cancellationToken);
            var snapshotJson = cipher.Unprotect(row.KeyVersion, row.SnapshotCipher,
                SnapshotContext(row.Id, row.ForwardToolName));
            if (cipher.RequiresReencryption(row.KeyVersion))
                await ReencryptAsync(row, leaseToken, snapshotJson, cancellationToken);
            using var document = JsonDocument.Parse(snapshotJson);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(tool.Descriptor.Timeout);
            await tool.CompensateAsync(new ToolExecutionContext(requester, $"comp-{id}"),
                document.RootElement.Clone(), timeout.Token);
            var completedAt = timeProvider.GetUtcNow();
            await using var connection = await OpenAsync(CancellationToken.None);
            await using var complete = connection.CreateCommand();
            complete.CommandText = $"""
                UPDATE dbo.{TableName} SET Status=@completed,CompletedAt=@now,ExecutionLeaseToken=NULL,
                    ExecutionLeaseExpiresAt=NULL,UpdatedAt=@now
                WHERE Id COLLATE Latin1_General_100_BIN2=@id AND Status=@executing
                  AND ExecutionLeaseToken=@token AND ExecutionLeaseExpiresAt>@now;
                """;
            AddByte(complete, "@completed", ToolCompensationStatus.Completed); AddDate(complete, "@now", completedAt);
            AddString(complete, "@id", 64, id); AddByte(complete, "@executing", ToolCompensationStatus.Executing);
            AddString(complete, "@token", 64, leaseToken);
            if (await complete.ExecuteNonQueryAsync(CancellationToken.None) != 1)
            {
                await MarkOutcomeUnknownAsync(id, leaseToken, CancellationToken.None);
                throw Failure("TOOL_COMPENSATION_LEASE_LOST", "补偿执行租约已失效，结果保持不确定。",
                    ToolCompensationErrorKind.Conflict);
            }
            await AuditAsync("tool.compensation.executed", "completed", requester, id, row.CompensationToolName,
                "TOOL_COMPENSATION_COMPLETED", CancellationToken.None);
            return new ToolCompensationExecutionResult(id, ToolCompensationStatus.Completed,
                "TOOL_COMPENSATION_COMPLETED", false, completedAt);
        }
        catch (ToolCompensationException)
        {
            await MarkOutcomeUnknownAsync(id, leaseToken, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await MarkOutcomeUnknownAsync(id, leaseToken, CancellationToken.None);
            await AuditAsync("tool.compensation.executed", "outcome_unknown", requester, id,
                row.CompensationToolName, $"TOOL_COMPENSATION_OUTCOME_UNKNOWN_{exception.GetType().Name}",
                CancellationToken.None);
            throw OutcomeUnknown();
        }
    }

    /// <inheritdoc />
    public async Task<ToolCompensationOutcomeProbeResult> ProbeOutcomeAsync(string compensationId,
        AccessContext reconciler, CancellationToken cancellationToken = default)
    {
        ValidateReconciler(reconciler);
        var id = RequiredText(compensationId, 64, "TOOL_COMPENSATION_ID_INVALID", "补偿标识无效。");
        await EnsureInitializedAsync(cancellationToken);

        CompensationRow row;
        await using (var connection = await OpenAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {Columns} FROM dbo.{TableName} " +
                "WHERE Id COLLATE Latin1_General_100_BIN2=@id " +
                "AND TenantId COLLATE Latin1_General_100_BIN2=@tenant AND Status=@unknown;";
            AddString(command, "@id", 64, id);
            AddString(command, "@tenant", 128, reconciler.TenantId);
            AddByte(command, "@unknown", ToolCompensationStatus.OutcomeUnknown);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw OutcomeUnknownNotFound();
            row = Read(reader);
        }

        if (!_outcomeProbes.TryGetValue(row.CompensationToolName, out var probe))
            throw Failure("TOOL_COMPENSATION_OUTCOME_PROBE_NOT_SUPPORTED",
                "该反向工具尚未提供目标状态探测器。", ToolCompensationErrorKind.Validation);

        // 快照只能在服务端按原认证上下文解密，并以原所有者身份交给专属只读探测器。
        var snapshotJson = cipher.Unprotect(row.KeyVersion, row.SnapshotCipher,
            SnapshotContext(row.Id, row.ForwardToolName));
        using var snapshot = JsonDocument.Parse(snapshotJson);
        var owner = AccessContext.Create(row.TenantId, row.RequesterSubjectId, []);
        var probeResult = await probe.ProbeAsync(row.Id, owner, snapshot.RootElement.Clone(), cancellationToken);
        var result = ToolCompensationProbeResultValidator.Validate(probeResult, row.Id,
            row.CompensationToolName, timeProvider.GetUtcNow());
        await AuditAsync("tool.compensation.reconciliation.probe", result.State.ToString(), reconciler,
            row.Id, row.CompensationToolName, result.Code, cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public async Task<ToolCompensationReconciliationReviewResult> ReviewOutcomeAsync(string compensationId,
        bool confirmed, string reason, AccessContext reconciler, CancellationToken cancellationToken = default)
    {
        ValidateReconciler(reconciler);
        var normalizedReason = RequiredText(reason, 500,
            "TOOL_COMPENSATION_RECONCILIATION_REASON_INVALID", "补偿对账理由必须为 1 到 500 个字符。");
        var evidence = await ProbeOutcomeAsync(compensationId, reconciler, cancellationToken);
        if (confirmed && evidence.State == ToolOutcomeProbeState.Indeterminate)
            throw Failure("TOOL_COMPENSATION_RECONCILIATION_EVIDENCE_INDETERMINATE",
                "不确定证据不能用于结案或授权重试。", ToolCompensationErrorKind.Validation);

        var now = timeProvider.GetUtcNow();
        var evidenceExpiresAt = evidence.ObservedAt.Add(compensationOptions.ReconciliationEvidenceLifetime);
        var reasonHash = Hash(normalizedReason);
        ToolCompensationReconciliationReviewResult result;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        // 先锁定主补偿记录，确保整个复核期间它仍是当前租户的 OutcomeUnknown。
        await using (var compensation = connection.CreateCommand())
        {
            compensation.Transaction = transaction;
            compensation.CommandText = $"SELECT Status FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE Id COLLATE Latin1_General_100_BIN2=@id " +
                "AND TenantId COLLATE Latin1_General_100_BIN2=@tenant;";
            AddString(compensation, "@id", 64, evidence.CompensationId);
            AddString(compensation, "@tenant", 128, reconciler.TenantId);
            var value = await compensation.ExecuteScalarAsync(cancellationToken);
            if (value is null || Convert.ToByte(value, CultureInfo.InvariantCulture) !=
                (byte)ToolCompensationStatus.OutcomeUnknown)
                throw OutcomeUnknownNotFound();
        }

        CompensationReviewRow? current;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"SELECT EvidenceState,EvidenceCode,EvidenceExpiresAt," +
                $"FirstReviewerSubjectId,Status FROM dbo.{ReconciliationTableName} WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE CompensationId COLLATE Latin1_General_100_BIN2=@id;";
            AddString(read, "@id", 64, evidence.CompensationId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            current = await reader.ReadAsync(cancellationToken)
                ? new CompensationReviewRow((ToolOutcomeProbeState)reader.GetByte(0), reader.GetString(1),
                    reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3), reader.GetByte(4))
                : null;
        }

        if (!confirmed)
        {
            await ReplaceFirstReviewAsync(connection, transaction, evidence, reconciler, reasonHash,
                evidenceExpiresAt, status: 1, cancellationToken);
            result = new(evidence.CompensationId, ToolReconciliationReviewStatus.Rejected,
                evidence.State, evidenceExpiresAt);
        }
        else if (current is null || current.Status != 0)
        {
            await ReplaceFirstReviewAsync(connection, transaction, evidence, reconciler, reasonHash,
                evidenceExpiresAt, status: 0, cancellationToken);
            result = new(evidence.CompensationId, ToolReconciliationReviewStatus.AwaitingSecondReviewer,
                evidence.State, evidenceExpiresAt);
        }
        else if (current.EvidenceExpiresAt <= now || evidence.ObservedAt > current.EvidenceExpiresAt)
        {
            // 删除过期首审，返回本次过期结论；下一次调用必须从新的第一人证据开始。
            await using var deleteExpired = connection.CreateCommand();
            deleteExpired.Transaction = transaction;
            deleteExpired.CommandText = $"DELETE dbo.{ReconciliationTableName} " +
                "WHERE CompensationId COLLATE Latin1_General_100_BIN2=@id AND Status=0;";
            AddString(deleteExpired, "@id", 64, evidence.CompensationId);
            await deleteExpired.ExecuteNonQueryAsync(cancellationToken);
            result = new(evidence.CompensationId, ToolReconciliationReviewStatus.EvidenceExpired,
                evidence.State, current.EvidenceExpiresAt);
        }
        else if (string.Equals(current.FirstReviewerSubjectId, reconciler.SubjectId, StringComparison.Ordinal))
        {
            result = new(evidence.CompensationId, ToolReconciliationReviewStatus.ReviewerMustDiffer,
                evidence.State, current.EvidenceExpiresAt);
        }
        else if (current.EvidenceState != evidence.State
                 || !string.Equals(current.EvidenceCode, evidence.Code, StringComparison.Ordinal))
        {
            // 不允许把两次不同观察拼接成双人结论；清除旧首审后必须重新开始。
            await using var deleteChanged = connection.CreateCommand();
            deleteChanged.Transaction = transaction;
            deleteChanged.CommandText = $"DELETE dbo.{ReconciliationTableName} " +
                "WHERE CompensationId COLLATE Latin1_General_100_BIN2=@id AND Status=0;";
            AddString(deleteChanged, "@id", 64, evidence.CompensationId);
            await deleteChanged.ExecuteNonQueryAsync(cancellationToken);
            result = new(evidence.CompensationId, ToolReconciliationReviewStatus.EvidenceChanged,
                evidence.State, current.EvidenceExpiresAt);
        }
        else
        {
            // 第二名不同复核人在同一串行化事务内写入裁决并改变主记录，保证并发单胜者。
            await using (var finish = connection.CreateCommand())
            {
                finish.Transaction = transaction;
                finish.CommandText = $"UPDATE dbo.{ReconciliationTableName} SET " +
                    "SecondReviewerSubjectId=@reviewer,SecondConfirmed=1,SecondReasonHash=@reason," +
                    "SecondReviewedAt=@now,Status=2,UpdatedAt=@now " +
                    "WHERE CompensationId COLLATE Latin1_General_100_BIN2=@id AND Status=0;";
                AddString(finish, "@reviewer", 256, reconciler.SubjectId);
                AddAnsiString(finish, "@reason", 64, reasonHash);
                AddDate(finish, "@now", now); AddString(finish, "@id", 64, evidence.CompensationId);
                if (await finish.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw Failure("TOOL_COMPENSATION_RECONCILIATION_CONFLICT", "补偿复核状态发生并发变化。",
                        ToolCompensationErrorKind.Conflict);
            }

            await using (var resolve = connection.CreateCommand())
            {
                resolve.Transaction = transaction;
                if (evidence.State == ToolOutcomeProbeState.Applied)
                {
                    resolve.CommandText = $"UPDATE dbo.{TableName} SET Status=@completed,CompletedAt=@now," +
                        "ExecutionLeaseToken=NULL,ExecutionLeaseExpiresAt=NULL,UpdatedAt=@now " +
                        "WHERE Id COLLATE Latin1_General_100_BIN2=@id AND Status=@unknown;";
                    AddByte(resolve, "@completed", ToolCompensationStatus.Completed);
                }
                else
                {
                    resolve.CommandText = $"UPDATE dbo.{TableName} SET Status=@available,ApprovalId=NULL," +
                        "Justification=NULL,ApprovalExpiresAt=NULL,ApproverSubjectId=NULL,DecisionReasonHash=NULL," +
                        "CompensationExecutionKey=NULL,ExecutionLeaseToken=NULL,ExecutionLeaseExpiresAt=NULL," +
                        "CompletedAt=NULL,UpdatedAt=@now " +
                        "WHERE Id COLLATE Latin1_General_100_BIN2=@id AND Status=@unknown;";
                    AddByte(resolve, "@available", ToolCompensationStatus.Available);
                }
                AddDate(resolve, "@now", now); AddString(resolve, "@id", 64, evidence.CompensationId);
                AddByte(resolve, "@unknown", ToolCompensationStatus.OutcomeUnknown);
                if (await resolve.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw Failure("TOOL_COMPENSATION_RECONCILIATION_CONFLICT", "补偿主记录状态发生并发变化。",
                        ToolCompensationErrorKind.Conflict);
            }
            result = new(evidence.CompensationId,
                evidence.State == ToolOutcomeProbeState.Applied
                    ? ToolReconciliationReviewStatus.ResolvedApplied
                    : ToolReconciliationReviewStatus.RetryAuthorized,
                evidence.State, current.EvidenceExpiresAt);
        }

        await transaction.CommitAsync(cancellationToken);
        await AuditAsync("tool.compensation.reconciliation.review", result.Status.ToString(), reconciler,
            evidence.CompensationId, evidence.CompensationToolName,
            $"TOOL_COMPENSATION_RECONCILIATION_{result.Status.ToString().ToUpperInvariant()}", cancellationToken);
        return result;
    }

    private static async Task ReplaceFirstReviewAsync(SqlConnection connection, SqlTransaction transaction,
        ToolCompensationOutcomeProbeResult evidence, AccessContext reconciler, string reasonHash,
        DateTimeOffset expiresAt, byte status, CancellationToken cancellationToken)
    {
        await using var replace = connection.CreateCommand();
        replace.Transaction = transaction;
        replace.CommandText = $"DELETE dbo.{ReconciliationTableName} " +
            "WHERE CompensationId COLLATE Latin1_General_100_BIN2=@id; " +
            $"INSERT dbo.{ReconciliationTableName} (CompensationId,TenantId,EvidenceState,EvidenceCode," +
            "EvidenceObservedAt,EvidenceExpiresAt,FirstReviewerSubjectId,FirstConfirmed,FirstReasonHash," +
            "FirstReviewedAt,Status,UpdatedAt) VALUES(@id,@tenant,@state,@code,@observed,@expires," +
            "@reviewer,@confirmed,@reason,@now,@status,@now);";
        AddString(replace, "@id", 64, evidence.CompensationId);
        AddString(replace, "@tenant", 128, reconciler.TenantId);
        AddInt(replace, "@state", (int)evidence.State); AddString(replace, "@code", 128, evidence.Code);
        AddDate(replace, "@observed", evidence.ObservedAt); AddDate(replace, "@expires", expiresAt);
        AddString(replace, "@reviewer", 256, reconciler.SubjectId);
        AddBool(replace, "@confirmed", status == 0); AddAnsiString(replace, "@reason", 64, reasonHash);
        AddDate(replace, "@now", evidence.ObservedAt); AddInt(replace, "@status", status);
        await replace.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TransitionPreparationAsync(ToolCompensationPreparation preparation,
        ToolCompensationStatus next, string outcome, string code, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=@next,UpdatedAt=@now
            OUTPUT inserted.TenantId,inserted.RequesterSubjectId,inserted.ForwardToolName
            WHERE Id COLLATE Latin1_General_100_BIN2=@id AND Status=@prepared AND PreparationTokenHash=@hash;
            """;
        AddByte(command, "@next", next); AddDate(command, "@now", timeProvider.GetUtcNow());
        AddString(command, "@id", 64, preparation.Id); AddByte(command, "@prepared", ToolCompensationStatus.Prepared);
        AddAnsiString(command, "@hash", 64, Hash(preparation.PreparationToken));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw PreparationLost();
        var access = AccessContext.Create(reader.GetString(0), reader.GetString(1), []);
        var toolName = reader.GetString(2);
        await reader.DisposeAsync();
        await AuditAsync("tool.compensation.available", outcome, access, preparation.Id, toolName, code,
            cancellationToken);
    }

    private async Task ReencryptAsync(CompensationRow row, string leaseToken, string plaintext,
        CancellationToken cancellationToken)
    {
        var updated = cipher.Protect(plaintext, SnapshotContext(row.Id, row.ForwardToolName));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE dbo.{TableName} SET KeyVersion=@version,SnapshotCipher=@snapshot,UpdatedAt=@now
            WHERE Id COLLATE Latin1_General_100_BIN2=@id AND Status=@executing AND ExecutionLeaseToken=@token;
            """;
        AddString(command, "@version", 64, updated.KeyVersion); AddString(command, "@snapshot", -1, updated.Ciphertext);
        AddDate(command, "@now", timeProvider.GetUtcNow()); AddString(command, "@id", 64, row.Id);
        AddByte(command, "@executing", ToolCompensationStatus.Executing); AddString(command, "@token", 64, leaseToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw Failure("TOOL_COMPENSATION_LEASE_LOST", "补偿执行租约已经失效。",
                ToolCompensationErrorKind.Conflict);
    }

    private async Task MarkOutcomeUnknownAsync(string id, string leaseToken, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=@unknown,ExecutionLeaseToken=NULL,ExecutionLeaseExpiresAt=NULL,UpdatedAt=@now
            WHERE Id COLLATE Latin1_General_100_BIN2=@id AND Status=@executing AND ExecutionLeaseToken=@token;
            """;
        AddByte(command, "@unknown", ToolCompensationStatus.OutcomeUnknown); AddDate(command, "@now", timeProvider.GetUtcNow());
        AddString(command, "@id", 64, id); AddByte(command, "@executing", ToolCompensationStatus.Executing);
        AddString(command, "@token", 64, leaseToken); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            ValidateConfiguration();
            if (!sqlOptions.InitializeSchema) { _initialized = true; return; }
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = RuntimeSchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initializationGate.Release(); }
    }

    private async Task NormalizeDueAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        await NormalizeAndPruneAsync(connection, transaction, timeProvider.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<(string TenantId, string SubjectId, string ToolName)> ReadAuditIdentityAsync(string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT TenantId,RequesterSubjectId,ForwardToolName FROM dbo.{TableName} " +
            "WHERE Id COLLATE Latin1_General_100_BIN2=@id;";
        AddString(command, "@id", 64, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw PreparationLost();
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private async Task NormalizeAndPruneAsync(SqlConnection connection, SqlTransaction transaction, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=@outcomeUnknown,ExecutionLeaseToken=NULL,
                ExecutionLeaseExpiresAt=NULL,UpdatedAt=@now
              WHERE Status=@executing AND ExecutionLeaseExpiresAt<=@now;
            UPDATE dbo.{TableName} SET Status=@available,ApprovalId=NULL,ApprovalExpiresAt=NULL,
                ApproverSubjectId=NULL,DecisionReasonHash=NULL,UpdatedAt=@now
              WHERE Status IN (@awaiting,@approved) AND ApprovalExpiresAt<=@now;
            UPDATE dbo.{TableName} SET Status=@expired,UpdatedAt=@now
              WHERE ExpiresAt<=@now AND Status NOT IN (@completed,@outcomeUnknown,@forwardUnknown);
            DELETE dbo.{TableName} WHERE ExpiresAt<=@deleteBefore;
            """;
        AddByte(command, "@outcomeUnknown", ToolCompensationStatus.OutcomeUnknown);
        AddByte(command, "@executing", ToolCompensationStatus.Executing); AddDate(command, "@now", now);
        AddByte(command, "@available", ToolCompensationStatus.Available);
        AddByte(command, "@awaiting", ToolCompensationStatus.AwaitingApproval);
        AddByte(command, "@approved", ToolCompensationStatus.Approved);
        AddByte(command, "@expired", ToolCompensationStatus.Expired);
        AddByte(command, "@completed", ToolCompensationStatus.Completed);
        AddByte(command, "@forwardUnknown", ToolCompensationStatus.ForwardOutcomeUnknown);
        AddDate(command, "@deleteBefore", now.Subtract(compensationOptions.CompensationLifetime));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<CompensationRow?> ReadForUpdateAsync(SqlConnection connection, SqlTransaction transaction,
        string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE Id COLLATE Latin1_General_100_BIN2=@id;";
        AddString(command, "@id", 64, id); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static CompensationRow Read(SqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), (ToolCompensationStatus)reader.GetByte(6), reader.GetString(7), reader.GetString(8),
        reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10), reader.GetFieldValue<DateTimeOffset>(11),
        OptionalString(reader, 12), OptionalString(reader, 13), OptionalDate(reader, 14), OptionalString(reader, 15),
        OptionalString(reader, 16), OptionalString(reader, 17), OptionalString(reader, 18), OptionalDate(reader, 19),
        OptionalDate(reader, 20));

    private static ToolCompensationSummary ToSummary(CompensationRow row) => new(row.Id, row.ForwardToolName,
        row.CompensationToolName, row.Status, row.CreatedAt, row.ExpiresAt, row.ApprovalId, row.Justification,
        row.CompletedAt);

    private static string? OptionalString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? OptionalDate(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    private static void EnsureOwned(CompensationRow? row, AccessContext access)
    {
        if (row is null || !string.Equals(row.TenantId, access.TenantId, StringComparison.Ordinal)
            || !string.Equals(row.RequesterSubjectId, access.SubjectId, StringComparison.Ordinal)) throw NotFound();
    }
    private static void ValidateAccess(AccessContext access)
    {
        if (string.IsNullOrWhiteSpace(access.TenantId) || string.IsNullOrWhiteSpace(access.SubjectId))
            throw Failure("TOOL_COMPENSATION_ACCESS_INVALID", "补偿访问身份无效。", ToolCompensationErrorKind.Validation);
    }
    private void ValidateReconciler(AccessContext access)
    {
        ValidateAccess(access);
        if (!access.Groups.Overlaps(compensationOptions.ReconcilerGroups))
            throw Failure("TOOL_COMPENSATION_RECONCILER_ROLE_REQUIRED", "当前用户不属于工具对账人员组。",
                ToolCompensationErrorKind.Forbidden);
    }
    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(sqlOptions.ConnectionString) || compensationOptions.CompensationLifetime <= TimeSpan.Zero
            || compensationOptions.ApprovalLifetime <= TimeSpan.Zero || compensationOptions.ExecutionLeaseDuration <= TimeSpan.Zero
            || compensationOptions.CompensationLifetime <= compensationOptions.ApprovalLifetime
            || compensationOptions.CompensationLifetime <= compensationOptions.ExecutionLeaseDuration
            || compensationOptions.MaximumEntries <= 0 || compensationOptions.MaximumSnapshotBytes <= 0
            || compensationOptions.ApproverGroups.Count == 0 || compensationOptions.ReconcilerGroups.Count == 0
            || compensationOptions.ReconciliationEvidenceLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("SQL Server 工具补偿配置无效。");
    }
    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    { var connection = new SqlConnection(sqlOptions.ConnectionString); await connection.OpenAsync(cancellationToken); return connection; }
    private Task AuditAsync(string name, string outcome, AccessContext actor, string id, string tool, string code,
        CancellationToken cancellationToken) => traceSink.WriteAsync($"tool-compensation-{id}",
        [new TraceStep(name, outcome, timeProvider.GetUtcNow(), new Dictionary<string, object?>
        { ["tenant"] = actor.TenantId, ["subject"] = actor.SubjectId, ["compensationId"] = id,
            ["tool"] = tool, ["code"] = code })], cancellationToken);
    private static string RequiredText(string? value, int max, string code, string message)
    { var normalized = value?.Trim(); return normalized is { Length: > 0 } && normalized.Length <= max ? normalized : throw Failure(code, message, ToolCompensationErrorKind.Validation); }
    private static string? NormalizeIdempotencyKey(string? value)
    { var normalized = value?.Trim(); return normalized is { Length: > 0 and <= 128 } && normalized.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') ? normalized : null; }
    private static string ExecutionKey(string id, AccessContext access, string key) => Hash($"{id}\u001f{access.TenantId}\u001f{access.SubjectId}\u001f{key}");
    private static string SnapshotContext(string id, string tool) => $"tool-compensation:{id}:{tool}";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedEquals(string? left, string? right) => left is not null && right is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static ToolCompensationException PreparationLost() => Failure("TOOL_COMPENSATION_PREPARATION_LOST",
        "补偿准备令牌无效或状态已经变化。", ToolCompensationErrorKind.Conflict);
    private static ToolCompensationException NotFound() => Failure("TOOL_COMPENSATION_NOT_FOUND",
        "没有找到当前用户可访问的补偿。", ToolCompensationErrorKind.NotFound);
    private static ToolCompensationException OutcomeUnknown() => Failure("TOOL_COMPENSATION_OUTCOME_UNKNOWN",
        "正向或补偿结果不确定，禁止自动重试。", ToolCompensationErrorKind.Conflict);
    private static ToolCompensationException OutcomeUnknownNotFound() => Failure(
        "TOOL_COMPENSATION_OUTCOME_UNKNOWN_NOT_FOUND", "当前租户不存在可对账的结果不确定补偿。",
        ToolCompensationErrorKind.NotFound);
    private static ToolCompensationException Failure(string code, string message, ToolCompensationErrorKind kind) => new(code, message, kind);
    private static void AddString(SqlCommand command, string name, int size, string value) => command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;
    private static void AddAnsiString(SqlCommand command, string name, int size, string value) => command.Parameters.Add(name, SqlDbType.Char, size).Value = value;
    private static void AddDate(SqlCommand command, string name, DateTimeOffset value) => command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value;
    private static void AddByte(SqlCommand command, string name, ToolCompensationStatus value) => command.Parameters.Add(name, SqlDbType.TinyInt).Value = (byte)value;
    private static void AddInt(SqlCommand command, string name, int value) => command.Parameters.Add(name, SqlDbType.Int).Value = value;
    private static void AddBool(SqlCommand command, string name, bool value) => command.Parameters.Add(name, SqlDbType.Bit).Value = value;

    private const string RuntimeSchemaSql = """
        SET XACT_ABORT ON; BEGIN TRANSACTION;
        DECLARE @lockResult int; EXEC @lockResult=sys.sp_getapplock @Resource=N'AiMentor.Workflow.Schema',
          @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000;
        IF @lockResult < 0 THROW 51000,'无法获取 AiMentor 数据库架构锁。',1;
        IF OBJECT_ID(N'dbo.AiMentorToolCompensations',N'U') IS NULL
        BEGIN
          CREATE TABLE dbo.AiMentorToolCompensations(
            Id nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL
              CONSTRAINT PK_AiMentorToolCompensations PRIMARY KEY,
            ForwardExecutionKey char(64) NOT NULL,
            TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            RequesterSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ForwardToolName nvarchar(128) NOT NULL,
            CompensationToolName nvarchar(128) NOT NULL,Status tinyint NOT NULL,PreparationTokenHash char(64) NOT NULL,
            KeyVersion nvarchar(64) NOT NULL,SnapshotCipher nvarchar(max) NOT NULL,CreatedAt datetimeoffset(7) NOT NULL,
            ExpiresAt datetimeoffset(7) NOT NULL,
            ApprovalId nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL,Justification nvarchar(500) NULL,
            ApprovalExpiresAt datetimeoffset(7) NULL,
            ApproverSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,DecisionReasonHash char(64) NULL,
            CompensationExecutionKey char(64) NULL,ExecutionLeaseToken nvarchar(64) NULL,
            ExecutionLeaseExpiresAt datetimeoffset(7) NULL,CompletedAt datetimeoffset(7) NULL,
            UpdatedAt datetimeoffset(7) NOT NULL,RowVersion rowversion NOT NULL,
            CONSTRAINT UQ_AiMentorToolCompensations_ForwardExecution UNIQUE(ForwardExecutionKey));
          CREATE INDEX IX_AiMentorToolCompensations_TenantStatusCreated
            ON dbo.AiMentorToolCompensations(TenantId,Status,CreatedAt DESC);
          CREATE UNIQUE INDEX UX_AiMentorToolCompensations_Approval
            ON dbo.AiMentorToolCompensations(ApprovalId) WHERE ApprovalId IS NOT NULL;
        END;
        IF OBJECT_ID(N'dbo.AiMentorToolCompensationReconciliations',N'U') IS NULL
        BEGIN
          CREATE TABLE dbo.AiMentorToolCompensationReconciliations(
            CompensationId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL
              CONSTRAINT PK_AiMentorToolCompensationReconciliations PRIMARY KEY,
            TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            EvidenceState tinyint NOT NULL,EvidenceCode nvarchar(128) NOT NULL,
            EvidenceObservedAt datetimeoffset(7) NOT NULL,EvidenceExpiresAt datetimeoffset(7) NOT NULL,
            FirstReviewerSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,FirstConfirmed bit NOT NULL,
            FirstReasonHash char(64) NOT NULL,FirstReviewedAt datetimeoffset(7) NOT NULL,
            SecondReviewerSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,SecondConfirmed bit NULL,
            SecondReasonHash char(64) NULL,SecondReviewedAt datetimeoffset(7) NULL,
            Status tinyint NOT NULL,UpdatedAt datetimeoffset(7) NOT NULL,RowVersion rowversion NOT NULL,
            CONSTRAINT FK_AiMentorToolCompensationReconciliations_Compensation FOREIGN KEY(CompensationId)
              REFERENCES dbo.AiMentorToolCompensations(Id) ON DELETE CASCADE);
          CREATE INDEX IX_AiMentorToolCompensationReconciliations_TenantStatusExpiry
            ON dbo.AiMentorToolCompensationReconciliations(TenantId,Status,EvidenceExpiresAt);
        END; COMMIT TRANSACTION;
        """;

    private sealed record CompensationRow(string Id, string ForwardExecutionKey, string TenantId,
        string RequesterSubjectId, string ForwardToolName, string CompensationToolName,
        ToolCompensationStatus Status, string PreparationTokenHash, string KeyVersion, string SnapshotCipher,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? ApprovalId, string? Justification,
        DateTimeOffset? ApprovalExpiresAt, string? ApproverSubjectId, string? DecisionReasonHash,
        string? CompensationExecutionKey, string? ExecutionLeaseToken, DateTimeOffset? ExecutionLeaseExpiresAt,
        DateTimeOffset? CompletedAt);

    private sealed record CompensationReviewRow(ToolOutcomeProbeState EvidenceState, string EvidenceCode,
        DateTimeOffset EvidenceExpiresAt, string FirstReviewerSubjectId, byte Status);

    public void Dispose() => _initializationGate.Dispose();
}
