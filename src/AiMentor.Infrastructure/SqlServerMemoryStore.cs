using System.Data;
using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.Data.SqlClient;

namespace AiMentor.Infrastructure;

/// <summary>
/// 将记忆提案和正式记忆持久化到 SQL Server；键和值仅以认证密文和不可逆指纹落库。
/// 所有身份与资源标识使用序数语义，批准事务通过键区间锁保证跨实例单胜者。
/// </summary>
public sealed class SqlServerMemoryStore(SqlServerWorkflowOptions options, IMemoryCipher cipher,
    MemoryWorkflowOptions? workflowOptions = null) : IMemoryStore, IMemoryRetentionStore, IDisposable
{
    private const string ProposalTable = "AiMentorMemoryProposals";
    private const string MemoryTable = "AiMentorMemories";
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly MemoryWorkflowOptions _workflowOptions = workflowOptions ?? new MemoryWorkflowOptions();
    private volatile bool _initialized;

    /// <summary>验证生产迁移已创建两张记忆表且当前连接可读取，不返回任何业务数据。</summary>
    public async Task ProbeReadinessAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ReadinessSql;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("SQL Server 记忆迁移 012/014 的结构、索引、排序规则或 DML 权限不完整。");

        await using (var versions = connection.CreateCommand())
        {
            versions.CommandText = CipherVersionsSql;
            await using var reader = await versions.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var envelopeVersion = reader.GetString(0);
                var keyVersion = reader.GetString(1);
                if (!cipher.SupportsCiphertextVersion(envelopeVersion, keyVersion))
                    throw new InvalidOperationException(
                        $"SQL Server 记忆中存在当前密钥环不支持的密文版本 {envelopeVersion}:{keyVersion}。");
                try
                {
                    var context = Context(reader.GetString(2), reader.GetString(3),
                        (MemoryScope)reader.GetByte(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetString(6), "key");
                    _ = cipher.Unprotect(reader.GetString(7), context);
                }
                catch (Exception exception) when (exception is FormatException
                                                   or System.Security.Cryptography.CryptographicException)
                {
                    throw new InvalidOperationException(
                        $"SQL Server 记忆密钥版本 {keyVersion} 无法认证解开现存密文。", exception);
                }
            }
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await using var lockProbe = connection.CreateCommand();
        lockProbe.Transaction = transaction;
        lockProbe.CommandText = """
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=N'AiMentor.Memory.Retention.Readiness',
                @LockMode='Shared',@LockOwner='Transaction',@LockTimeout=0;
            SELECT @result;
            """;
        var lockResult = Convert.ToInt32(await lockProbe.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        await transaction.RollbackAsync(cancellationToken);
        if (lockResult < 0)
            throw new InvalidOperationException("SQL Server 记忆保留期清理无法取得数据库应用锁。");
    }

    /// <inheritdoc />
    public async Task SaveProposalAsync(MemoryProposal proposal, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var keyCipher = cipher.Protect(proposal.Key, Context(proposal, "key"));
        var valueCipher = cipher.Protect(proposal.Value, Context(proposal, "value"));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            await PruneTenantExpiredAsync(connection, transaction, proposal.TenantId, proposal.CreatedAt,
                cancellationToken);
            await using (var capacity = connection.CreateCommand())
            {
                capacity.Transaction = transaction;
                capacity.CommandText = $"SELECT COUNT_BIG(1) FROM dbo.{ProposalTable} WITH (UPDLOCK,HOLDLOCK) " +
                    "WHERE TenantId=@tenantId AND Status=0;";
                AddString(capacity, "@tenantId", 128, proposal.TenantId);
                if (Convert.ToInt64(await capacity.ExecuteScalarAsync(cancellationToken),
                        System.Globalization.CultureInfo.InvariantCulture)
                    >= _workflowOptions.MaximumPendingProposalsPerTenant)
                {
                    await transaction.CommitAsync(cancellationToken);
                    throw new MemoryStoreCapacityException();
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO dbo.{ProposalTable}
                    (Id,TenantId,SubjectId,Scope,SessionId,KeyVersion,KeyCipher,KeyFingerprint,ValueCipher,
                     CreatedAt,ApprovalExpiresAt,MemoryExpiresAt,Status)
                VALUES
                    (@id,@tenantId,@subjectId,@scope,@sessionId,@keyVersion,@keyCipher,@keyFingerprint,@valueCipher,
                     @createdAt,@approvalExpiresAt,@memoryExpiresAt,@status);
                """;
            AddString(command, "@id", 128, proposal.Id);
            AddString(command, "@tenantId", 128, proposal.TenantId);
            AddString(command, "@subjectId", 256, proposal.SubjectId);
            AddByte(command, "@scope", (byte)proposal.Scope);
            AddNullableString(command, "@sessionId", 128, proposal.SessionId);
            AddString(command, "@keyVersion", 64, cipher.ActiveKeyVersion);
            AddString(command, "@keyCipher", -1, keyCipher);
            AddFingerprint(command, "@keyFingerprint", cipher.Fingerprint(proposal.Key,
                FingerprintContext(proposal.TenantId, proposal.SubjectId, proposal.Scope, proposal.SessionId)));
            AddString(command, "@valueCipher", -1, valueCipher);
            AddDateTimeOffset(command, "@createdAt", proposal.CreatedAt);
            AddDateTimeOffset(command, "@approvalExpiresAt", proposal.ApprovalExpiresAt);
            AddDateTimeOffset(command, "@memoryExpiresAt", proposal.MemoryExpiresAt);
            AddByte(command, "@status", (byte)proposal.Status);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await RollbackAsync(transaction);
            throw new InvalidOperationException("记忆提案标识发生冲突。", exception);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MemoryStoreResult<MemoryRecord>> ApproveAsync(string proposalId, AccessContext access,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            var proposal = await ReadProposalAsync(connection, transaction, proposalId, cancellationToken);
            if (proposal is null || !OwnedBy(proposal.TenantId, proposal.SubjectId, access))
                return await CommitAsync(transaction,
                    new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.NotFound), cancellationToken);
            if (proposal.Status != MemoryProposalStatus.PendingApproval)
            {
                await DeleteProposalAsync(connection, transaction, proposal.Id, cancellationToken);
                return await CommitAsync(transaction,
                    new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Conflict), cancellationToken);
            }
            if (proposal.ApprovalExpiresAt <= now || proposal.MemoryExpiresAt <= now)
            {
                await DeleteProposalAsync(connection, transaction, proposal.Id, cancellationToken);
                return await CommitAsync(transaction,
                    new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Expired), cancellationToken);
            }

            await PruneTenantExpiredAsync(connection, transaction, proposal.TenantId, now, cancellationToken);

            await using (var duplicate = connection.CreateCommand())
            {
                duplicate.Transaction = transaction;
                // HOLDLOCK 在精确复合索引区间上建立可串行化键锁，防止两个节点同时批准同一键。
                duplicate.CommandText = $"""
                    SELECT TOP(1) 1
                    FROM dbo.{MemoryTable} WITH (UPDLOCK,HOLDLOCK,INDEX(IX_AiMentorMemories_OwnerKey))
                    WHERE TenantId=@tenantId AND SubjectId=@subjectId AND Scope=@scope
                      AND ((SessionId IS NULL AND @sessionId IS NULL) OR SessionId=@sessionId)
                      AND KeyFingerprint=@keyFingerprint AND ExpiresAt>@now;
                    """;
                AddString(duplicate, "@tenantId", 128, proposal.TenantId);
                AddString(duplicate, "@subjectId", 256, proposal.SubjectId);
                AddByte(duplicate, "@scope", (byte)proposal.Scope);
                AddNullableString(duplicate, "@sessionId", 128, proposal.SessionId);
                AddFingerprint(duplicate, "@keyFingerprint", proposal.KeyFingerprint);
                AddDateTimeOffset(duplicate, "@now", now);
                if (await duplicate.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    await DeleteProposalAsync(connection, transaction, proposal.Id, cancellationToken);
                    return await CommitAsync(transaction,
                        new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.AlreadyExists), cancellationToken);
                }
            }

            var memory = new MemoryRecord(Guid.NewGuid().ToString("N"), proposal.TenantId, proposal.SubjectId,
                proposal.Scope, proposal.SessionId,
                cipher.Unprotect(proposal.KeyCipher, Context(proposal, "key")),
                cipher.Unprotect(proposal.ValueCipher, Context(proposal, "value")),
                1, now, now, proposal.MemoryExpiresAt);
            await InsertMemoryAsync(connection, transaction, memory, cancellationToken);
            if (await DeleteProposalAsync(connection, transaction, proposal.Id, cancellationToken) != 1)
                throw new InvalidOperationException("记忆提案批准状态发生并发冲突。");
            return await CommitAsync(transaction,
                new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Success, memory), cancellationToken);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(AccessContext access, MemoryScope? scope,
        string? sessionId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await PruneTenantExpiredAsync(access.TenantId, now, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id,TenantId,SubjectId,Scope,SessionId,KeyVersion,KeyCipher,KeyFingerprint,ValueCipher,
                   Version,CreatedAt,UpdatedAt,ExpiresAt
            FROM dbo.{MemoryTable}
            WHERE TenantId=@tenantId AND SubjectId=@subjectId AND ExpiresAt>@now
              AND (@scope IS NULL OR Scope=@scope)
              AND (@sessionId IS NULL OR SessionId=@sessionId)
            ORDER BY Scope,KeyFingerprint;
            """;
        AddString(command, "@tenantId", 128, access.TenantId);
        AddString(command, "@subjectId", 256, access.SubjectId);
        AddNullableByte(command, "@scope", scope is null ? null : (byte)scope.Value);
        AddNullableString(command, "@sessionId", 128, sessionId);
        AddDateTimeOffset(command, "@now", now);
        var stored = new List<StoredMemory>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) stored.Add(ReadMemory(reader));
        }
        var result = new List<MemoryRecord>(stored.Count);
        foreach (var memory in stored)
            result.Add(await ToMemoryRecordAndRotateAsync(connection, null, memory, cancellationToken));
        return result;
    }

    /// <inheritdoc />
    public async Task<MemoryStoreResult<MemoryRecord>> UpdateAsync(string memoryId, AccessContext access,
        int expectedVersion, string value, DateTimeOffset? expiresAt, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await PruneTenantExpiredAsync(connection, transaction, access.TenantId, now, cancellationToken);
            var stored = await ReadMemoryAsync(connection, transaction, memoryId, true, cancellationToken);
            if (stored is null || !OwnedBy(stored.TenantId, stored.SubjectId, access) || stored.ExpiresAt <= now)
                return await CommitAsync(transaction,
                    new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.NotFound), cancellationToken);
            if (stored.Version != expectedVersion)
                return await CommitAsync(transaction,
                    new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Conflict), cancellationToken);
            if (expiresAt > stored.ExpiresAt)
                return await CommitAsync(transaction,
                    new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.RetentionExceeded), cancellationToken);

            var updated = ToMemoryRecord(stored) with
            {
                Value = value,
                Version = stored.Version + 1,
                UpdatedAt = now,
                ExpiresAt = expiresAt ?? stored.ExpiresAt
            };
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE dbo.{MemoryTable}
                SET KeyVersion=@keyVersion,KeyCipher=@keyCipher,KeyFingerprint=@keyFingerprint,ValueCipher=@valueCipher,
                    Version=@nextVersion,UpdatedAt=@updatedAt,ExpiresAt=@expiresAt
                WHERE Id=@id AND Version=@expectedVersion;
                """;
            AddString(update, "@keyVersion", 64, cipher.ActiveKeyVersion);
            AddString(update, "@keyCipher", -1, cipher.Protect(updated.Key, Context(updated, "key")));
            AddFingerprint(update, "@keyFingerprint", cipher.Fingerprint(updated.Key,
                FingerprintContext(updated.TenantId, updated.SubjectId, updated.Scope, updated.SessionId)));
            AddString(update, "@valueCipher", -1, cipher.Protect(value, Context(updated, "value")));
            AddInt(update, "@nextVersion", updated.Version);
            AddDateTimeOffset(update, "@updatedAt", updated.UpdatedAt);
            AddDateTimeOffset(update, "@expiresAt", updated.ExpiresAt);
            AddString(update, "@id", 128, updated.Id);
            AddInt(update, "@expectedVersion", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("记忆版本在事务内发生意外变化。");
            return await CommitAsync(transaction,
                new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Success, updated), cancellationToken);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MemoryStoreResult<bool>> DeleteAsync(string memoryId, AccessContext access,
        int expectedVersion, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var stored = await ReadMemoryAsync(connection, transaction, memoryId, true, cancellationToken);
            if (stored is null || !OwnedBy(stored.TenantId, stored.SubjectId, access))
                return await CommitAsync(transaction,
                    new MemoryStoreResult<bool>(MemoryStoreStatus.NotFound), cancellationToken);
            if (stored.Version != expectedVersion)
                return await CommitAsync(transaction,
                    new MemoryStoreResult<bool>(MemoryStoreStatus.Conflict), cancellationToken);
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM dbo.{MemoryTable} WHERE Id=@id AND Version=@expectedVersion;";
            AddString(delete, "@id", 128, memoryId);
            AddInt(delete, "@expectedVersion", expectedVersion);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("记忆版本在删除事务内发生意外变化。");
            return await CommitAsync(transaction,
                new MemoryStoreResult<bool>(MemoryStoreStatus.Success, true), cancellationToken);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MemoryTargetState> ProbeTargetStateAsync(string memoryId, AccessContext access,
        int expectedVersion, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var memory = await ReadMemoryAsync(connection, null, memoryId, false, cancellationToken);
        if (memory is null) return MemoryTargetState.Absent;
        if (!OwnedBy(memory.TenantId, memory.SubjectId, access)) return MemoryTargetState.Inaccessible;
        return memory.Version == expectedVersion
            ? MemoryTargetState.PresentAtExpectedVersion
            : MemoryTargetState.PresentAtDifferentVersion;
    }

    /// <inheritdoc />
    public async Task<MemoryStoreResult<MemoryRecord>> RestoreCompensationAsync(string memoryId,
        AccessContext access, int expectedVersion, string previousValue, DateTimeOffset previousExpiresAt,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var stored = await ReadMemoryAsync(connection, transaction, memoryId, true, cancellationToken);
            if (stored is null || !OwnedBy(stored.TenantId, stored.SubjectId, access))
                return await CommitAsync(transaction,
                    new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.NotFound), cancellationToken);
            if (stored.Version != expectedVersion)
                return await CommitAsync(transaction,
                    new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Conflict), cancellationToken);

            // 该专用路径只接收服务器认证过的补偿快照，允许恢复正向更正前的原期限。
            var restored = ToMemoryRecord(stored) with
            {
                Value = previousValue,
                Version = stored.Version + 1,
                UpdatedAt = now,
                ExpiresAt = previousExpiresAt
            };
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE dbo.{MemoryTable}
                SET KeyVersion=@keyVersion,KeyCipher=@keyCipher,KeyFingerprint=@keyFingerprint,ValueCipher=@valueCipher,
                    Version=@nextVersion,UpdatedAt=@updatedAt,ExpiresAt=@expiresAt
                WHERE Id=@id AND Version=@expectedVersion;
                """;
            AddString(update, "@keyVersion", 64, cipher.ActiveKeyVersion);
            AddString(update, "@keyCipher", -1, cipher.Protect(restored.Key, Context(restored, "key")));
            AddFingerprint(update, "@keyFingerprint", cipher.Fingerprint(restored.Key,
                FingerprintContext(restored.TenantId, restored.SubjectId, restored.Scope, restored.SessionId)));
            AddString(update, "@valueCipher", -1,
                cipher.Protect(previousValue, Context(restored, "value")));
            AddInt(update, "@nextVersion", restored.Version);
            AddDateTimeOffset(update, "@updatedAt", restored.UpdatedAt);
            AddDateTimeOffset(update, "@expiresAt", restored.ExpiresAt);
            AddString(update, "@id", 128, restored.Id);
            AddInt(update, "@expectedVersion", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("记忆版本在补偿事务内发生意外变化。");
            return await CommitAsync(transaction,
                new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Success, restored), cancellationToken);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MemoryCorrectionCompensationState> ProbeCorrectionCompensationAsync(string memoryId,
        AccessContext access, int expectedVersionAfterForward, string previousValue,
        DateTimeOffset previousExpiresAt, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var memory = await ReadMemoryAsync(connection, null, memoryId, false, cancellationToken);
        if (memory is null || !OwnedBy(memory.TenantId, memory.SubjectId, access))
            return MemoryCorrectionCompensationState.Inaccessible;
        var record = await ToMemoryRecordAndRotateAsync(connection, null, memory, cancellationToken);
        if (memory.Version == expectedVersionAfterForward)
            return MemoryCorrectionCompensationState.NotApplied;
        return memory.Version == expectedVersionAfterForward + 1
            && string.Equals(record.Value, previousValue, StringComparison.Ordinal)
            && memory.ExpiresAt.Equals(previousExpiresAt)
                ? MemoryCorrectionCompensationState.Applied
                : MemoryCorrectionCompensationState.Changed;
    }

    /// <inheritdoc />
    public async Task<MemoryPurgeResult> PurgeExpiredAsync(int maximumRowsPerTable,
        CancellationToken cancellationToken = default)
    {
        if (maximumRowsPerTable is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(maximumRowsPerTable), "单次清理批次必须在 1 到 10000 之间。");
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using (var acquire = connection.CreateCommand())
            {
                acquire.Transaction = transaction;
                acquire.CommandText = """
                    DECLARE @result int;
                    EXEC @result=sys.sp_getapplock @Resource=N'AiMentor.Memory.Retention',
                        @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
                    SELECT @result;
                    """;
                var result = Convert.ToInt32(await acquire.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (result < 0)
                {
                    var state = await ReadRetentionStateAsync(connection, transaction, cancellationToken);
                    return await CommitAsync(transaction,
                        new MemoryPurgeResult(false, 0, 0, state.LastCompletedAt, state.MonitoringStartedAt),
                        cancellationToken);
                }
            }

            // 永久删除的截止时间必须来自数据库，不能信任任一应用节点可能漂移的本机时钟。
            var now = await ReadDatabaseUtcNowAsync(connection, transaction, cancellationToken);
            var proposalsDeleted = 0;
            proposalsDeleted += await DeleteBatchAsync(connection, transaction, ProposalTable,
                "Status<>0", now, maximumRowsPerTable - proposalsDeleted, cancellationToken);
            proposalsDeleted += await DeleteBatchAsync(connection, transaction, ProposalTable,
                "Status=0 AND ApprovalExpiresAt<=@now", now, maximumRowsPerTable - proposalsDeleted,
                cancellationToken);
            proposalsDeleted += await DeleteBatchAsync(connection, transaction, ProposalTable,
                "MemoryExpiresAt<=@now", now, maximumRowsPerTable - proposalsDeleted, cancellationToken);
            var memoriesDeleted = await DeleteBatchAsync(connection, transaction, MemoryTable,
                "ExpiresAt<=@now", now, maximumRowsPerTable, cancellationToken);
            RetentionState completedState;
            await using (var completion = connection.CreateCommand())
            {
                completion.Transaction = transaction;
                completion.CommandText = """
                    UPDATE dbo.AiMentorMemoryRetentionState
                    SET LastCompletedAt=SYSUTCDATETIME()
                    OUTPUT inserted.LastCompletedAt,inserted.MonitoringStartedAt
                    WHERE Id=1;
                    """;
                await using var reader = await completion.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("记忆保留期共享成功状态不存在，拒绝把清理误报为成功。");
                completedState = new RetentionState(reader.GetDateTimeOffset(0), reader.GetDateTimeOffset(1));
            }
            return await CommitAsync(transaction,
                new MemoryPurgeResult(true, proposalsDeleted, memoriesDeleted, completedState.LastCompletedAt,
                    completedState.MonitoringStartedAt), cancellationToken);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    private static async Task<RetentionState> ReadRetentionStateAsync(SqlConnection connection,
        SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT LastCompletedAt,MonitoringStartedAt
            FROM dbo.AiMentorMemoryRetentionState WHERE Id=1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("记忆保留期共享状态行不存在。");
        return new RetentionState(reader.IsDBNull(0) ? null : reader.GetDateTimeOffset(0),
            reader.GetDateTimeOffset(1));
    }

    private static async Task<DateTimeOffset> ReadDatabaseUtcNowAsync(SqlConnection connection,
        SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CONVERT(datetimeoffset(7),SYSUTCDATETIME());";
        return (DateTimeOffset)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("无法读取 SQL Server UTC 时间。"));
    }

    private sealed record RetentionState(DateTimeOffset? LastCompletedAt, DateTimeOffset MonitoringStartedAt);

    public void Dispose() => _initializationGate.Dispose();

    private async Task PruneTenantExpiredAsync(string tenantId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await PruneTenantExpiredAsync(connection, transaction, tenantId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    private async Task PruneTenantExpiredAsync(SqlConnection connection, SqlTransaction transaction,
        string tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var batchSize = _workflowOptions.RequestCleanupBatchSize;
        var proposalRemaining = batchSize;
        proposalRemaining -= await DeleteTenantBatchAsync(connection, transaction, ProposalTable, "Status<>0",
            tenantId, now, proposalRemaining, cancellationToken);
        proposalRemaining -= await DeleteTenantBatchAsync(connection, transaction, ProposalTable,
            "Status=0 AND ApprovalExpiresAt<=@now", tenantId, now, proposalRemaining, cancellationToken);
        await DeleteTenantBatchAsync(connection, transaction, ProposalTable, "MemoryExpiresAt<=@now", tenantId,
            now, proposalRemaining, cancellationToken);
        await DeleteTenantBatchAsync(connection, transaction, MemoryTable, "ExpiresAt<=@now", tenantId, now,
            batchSize, cancellationToken);
    }

    private static async Task<int> DeleteTenantBatchAsync(SqlConnection connection, SqlTransaction transaction,
        string tableName, string predicate, string tenantId, DateTimeOffset now, int batchSize,
        CancellationToken cancellationToken)
    {
        if (batchSize <= 0) return 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE TOP (@limit) FROM dbo.{tableName} " +
            $"WHERE TenantId=@tenantId AND {predicate};";
        AddInt(command, "@limit", batchSize);
        AddString(command, "@tenantId", 128, tenantId);
        AddDateTimeOffset(command, "@now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> DeleteBatchAsync(SqlConnection connection, SqlTransaction transaction,
        string tableName, string predicate, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize <= 0) return 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE TOP (@limit) FROM dbo.{tableName} WHERE {predicate};";
        AddInt(command, "@limit", batchSize);
        AddDateTimeOffset(command, "@now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertMemoryAsync(SqlConnection connection, SqlTransaction transaction, MemoryRecord memory,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO dbo.{MemoryTable}
                (Id,TenantId,SubjectId,Scope,SessionId,KeyVersion,KeyCipher,KeyFingerprint,ValueCipher,
                 Version,CreatedAt,UpdatedAt,ExpiresAt)
            VALUES
                (@id,@tenantId,@subjectId,@scope,@sessionId,@keyVersion,@keyCipher,@keyFingerprint,@valueCipher,
                 @version,@createdAt,@updatedAt,@expiresAt);
            """;
        AddString(command, "@id", 128, memory.Id);
        AddString(command, "@tenantId", 128, memory.TenantId);
        AddString(command, "@subjectId", 256, memory.SubjectId);
        AddByte(command, "@scope", (byte)memory.Scope);
        AddNullableString(command, "@sessionId", 128, memory.SessionId);
        AddString(command, "@keyVersion", 64, cipher.ActiveKeyVersion);
        AddString(command, "@keyCipher", -1, cipher.Protect(memory.Key, Context(memory, "key")));
        AddFingerprint(command, "@keyFingerprint", cipher.Fingerprint(memory.Key,
            FingerprintContext(memory.TenantId, memory.SubjectId, memory.Scope, memory.SessionId)));
        AddString(command, "@valueCipher", -1, cipher.Protect(memory.Value, Context(memory, "value")));
        AddInt(command, "@version", memory.Version);
        AddDateTimeOffset(command, "@createdAt", memory.CreatedAt);
        AddDateTimeOffset(command, "@updatedAt", memory.UpdatedAt);
        AddDateTimeOffset(command, "@expiresAt", memory.ExpiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<StoredProposal?> ReadProposalAsync(SqlConnection connection, SqlTransaction transaction,
        string proposalId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT Id,TenantId,SubjectId,Scope,SessionId,KeyVersion,KeyCipher,KeyFingerprint,ValueCipher,
                   CreatedAt,ApprovalExpiresAt,MemoryExpiresAt,Status
            FROM dbo.{ProposalTable} WITH (UPDLOCK,HOLDLOCK) WHERE Id=@id;
            """;
        AddString(command, "@id", 128, proposalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new StoredProposal(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            (MemoryScope)reader.GetByte(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetDateTimeOffset(9),
            reader.GetDateTimeOffset(10), reader.GetDateTimeOffset(11), (MemoryProposalStatus)reader.GetByte(12));
    }

    private static async Task<StoredMemory?> ReadMemoryAsync(SqlConnection connection, SqlTransaction? transaction,
        string memoryId, bool lockRow, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT Id,TenantId,SubjectId,Scope,SessionId,KeyVersion,KeyCipher,KeyFingerprint,ValueCipher,
                   Version,CreatedAt,UpdatedAt,ExpiresAt
            FROM dbo.{MemoryTable} {(lockRow ? "WITH (UPDLOCK,HOLDLOCK)" : string.Empty)} WHERE Id=@id;
            """;
        AddString(command, "@id", 128, memoryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMemory(reader) : null;
    }

    private static StoredMemory ReadMemory(SqlDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.GetString(2), (MemoryScope)reader.GetByte(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt32(9),
        reader.GetDateTimeOffset(10), reader.GetDateTimeOffset(11), reader.GetDateTimeOffset(12));

    private MemoryRecord ToMemoryRecord(StoredMemory memory) => new(memory.Id, memory.TenantId, memory.SubjectId,
        memory.Scope, memory.SessionId, cipher.Unprotect(memory.KeyCipher, Context(memory, "key")),
        cipher.Unprotect(memory.ValueCipher, Context(memory, "value")), memory.Version, memory.CreatedAt,
        memory.UpdatedAt, memory.ExpiresAt);

    private async Task<MemoryRecord> ToMemoryRecordAndRotateAsync(SqlConnection connection,
        SqlTransaction? transaction, StoredMemory memory, CancellationToken cancellationToken)
    {
        var record = ToMemoryRecord(memory);
        if (!cipher.RequiresReencryption(memory.KeyCipher)
            && !cipher.RequiresReencryption(memory.ValueCipher)) return record;

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"""
            UPDATE dbo.{MemoryTable}
            SET KeyVersion=@keyVersion,KeyCipher=@keyCipher,KeyFingerprint=@keyFingerprint,ValueCipher=@valueCipher
            WHERE Id=@id AND KeyCipher=@oldKeyCipher AND ValueCipher=@oldValueCipher;
            """;
        AddString(update, "@keyVersion", 64, cipher.ActiveKeyVersion);
        AddString(update, "@keyCipher", -1, cipher.Protect(record.Key, Context(record, "key")));
        AddFingerprint(update, "@keyFingerprint", cipher.Fingerprint(record.Key,
            FingerprintContext(record.TenantId, record.SubjectId, record.Scope, record.SessionId)));
        AddString(update, "@valueCipher", -1, cipher.Protect(record.Value, Context(record, "value")));
        AddString(update, "@id", 128, record.Id);
        AddString(update, "@oldKeyCipher", -1, memory.KeyCipher);
        AddString(update, "@oldValueCipher", -1, memory.ValueCipher);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return record;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
                throw new InvalidOperationException("SQL Server 记忆存储配置无效。");
            if (_workflowOptions.MaximumPendingProposalsPerTenant <= 0
                || _workflowOptions.RequestCleanupBatchSize is < 1 or > 10_000)
                throw new InvalidOperationException("SQL Server 记忆容量或请求清理批次配置无效。");
            if (options.InitializeSchema)
            {
                await using var connection = await OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = SchemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<int> DeleteProposalAsync(SqlConnection connection, SqlTransaction transaction,
        string proposalId, CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = $"DELETE FROM dbo.{ProposalTable} WHERE Id=@id;";
        AddString(delete, "@id", 128, proposalId);
        return await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> CommitAsync<T>(SqlTransaction transaction, T value,
        CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        return value;
    }

    private static async Task RollbackAsync(SqlTransaction transaction)
    {
        try
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // 连接中断或事务已完成时保留原始失败，避免回滚异常掩盖根因。
        }
    }

    private static string Context(MemoryProposal memory, string field) => Context(memory.TenantId,
        memory.SubjectId, memory.Scope, memory.SessionId, memory.Id, field);

    private static string Context(MemoryRecord memory, string field) => Context(memory.TenantId,
        memory.SubjectId, memory.Scope, memory.SessionId, memory.Id, field);

    private static string Context(StoredOwner memory, string field) => Context(memory.TenantId,
        memory.SubjectId, memory.Scope, memory.SessionId, memory.Id, field);

    private static string Context(string tenantId, string subjectId, MemoryScope scope, string? sessionId,
        string id, string field) => string.Join('\u001f', tenantId, subjectId, scope.ToString(),
        sessionId ?? string.Empty, id, field);

    private static string FingerprintContext(string tenantId, string subjectId, MemoryScope scope,
        string? sessionId) => string.Join('\u001f', tenantId, subjectId, scope.ToString(), sessionId ?? string.Empty);

    private static bool OwnedBy(string tenantId, string subjectId, AccessContext access) =>
        string.Equals(tenantId, access.TenantId, StringComparison.Ordinal)
        && string.Equals(subjectId, access.SubjectId, StringComparison.Ordinal);

    private static void AddString(SqlCommand command, string name, int size, string value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;

    private static void AddNullableString(SqlCommand command, string name, int size, string? value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = (object?)value ?? DBNull.Value;

    private static void AddFingerprint(SqlCommand command, string name, string value) =>
        command.Parameters.Add(name, SqlDbType.Char, 64).Value = value;

    private static void AddByte(SqlCommand command, string name, byte value) =>
        command.Parameters.Add(name, SqlDbType.TinyInt).Value = value;

    private static void AddNullableByte(SqlCommand command, string name, byte? value) =>
        command.Parameters.Add(name, SqlDbType.TinyInt).Value = (object?)value ?? DBNull.Value;

    private static void AddInt(SqlCommand command, string name, int value) =>
        command.Parameters.Add(name, SqlDbType.Int).Value = value;

    private static void AddDateTimeOffset(SqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value;

    private abstract record StoredOwner(string Id, string TenantId, string SubjectId, MemoryScope Scope,
        string? SessionId, string KeyVersion, string KeyCipher, string KeyFingerprint, string ValueCipher);

    private sealed record StoredProposal(string Id, string TenantId, string SubjectId, MemoryScope Scope,
        string? SessionId, string KeyVersion, string KeyCipher, string KeyFingerprint, string ValueCipher,
        DateTimeOffset CreatedAt,
        DateTimeOffset ApprovalExpiresAt, DateTimeOffset MemoryExpiresAt, MemoryProposalStatus Status)
        : StoredOwner(Id, TenantId, SubjectId, Scope, SessionId, KeyVersion, KeyCipher, KeyFingerprint, ValueCipher);

    private sealed record StoredMemory(string Id, string TenantId, string SubjectId, MemoryScope Scope,
        string? SessionId, string KeyVersion, string KeyCipher, string KeyFingerprint, string ValueCipher, int Version,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset ExpiresAt)
        : StoredOwner(Id, TenantId, SubjectId, Scope, SessionId, KeyVersion, KeyCipher, KeyFingerprint, ValueCipher);

    private const string ReadinessSql = """
        DECLARE @ready int=1;
        DECLARE @proposalId int=OBJECT_ID(N'dbo.AiMentorMemoryProposals',N'U');
        DECLARE @memoryId int=OBJECT_ID(N'dbo.AiMentorMemories',N'U');
        DECLARE @retentionId int=OBJECT_ID(N'dbo.AiMentorMemoryRetentionState',N'U');
        IF @proposalId IS NULL OR @memoryId IS NULL OR @retentionId IS NULL SET @ready=0;

        IF EXISTS (
            SELECT 1 FROM (VALUES
                (N'Id'),(N'TenantId'),(N'SubjectId'),(N'Scope'),(N'SessionId'),(N'KeyVersion'),(N'KeyCipher'),
                (N'KeyFingerprint'),(N'ValueCipher'),(N'CreatedAt'),(N'ApprovalExpiresAt'),
                (N'MemoryExpiresAt'),(N'Status'),(N'RowVersion')) required(Name)
            WHERE NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=@proposalId AND c.name=required.Name))
            SET @ready=0;
        IF EXISTS (
            SELECT 1 FROM (VALUES
                (N'Id'),(N'TenantId'),(N'SubjectId'),(N'Scope'),(N'SessionId'),(N'KeyVersion'),(N'KeyCipher'),
                (N'KeyFingerprint'),(N'ValueCipher'),(N'Version'),(N'CreatedAt'),(N'UpdatedAt'),
                (N'ExpiresAt'),(N'RowVersion')) required(Name)
            WHERE NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=@memoryId AND c.name=required.Name))
            SET @ready=0;
        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=@retentionId AND name=N'Id'
            AND system_type_id=48 AND is_nullable=0) SET @ready=0;
        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=@retentionId AND name=N'LastCompletedAt'
            AND system_type_id=43 AND scale=7 AND is_nullable=1) SET @ready=0;
        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=@retentionId AND name=N'MonitoringStartedAt'
            AND system_type_id=43 AND scale=7 AND is_nullable=0) SET @ready=0;
        IF NOT EXISTS (SELECT 1 FROM dbo.AiMentorMemoryRetentionState WHERE Id=1) SET @ready=0;

        IF EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id IN (@proposalId,@memoryId)
            AND c.name IN (N'Id',N'TenantId',N'SubjectId',N'SessionId',N'KeyVersion',N'KeyCipher',N'KeyFingerprint',N'ValueCipher')
            AND c.collation_name<>N'Latin1_General_100_BIN2') SET @ready=0;
        IF EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id IN (@proposalId,@memoryId)
            AND c.name=N'KeyVersion' AND c.is_nullable=1) SET @ready=0;

        IF EXISTS (SELECT 1 FROM (VALUES
                (@proposalId,N'IX_AiMentorMemoryProposals_Owner'),
                (@proposalId,N'IX_AiMentorMemoryProposals_TenantStatusApprovalExpiry'),
                (@proposalId,N'IX_AiMentorMemoryProposals_TenantMemoryExpiry'),
                (@proposalId,N'IX_AiMentorMemoryProposals_Status'),
                (@proposalId,N'IX_AiMentorMemoryProposals_ApprovalExpiry'),
                (@proposalId,N'IX_AiMentorMemoryProposals_MemoryExpiry'),
                (@proposalId,N'IX_AiMentorMemoryProposals_KeyVersion'),
                (@memoryId,N'IX_AiMentorMemories_OwnerKey'),
                (@memoryId,N'IX_AiMentorMemories_Expiry'),
                (@memoryId,N'IX_AiMentorMemories_TenantExpiry'),
                (@memoryId,N'IX_AiMentorMemories_KeyVersion')) required(ObjectId,Name)
            WHERE NOT EXISTS (SELECT 1 FROM sys.indexes i
                WHERE i.object_id=required.ObjectId AND i.name=required.Name AND i.is_disabled=0)) SET @ready=0;
        IF EXISTS (SELECT 1 FROM (VALUES
                (@proposalId,N'IX_AiMentorMemoryProposals_Owner',1,N'TenantId'),
                (@proposalId,N'IX_AiMentorMemoryProposals_Owner',2,N'SubjectId'),
                (@proposalId,N'IX_AiMentorMemoryProposals_Owner',3,N'Status'),
                (@proposalId,N'IX_AiMentorMemoryProposals_Owner',4,N'ApprovalExpiresAt'),
                (@proposalId,N'IX_AiMentorMemoryProposals_TenantStatusApprovalExpiry',1,N'TenantId'),
                (@proposalId,N'IX_AiMentorMemoryProposals_TenantStatusApprovalExpiry',2,N'Status'),
                (@proposalId,N'IX_AiMentorMemoryProposals_TenantStatusApprovalExpiry',3,N'ApprovalExpiresAt'),
                (@proposalId,N'IX_AiMentorMemoryProposals_TenantMemoryExpiry',1,N'TenantId'),
                (@proposalId,N'IX_AiMentorMemoryProposals_TenantMemoryExpiry',2,N'MemoryExpiresAt'),
                (@proposalId,N'IX_AiMentorMemoryProposals_Status',1,N'Status'),
                (@proposalId,N'IX_AiMentorMemoryProposals_ApprovalExpiry',1,N'ApprovalExpiresAt'),
                (@proposalId,N'IX_AiMentorMemoryProposals_MemoryExpiry',1,N'MemoryExpiresAt'),
                (@proposalId,N'IX_AiMentorMemoryProposals_KeyVersion',1,N'KeyVersion'),
                (@memoryId,N'IX_AiMentorMemories_OwnerKey',1,N'TenantId'),
                (@memoryId,N'IX_AiMentorMemories_OwnerKey',2,N'SubjectId'),
                (@memoryId,N'IX_AiMentorMemories_OwnerKey',3,N'Scope'),
                (@memoryId,N'IX_AiMentorMemories_OwnerKey',4,N'SessionId'),
                (@memoryId,N'IX_AiMentorMemories_OwnerKey',5,N'KeyFingerprint'),
                (@memoryId,N'IX_AiMentorMemories_OwnerKey',6,N'ExpiresAt'),
                (@memoryId,N'IX_AiMentorMemories_Expiry',1,N'ExpiresAt'),
                (@memoryId,N'IX_AiMentorMemories_TenantExpiry',1,N'TenantId'),
                (@memoryId,N'IX_AiMentorMemories_TenantExpiry',2,N'ExpiresAt'),
                (@memoryId,N'IX_AiMentorMemories_KeyVersion',1,N'KeyVersion'))
                required(ObjectId,IndexName,KeyOrdinal,ColumnName)
            WHERE NOT EXISTS (SELECT 1 FROM sys.indexes i
                INNER JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                INNER JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE i.object_id=required.ObjectId AND i.name=required.IndexName
                  AND ic.key_ordinal=required.KeyOrdinal AND c.name=required.ColumnName)) SET @ready=0;

        IF ISNULL(HAS_PERMS_BY_NAME(N'dbo.AiMentorMemoryProposals',N'OBJECT',N'SELECT'),0)<>1
            OR ISNULL(HAS_PERMS_BY_NAME(N'dbo.AiMentorMemoryProposals',N'OBJECT',N'INSERT'),0)<>1
            OR ISNULL(HAS_PERMS_BY_NAME(N'dbo.AiMentorMemoryProposals',N'OBJECT',N'UPDATE'),0)<>1
            OR ISNULL(HAS_PERMS_BY_NAME(N'dbo.AiMentorMemoryProposals',N'OBJECT',N'DELETE'),0)<>1
            OR ISNULL(HAS_PERMS_BY_NAME(N'dbo.AiMentorMemories',N'OBJECT',N'SELECT'),0)<>1
            OR ISNULL(HAS_PERMS_BY_NAME(N'dbo.AiMentorMemories',N'OBJECT',N'INSERT'),0)<>1
            OR ISNULL(HAS_PERMS_BY_NAME(N'dbo.AiMentorMemories',N'OBJECT',N'UPDATE'),0)<>1
            OR ISNULL(HAS_PERMS_BY_NAME(N'dbo.AiMentorMemories',N'OBJECT',N'DELETE'),0)<>1
            OR ISNULL(HAS_PERMS_BY_NAME(N'dbo.AiMentorMemoryRetentionState',N'OBJECT',N'SELECT'),0)<>1
            OR ISNULL(HAS_PERMS_BY_NAME(N'dbo.AiMentorMemoryRetentionState',N'OBJECT',N'UPDATE'),0)<>1 SET @ready=0;
        SELECT @ready;
        """;

    private const string CipherVersionsSql = """
        WITH Candidates AS (
            SELECT KeyVersion,TenantId,SubjectId,Scope,SessionId,Id,KeyCipher
            FROM dbo.AiMentorMemoryProposals
            UNION ALL
            SELECT KeyVersion,TenantId,SubjectId,Scope,SessionId,Id,KeyCipher
            FROM dbo.AiMentorMemories
        ), Ranked AS (
            SELECT *,ROW_NUMBER() OVER(PARTITION BY KeyVersion ORDER BY Id) AS RowNumber
            FROM Candidates
        )
        SELECT CASE WHEN KeyCipher LIKE N'v1.%' THEN N'v1' ELSE N'mem1' END,
               KeyVersion,TenantId,SubjectId,Scope,SessionId,Id,KeyCipher
        FROM Ranked WHERE RowNumber=1;
        """;

    private const string SchemaSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;
        DECLARE @lockResult int;
        EXEC @lockResult=sys.sp_getapplock @Resource=N'AiMentor.Workflow.Schema',
            @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000;
        IF @lockResult<0 THROW 51000,N'无法获取工作流架构锁。',1;
        IF OBJECT_ID(N'dbo.AiMentorMemoryProposals', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.AiMentorMemoryProposals(
                Id nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL
                    CONSTRAINT PK_AiMentorMemoryProposals PRIMARY KEY,
                TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                SubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Scope tinyint NOT NULL,
                SessionId nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL,
                KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                KeyCipher nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                KeyFingerprint char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ValueCipher nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CreatedAt datetimeoffset(7) NOT NULL,
                ApprovalExpiresAt datetimeoffset(7) NOT NULL,
                MemoryExpiresAt datetimeoffset(7) NOT NULL,
                Status tinyint NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT CK_AiMentorMemoryProposals_Scope CHECK (Scope BETWEEN 0 AND 2),
                CONSTRAINT CK_AiMentorMemoryProposals_Status CHECK (Status BETWEEN 0 AND 1));
            CREATE INDEX IX_AiMentorMemoryProposals_Owner
                ON dbo.AiMentorMemoryProposals(TenantId,SubjectId,Status,ApprovalExpiresAt);
        END;
        IF COL_LENGTH(N'dbo.AiMentorMemoryProposals',N'KeyVersion') IS NULL
            ALTER TABLE dbo.AiMentorMemoryProposals
                ADD KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL;
        UPDATE dbo.AiMentorMemoryProposals SET KeyVersion=CASE
            WHEN KeyCipher LIKE N'v1.%' THEN N'v1'
            WHEN KeyCipher LIKE N'mem1.%' AND CHARINDEX(N'.',KeyCipher,6)>6
                THEN CONVERT(nvarchar(64),SUBSTRING(KeyCipher,6,CHARINDEX(N'.',KeyCipher,6)-6))
            ELSE NULL END WHERE KeyVersion IS NULL;
        IF EXISTS (SELECT 1 FROM dbo.AiMentorMemoryProposals WHERE KeyVersion IS NULL)
            THROW 51000,N'记忆提案包含无法识别的历史密文版本。',1;
        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemoryProposals')
            AND name=N'KeyVersion' AND is_nullable=1)
            ALTER TABLE dbo.AiMentorMemoryProposals
                ALTER COLUMN KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemoryProposals')
            AND name=N'IX_AiMentorMemoryProposals_TenantStatusApprovalExpiry')
            CREATE INDEX IX_AiMentorMemoryProposals_TenantStatusApprovalExpiry
                ON dbo.AiMentorMemoryProposals(TenantId,Status,ApprovalExpiresAt);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemoryProposals')
            AND name=N'IX_AiMentorMemoryProposals_TenantMemoryExpiry')
            CREATE INDEX IX_AiMentorMemoryProposals_TenantMemoryExpiry
                ON dbo.AiMentorMemoryProposals(TenantId,MemoryExpiresAt);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemoryProposals')
            AND name=N'IX_AiMentorMemoryProposals_ApprovalExpiry')
            CREATE INDEX IX_AiMentorMemoryProposals_ApprovalExpiry
                ON dbo.AiMentorMemoryProposals(ApprovalExpiresAt) INCLUDE(Status);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemoryProposals')
            AND name=N'IX_AiMentorMemoryProposals_Status')
            CREATE INDEX IX_AiMentorMemoryProposals_Status ON dbo.AiMentorMemoryProposals(Status);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemoryProposals')
            AND name=N'IX_AiMentorMemoryProposals_MemoryExpiry')
            CREATE INDEX IX_AiMentorMemoryProposals_MemoryExpiry
                ON dbo.AiMentorMemoryProposals(MemoryExpiresAt);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemoryProposals')
            AND name=N'IX_AiMentorMemoryProposals_KeyVersion')
            CREATE INDEX IX_AiMentorMemoryProposals_KeyVersion
                ON dbo.AiMentorMemoryProposals(KeyVersion);
        IF OBJECT_ID(N'dbo.AiMentorMemories', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.AiMentorMemories(
                Id nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL
                    CONSTRAINT PK_AiMentorMemories PRIMARY KEY,
                TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                SubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Scope tinyint NOT NULL,
                SessionId nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL,
                KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                KeyCipher nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                KeyFingerprint char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ValueCipher nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Version int NOT NULL,
                CreatedAt datetimeoffset(7) NOT NULL,
                UpdatedAt datetimeoffset(7) NOT NULL,
                ExpiresAt datetimeoffset(7) NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT CK_AiMentorMemories_Scope CHECK (Scope BETWEEN 0 AND 2),
                CONSTRAINT CK_AiMentorMemories_Version CHECK (Version > 0));
            CREATE INDEX IX_AiMentorMemories_OwnerKey
                ON dbo.AiMentorMemories(TenantId,SubjectId,Scope,SessionId,KeyFingerprint,ExpiresAt);
            CREATE INDEX IX_AiMentorMemories_Expiry ON dbo.AiMentorMemories(ExpiresAt);
        END;
        IF COL_LENGTH(N'dbo.AiMentorMemories',N'KeyVersion') IS NULL
            ALTER TABLE dbo.AiMentorMemories
                ADD KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL;
        UPDATE dbo.AiMentorMemories SET KeyVersion=CASE
            WHEN KeyCipher LIKE N'v1.%' THEN N'v1'
            WHEN KeyCipher LIKE N'mem1.%' AND CHARINDEX(N'.',KeyCipher,6)>6
                THEN CONVERT(nvarchar(64),SUBSTRING(KeyCipher,6,CHARINDEX(N'.',KeyCipher,6)-6))
            ELSE NULL END WHERE KeyVersion IS NULL;
        IF EXISTS (SELECT 1 FROM dbo.AiMentorMemories WHERE KeyVersion IS NULL)
            THROW 51000,N'正式记忆包含无法识别的历史密文版本。',1;
        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemories')
            AND name=N'KeyVersion' AND is_nullable=1)
            ALTER TABLE dbo.AiMentorMemories
                ALTER COLUMN KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemories')
            AND name=N'IX_AiMentorMemories_TenantExpiry')
            CREATE INDEX IX_AiMentorMemories_TenantExpiry ON dbo.AiMentorMemories(TenantId,ExpiresAt);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemories')
            AND name=N'IX_AiMentorMemories_KeyVersion')
            CREATE INDEX IX_AiMentorMemories_KeyVersion ON dbo.AiMentorMemories(KeyVersion);
        IF OBJECT_ID(N'dbo.AiMentorMemoryRetentionState',N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.AiMentorMemoryRetentionState(
                Id tinyint NOT NULL CONSTRAINT PK_AiMentorMemoryRetentionState PRIMARY KEY,
                LastCompletedAt datetimeoffset(7) NULL,
                MonitoringStartedAt datetimeoffset(7) NOT NULL
                    CONSTRAINT DF_AiMentorMemoryRetentionState_MonitoringStartedAt DEFAULT SYSUTCDATETIME(),
                CONSTRAINT CK_AiMentorMemoryRetentionState_Singleton CHECK (Id=1));
        END;
        IF COL_LENGTH(N'dbo.AiMentorMemoryRetentionState',N'MonitoringStartedAt') IS NULL
            ALTER TABLE dbo.AiMentorMemoryRetentionState ADD MonitoringStartedAt datetimeoffset(7) NOT NULL
                CONSTRAINT DF_AiMentorMemoryRetentionState_MonitoringStartedAt DEFAULT SYSUTCDATETIME() WITH VALUES;
        IF NOT EXISTS (SELECT 1 FROM dbo.AiMentorMemoryRetentionState WITH (UPDLOCK,HOLDLOCK) WHERE Id=1)
            INSERT INTO dbo.AiMentorMemoryRetentionState(Id,LastCompletedAt) VALUES(1,NULL);
        COMMIT TRANSACTION;
        """;
}
