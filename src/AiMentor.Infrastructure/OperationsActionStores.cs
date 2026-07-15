using System.Data;
using System.Globalization;
using AiMentor.Application;
using Microsoft.Data.SqlClient;

namespace AiMentor.Infrastructure;

/// <summary>单进程开发实现；所有状态转换都在同一把锁内完成，语义与 SQL Store 对齐。</summary>
public sealed class InMemoryOperationsActionStore(TimeProvider? timeProvider = null,
    OperationsActionOptions? actionOptions = null) : IOperationsActionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OperationsActionRecord> _records = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly OperationsActionOptions _actionOptions = actionOptions ?? new OperationsActionOptions();

    public Task<OperationsActionCreateResult> CreateAsync(OperationsActionRecord record, int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Normalize(record.CreatedAt);
            var replay = _records.Values.FirstOrDefault(item => item.TenantId == record.TenantId
                && item.IdempotencyHash == record.IdempotencyHash);
            if (replay is not null)
                return Task.FromResult(new OperationsActionCreateResult(
                    replay.RequestFingerprint == record.RequestFingerprint
                        ? OperationsActionCreateStatus.Replay
                        : OperationsActionCreateStatus.IdempotencyConflict, replay));
            if (_records.Values.Any(item => item.TenantId == record.TenantId
                && item.TargetType == record.TargetType && item.TargetId == record.TargetId
                && item.Status is OperationsActionStatus.AwaitingReview or OperationsActionStatus.Executing
                    or OperationsActionStatus.OutcomeUnknown or OperationsActionStatus.OutcomeUnknownArchived))
                return Task.FromResult(new OperationsActionCreateResult(OperationsActionCreateStatus.TargetBusy, null));
            if (_records.Values.Count(item => item.TenantId == record.TenantId
                    && item.Status is OperationsActionStatus.AwaitingReview or OperationsActionStatus.Executing)
                >= maximumEntries)
                return Task.FromResult(new OperationsActionCreateResult(OperationsActionCreateStatus.Capacity, null));
            if (_records.Values.Count(item => item.TenantId == record.TenantId)
                >= _actionOptions.MaximumAuditEntriesPerTenant)
                return Task.FromResult(new OperationsActionCreateResult(OperationsActionCreateStatus.Capacity, null));
            _records.Add(record.Id, record);
            return Task.FromResult(new OperationsActionCreateResult(OperationsActionCreateStatus.Created, record));
        }
    }

    public Task<IReadOnlyList<OperationsActionRecord>> ListAsync(string tenantId, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Normalize(_timeProvider.GetUtcNow());
            IReadOnlyList<OperationsActionRecord> result = _records.Values
                .Where(item => item.TenantId == tenantId)
                .OrderByDescending(item => item.CreatedAt).Take(limit).ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<OperationsActionRecord>> ListTaskStateAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Normalize(_timeProvider.GetUtcNow());
            IReadOnlyList<OperationsActionRecord> result = _records.Values.Where(item => item.TenantId == tenantId
                    && (item.Status is OperationsActionStatus.AwaitingReview or OperationsActionStatus.Executing
                        or OperationsActionStatus.OutcomeUnknown
                        or OperationsActionStatus.OutcomeUnknownArchived
                        || item.Action == "escalate" && item.Status == OperationsActionStatus.Completed))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<OperationsActionRecord?> GetAsync(string tenantId, string id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Normalize(_timeProvider.GetUtcNow());
            return Task.FromResult(_records.TryGetValue(id, out var record) && record.TenantId == tenantId
                ? record : null);
        }
    }

    public Task<OperationsActionRecord?> GetByIdempotencyAsync(string tenantId, string idempotencyHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Normalize(_timeProvider.GetUtcNow());
            return Task.FromResult(_records.Values.FirstOrDefault(item => item.TenantId == tenantId
                && item.IdempotencyHash == idempotencyHash));
        }
    }

    public Task<OperationsActionAcquireResult> TryAcquireReviewAsync(string tenantId, string id,
        long expectedVersion, string reviewerSubjectId, bool approved, string reviewReasonHash, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Normalize(now);
            if (!_records.TryGetValue(id, out var current) || current.TenantId != tenantId)
                return Task.FromResult(new OperationsActionAcquireResult(OperationsActionAcquireStatus.NotFound, null));
            if (current.Status != OperationsActionStatus.AwaitingReview)
            {
                if (current.Status == OperationsActionStatus.Expired)
                    return Task.FromResult(new OperationsActionAcquireResult(
                        OperationsActionAcquireStatus.Expired, current));
                var replay = current.ReviewerSubjectId == reviewerSubjectId
                    && current.Status is OperationsActionStatus.Executing or OperationsActionStatus.Completed
                        or OperationsActionStatus.Rejected or OperationsActionStatus.Failed
                        or OperationsActionStatus.OutcomeUnknown or OperationsActionStatus.OutcomeUnknownArchived;
                return Task.FromResult(new OperationsActionAcquireResult(
                    replay ? OperationsActionAcquireStatus.Replay : OperationsActionAcquireStatus.VersionConflict,
                    current));
            }
            if (current.ExpiresAt <= now)
                return Task.FromResult(new OperationsActionAcquireResult(OperationsActionAcquireStatus.Expired,
                    _records[id]));
            if (current.Version != expectedVersion)
                return Task.FromResult(new OperationsActionAcquireResult(OperationsActionAcquireStatus.VersionConflict,
                    current));
            if (current.RequesterSubjectId == reviewerSubjectId)
                return Task.FromResult(new OperationsActionAcquireResult(
                    OperationsActionAcquireStatus.ReviewerMustDiffer, current));
            var next = current with
            {
                Status = approved ? OperationsActionStatus.Executing : OperationsActionStatus.Rejected,
                Version = current.Version + 1,
                ReviewerSubjectId = reviewerSubjectId,
                ReviewReasonHash = reviewReasonHash,
                ReviewedAt = now,
                CompletedAt = approved ? null : now,
                OutcomeCode = approved ? null : "OPERATIONS_ACTION_REVIEW_REJECTED"
            };
            _records[id] = next;
            return Task.FromResult(new OperationsActionAcquireResult(
                approved ? OperationsActionAcquireStatus.Acquired : OperationsActionAcquireStatus.Rejected, next));
        }
    }

    public Task<OperationsActionRecord> CompleteAsync(string tenantId, string id, long expectedVersion,
        OperationsActionStatus status, string outcomeCode, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var current) || current.TenantId != tenantId)
                throw new OperationsActionException("OPERATIONS_ACTION_NOT_FOUND", "没有找到运营动作请求。",
                    OperationsActionErrorKind.NotFound);
            if (current.Status != OperationsActionStatus.Executing || current.Version != expectedVersion)
                throw new OperationsActionException("OPERATIONS_ACTION_VERSION_CONFLICT", "运营动作状态已经变化。",
                    OperationsActionErrorKind.Conflict);
            var completed = current with
            {
                Status = status,
                Version = current.Version + 1,
                CompletedAt = now,
                OutcomeCode = outcomeCode
            };
            _records[id] = completed;
            return Task.FromResult(completed);
        }
    }

    private void Normalize(DateTimeOffset now)
    {
        foreach (var pair in _records.ToArray())
            if (pair.Value.Status is OperationsActionStatus.AwaitingReview or OperationsActionStatus.Executing
                && pair.Value.ExpiresAt <= now)
            {
                var executing = pair.Value.Status == OperationsActionStatus.Executing;
                _records[pair.Key] = pair.Value with
                {
                    Status = executing ? OperationsActionStatus.OutcomeUnknown : OperationsActionStatus.Expired,
                    Version = pair.Value.Version + 1,
                    CompletedAt = now,
                    OutcomeCode = executing ? "OPERATIONS_ACTION_EXECUTION_EXPIRED_OUTCOME_UNKNOWN"
                        : "OPERATIONS_ACTION_REVIEW_EXPIRED"
                };
            }
            else if (pair.Value.Status == OperationsActionStatus.OutcomeUnknown
                && (pair.Value.CompletedAt ?? pair.Value.ExpiresAt) <= now - _actionOptions.OutcomeUnknownRetention)
                _records[pair.Key] = pair.Value with
                {
                    Status = OperationsActionStatus.OutcomeUnknownArchived,
                    Version = pair.Value.Version + 1
                };
    }
}

/// <summary>跨实例持久审计 Store；可串行化事务保证幂等键、目标占位和第二复核都只有一个胜者。</summary>
public sealed class SqlServerOperationsActionStore(SqlServerWorkflowOptions options, TimeProvider timeProvider,
    OperationsActionOptions? actionOptions = null)
    : IOperationsActionStore, IDisposable
{
    private const string TableName = "AiMentorOperationsActions";
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly OperationsActionOptions _actionOptions = actionOptions ?? new OperationsActionOptions();
    private volatile bool _initialized;

    public async Task<OperationsActionCreateResult> CreateAsync(OperationsActionRecord record, int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await NormalizeAsync(connection, transaction, record.CreatedAt, cancellationToken);

        var replay = await FindByIdempotencyAsync(connection, transaction, record.TenantId, record.IdempotencyHash,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(replay.RequestFingerprint == record.RequestFingerprint
                ? OperationsActionCreateStatus.Replay : OperationsActionCreateStatus.IdempotencyConflict, replay);
        }

        await using (var active = connection.CreateCommand())
        {
            active.Transaction = transaction;
            active.CommandText = $"SELECT COUNT_BIG(1) FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE TenantId=@tenant AND TargetType=@type AND TargetId=@target AND Status IN (0,1,5,7);";
            AddString(active, "@tenant", 128, record.TenantId);
            AddString(active, "@type", 32, record.TargetType);
            AddString(active, "@target", 128, record.TargetId);
            if (Convert.ToInt64(await active.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(OperationsActionCreateStatus.TargetBusy, null);
            }
        }
        await using (var capacity = connection.CreateCommand())
        {
            capacity.Transaction = transaction;
            capacity.CommandText = $"SELECT COUNT_BIG(1) FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE TenantId=@tenant AND Status IN (0,1);";
            AddString(capacity, "@tenant", 128, record.TenantId);
            if (Convert.ToInt64(await capacity.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
                >= maximumEntries)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(OperationsActionCreateStatus.Capacity, null);
            }
        }
        await using (var auditCapacity = connection.CreateCommand())
        {
            auditCapacity.Transaction = transaction;
            auditCapacity.CommandText = $"SELECT COUNT_BIG(1) FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE TenantId=@tenant;";
            AddString(auditCapacity, "@tenant", 128, record.TenantId);
            if (Convert.ToInt64(await auditCapacity.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) >= _actionOptions.MaximumAuditEntriesPerTenant)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(OperationsActionCreateStatus.Capacity, null);
            }
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT dbo.{TableName}
                (Id,TenantId,TargetType,TargetId,Action,TargetETag,RequestFingerprint,IdempotencyHash,
                 RequesterSubjectId,RequestReasonHash,Status,Version,CreatedAt,ExpiresAt)
                VALUES(@id,@tenant,@type,@target,@action,@etag,@fingerprint,@idempotency,@requester,@reason,
                       0,1,@created,@expires);
                """;
            AddRecordParameters(insert, record);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(OperationsActionCreateStatus.Created, record);
    }

    public async Task<IReadOnlyList<OperationsActionRecord>> ListAsync(string tenantId, int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await NormalizeAsync(connection, transaction, timeProvider.GetUtcNow(), cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT TOP (@limit) {Columns} FROM dbo.{TableName} " +
            "WHERE TenantId=@tenant ORDER BY CreatedAt DESC,Id DESC;";
        command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = limit });
        AddString(command, "@tenant", 128, tenantId);
        var rows = new List<OperationsActionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<IReadOnlyList<OperationsActionRecord>> ListTaskStateAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await NormalizeAsync(connection, transaction, timeProvider.GetUtcNow(), cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM dbo.{TableName} WHERE TenantId=@tenant " +
            "AND (Status IN (0,1,5,7) OR (Action=N'escalate' AND Status=2)) ORDER BY CreatedAt DESC,Id DESC;";
        AddString(command, "@tenant", 128, tenantId);
        var rows = new List<OperationsActionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<OperationsActionRecord?> GetAsync(string tenantId, string id,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await NormalizeAsync(connection, transaction, timeProvider.GetUtcNow(), cancellationToken);
        var record = await FindAsync(connection, transaction, tenantId, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<OperationsActionRecord?> GetByIdempotencyAsync(string tenantId, string idempotencyHash,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await NormalizeAsync(connection, transaction, timeProvider.GetUtcNow(), cancellationToken);
        var record = await FindByIdempotencyAsync(connection, transaction, tenantId, idempotencyHash,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<OperationsActionAcquireResult> TryAcquireReviewAsync(string tenantId, string id,
        long expectedVersion, string reviewerSubjectId, bool approved, string reviewReasonHash, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await NormalizeAsync(connection, transaction, now, cancellationToken);
        var current = await FindAsync(connection, transaction, tenantId, id, cancellationToken, true);
        if (current is null) return await Finish(OperationsActionAcquireStatus.NotFound, null);
        if (current.Status != OperationsActionStatus.AwaitingReview)
        {
            if (current.Status == OperationsActionStatus.Expired)
                return await Finish(OperationsActionAcquireStatus.Expired, current);
            var replay = current.ReviewerSubjectId == reviewerSubjectId
                && current.Status is OperationsActionStatus.Executing or OperationsActionStatus.Completed
                    or OperationsActionStatus.Rejected or OperationsActionStatus.Failed
                    or OperationsActionStatus.OutcomeUnknown or OperationsActionStatus.OutcomeUnknownArchived;
            return await Finish(replay ? OperationsActionAcquireStatus.Replay
                : OperationsActionAcquireStatus.VersionConflict, current);
        }
        if (current.ExpiresAt <= now) return await Finish(OperationsActionAcquireStatus.Expired, current);
        if (current.Version != expectedVersion)
            return await Finish(OperationsActionAcquireStatus.VersionConflict, current);
        if (current.RequesterSubjectId == reviewerSubjectId)
            return await Finish(OperationsActionAcquireStatus.ReviewerMustDiffer, current);

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=@status,Version=Version+1,ReviewerSubjectId=@reviewer,
                ReviewReasonHash=@reason,ReviewedAt=@now,CompletedAt=@completed,OutcomeCode=@code
            WHERE Id=@id AND TenantId=@tenant AND Status=0 AND Version=@version;
            """;
        update.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt)
        { Value = (byte)(approved ? OperationsActionStatus.Executing : OperationsActionStatus.Rejected) });
        AddString(update, "@reviewer", 256, reviewerSubjectId);
        AddString(update, "@reason", 64, reviewReasonHash);
        AddDate(update, "@now", now);
        update.Parameters.Add(new SqlParameter("@completed", SqlDbType.DateTimeOffset)
        { Value = approved ? DBNull.Value : now });
        update.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 128)
        { Value = approved ? DBNull.Value : "OPERATIONS_ACTION_REVIEW_REJECTED" });
        AddString(update, "@id", 64, id); AddString(update, "@tenant", 128, tenantId);
        update.Parameters.Add(new SqlParameter("@version", SqlDbType.BigInt) { Value = expectedVersion });
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            return await Finish(OperationsActionAcquireStatus.VersionConflict, current);
        var next = await FindAsync(connection, transaction, tenantId, id, cancellationToken, true);
        return await Finish(approved ? OperationsActionAcquireStatus.Acquired
            : OperationsActionAcquireStatus.Rejected, next);

        async Task<OperationsActionAcquireResult> Finish(OperationsActionAcquireStatus status,
            OperationsActionRecord? record)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(status, record);
        }
    }

    public async Task<OperationsActionRecord> CompleteAsync(string tenantId, string id, long expectedVersion,
        OperationsActionStatus status, string outcomeCode, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=@status,Version=Version+1,CompletedAt=@now,OutcomeCode=@code
            WHERE Id=@id AND TenantId=@tenant AND Status=1 AND Version=@version;
            """;
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)status });
        AddDate(command, "@now", now); AddString(command, "@code", 128, outcomeCode);
        AddString(command, "@id", 64, id); AddString(command, "@tenant", 128, tenantId);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.BigInt) { Value = expectedVersion });
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new OperationsActionException("OPERATIONS_ACTION_VERSION_CONFLICT", "运营动作状态已经变化。",
                OperationsActionErrorKind.Conflict);
        await using var read = connection.CreateCommand();
        read.CommandText = $"SELECT {Columns} FROM dbo.{TableName} WHERE Id=@id AND TenantId=@tenant;";
        AddString(read, "@id", 64, id); AddString(read, "@tenant", 128, tenantId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new OperationsActionException("OPERATIONS_ACTION_NOT_FOUND", "没有找到运营动作请求。",
                OperationsActionErrorKind.NotFound);
        return Read(reader);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            if (options.InitializeSchema)
            {
                await using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = SchemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            _initialized = true;
        }
        finally { _initializationGate.Release(); }
    }

    private async Task NormalizeAsync(SqlConnection connection, SqlTransaction transaction, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE dbo.{TableName} SET " +
            "Status=CASE WHEN Status=0 THEN 6 ELSE 5 END,Version=Version+1,CompletedAt=@now," +
            "OutcomeCode=CASE WHEN Status=0 THEN N'OPERATIONS_ACTION_REVIEW_EXPIRED' " +
            "ELSE N'OPERATIONS_ACTION_EXECUTION_EXPIRED_OUTCOME_UNKNOWN' END " +
            "WHERE Status IN (0,1) AND ExpiresAt<=@now; " +
            $"UPDATE dbo.{TableName} SET Status=7,Version=Version+1 " +
            "WHERE Status=5 AND COALESCE(CompletedAt,ExpiresAt)<=@archiveBefore;";
        AddDate(command, "@now", now);
        AddDate(command, "@archiveBefore", now - _actionOptions.OutcomeUnknownRetention);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<OperationsActionRecord?> FindByIdempotencyAsync(SqlConnection connection,
        SqlTransaction transaction, string tenantId, string hash, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE TenantId=@tenant AND IdempotencyHash=@hash;";
        AddString(command, "@tenant", 128, tenantId); AddString(command, "@hash", 64, hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static async Task<OperationsActionRecord?> FindAsync(SqlConnection connection, SqlTransaction transaction,
        string tenantId, string id, CancellationToken cancellationToken, bool lockRow = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM dbo.{TableName} " +
            (lockRow ? "WITH (UPDLOCK,HOLDLOCK) " : string.Empty) + "WHERE TenantId=@tenant AND Id=@id;";
        AddString(command, "@tenant", 128, tenantId); AddString(command, "@id", 64, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static OperationsActionRecord Read(SqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
        (OperationsActionStatus)reader.GetByte(10), reader.GetInt64(11), reader.GetDateTimeOffset(12),
        reader.GetDateTimeOffset(13), reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetDateTimeOffset(16),
        reader.IsDBNull(17) ? null : reader.GetDateTimeOffset(17), reader.IsDBNull(18) ? null : reader.GetString(18));

    private static void AddRecordParameters(SqlCommand command, OperationsActionRecord record)
    {
        AddString(command, "@id", 64, record.Id); AddString(command, "@tenant", 128, record.TenantId);
        AddString(command, "@type", 32, record.TargetType); AddString(command, "@target", 128, record.TargetId);
        AddString(command, "@action", 32, record.Action); AddString(command, "@etag", 80, record.TargetETag);
        AddString(command, "@fingerprint", 64, record.RequestFingerprint);
        AddString(command, "@idempotency", 64, record.IdempotencyHash);
        AddString(command, "@requester", 256, record.RequesterSubjectId);
        AddString(command, "@reason", 64, record.RequestReasonHash);
        AddDate(command, "@created", record.CreatedAt); AddDate(command, "@expires", record.ExpiresAt);
    }

    private static void AddString(SqlCommand command, string name, int size, string value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, size) { Value = value });
    private static void AddDate(SqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.DateTimeOffset) { Value = value });

    private const string Columns = "Id,TenantId,TargetType,TargetId,Action,TargetETag,RequestFingerprint," +
        "IdempotencyHash,RequesterSubjectId,RequestReasonHash,Status,Version,CreatedAt,ExpiresAt," +
        "ReviewerSubjectId,ReviewReasonHash,ReviewedAt,CompletedAt,OutcomeCode";

    private const string SchemaSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;
        DECLARE @lockResult int;
        EXEC @lockResult=sys.sp_getapplock @Resource=N'AiMentor.Workflow.Schema',@LockMode='Exclusive',
            @LockOwner='Transaction',@LockTimeout=30000;
        IF @lockResult < 0 THROW 51000,N'无法获取工作流建表锁。',1;
        IF OBJECT_ID(N'dbo.AiMentorOperationsActions',N'U') IS NULL
        BEGIN
          CREATE TABLE dbo.AiMentorOperationsActions(
            Id nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL
              CONSTRAINT PK_AiMentorOperationsActions PRIMARY KEY,
            TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            TargetType nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            TargetId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Action nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            TargetETag nvarchar(80) COLLATE Latin1_General_100_BIN2 NOT NULL,
            RequestFingerprint char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            IdempotencyHash char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            RequesterSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            RequestReasonHash char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Status tinyint NOT NULL,Version bigint NOT NULL,
            CreatedAt datetimeoffset(7) NOT NULL,ExpiresAt datetimeoffset(7) NOT NULL,
            ReviewerSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
            ReviewReasonHash char(64) COLLATE Latin1_General_100_BIN2 NULL,
            ReviewedAt datetimeoffset(7) NULL,CompletedAt datetimeoffset(7) NULL,
            OutcomeCode nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL,RowVersion rowversion NOT NULL);
          CREATE UNIQUE INDEX UX_AiMentorOperationsActions_Idempotency
            ON dbo.AiMentorOperationsActions(TenantId,IdempotencyHash);
          CREATE INDEX IX_AiMentorOperationsActions_Target
            ON dbo.AiMentorOperationsActions(TenantId,TargetType,TargetId,Status);
          CREATE INDEX IX_AiMentorOperationsActions_Queue
            ON dbo.AiMentorOperationsActions(TenantId,Status,ExpiresAt);
        END;

        IF EXISTS (
          SELECT 1 FROM sys.columns
          WHERE object_id=OBJECT_ID(N'dbo.AiMentorOperationsActions')
            AND name IN (N'Id',N'TenantId',N'TargetType',N'TargetId',N'Action',N'TargetETag',
              N'RequestFingerprint',N'IdempotencyHash',N'RequesterSubjectId',N'RequestReasonHash',
              N'ReviewerSubjectId',N'ReviewReasonHash',N'OutcomeCode')
            AND collation_name<>N'Latin1_General_100_BIN2')
        BEGIN
          DECLARE @rowCount bigint;
          SELECT @rowCount=COUNT_BIG(1) FROM dbo.AiMentorOperationsActions WITH (TABLOCKX,HOLDLOCK);
          -- 身份别名无法自动归并；为避免把既有审计记录分配给错误租户，升级必须失败关闭。
          IF EXISTS (
            SELECT 1 FROM dbo.AiMentorOperationsActions
            GROUP BY TenantId
            HAVING COUNT(DISTINCT TenantId COLLATE Latin1_General_100_BIN2)>1)
            THROW 51011,N'检测到仅大小写或排序差异的多个租户标识；请先核实归属并清理别名。',1;
          IF EXISTS (
            SELECT Id COLLATE Latin1_General_100_BIN2 FROM dbo.AiMentorOperationsActions
            GROUP BY Id COLLATE Latin1_General_100_BIN2 HAVING COUNT_BIG(1)>1)
            THROW 51012,N'检测到完全相同的运营动作标识，无法重建主键。',1;
          IF EXISTS (
            SELECT TenantId COLLATE Latin1_General_100_BIN2,
              IdempotencyHash COLLATE Latin1_General_100_BIN2
            FROM dbo.AiMentorOperationsActions
            GROUP BY TenantId COLLATE Latin1_General_100_BIN2,
              IdempotencyHash COLLATE Latin1_General_100_BIN2 HAVING COUNT_BIG(1)>1)
            THROW 51013,N'检测到完全相同的租户幂等键，无法重建唯一索引。',1;

          DROP INDEX IF EXISTS UX_AiMentorOperationsActions_Idempotency
            ON dbo.AiMentorOperationsActions;
          DROP INDEX IF EXISTS IX_AiMentorOperationsActions_Target ON dbo.AiMentorOperationsActions;
          DROP INDEX IF EXISTS IX_AiMentorOperationsActions_Queue ON dbo.AiMentorOperationsActions;
          IF EXISTS (SELECT 1 FROM sys.key_constraints
            WHERE parent_object_id=OBJECT_ID(N'dbo.AiMentorOperationsActions')
              AND name=N'PK_AiMentorOperationsActions')
            ALTER TABLE dbo.AiMentorOperationsActions DROP CONSTRAINT PK_AiMentorOperationsActions;

          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN Id nvarchar(64)
            COLLATE Latin1_General_100_BIN2 NOT NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN TenantId nvarchar(128)
            COLLATE Latin1_General_100_BIN2 NOT NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN TargetType nvarchar(32)
            COLLATE Latin1_General_100_BIN2 NOT NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN TargetId nvarchar(128)
            COLLATE Latin1_General_100_BIN2 NOT NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN Action nvarchar(32)
            COLLATE Latin1_General_100_BIN2 NOT NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN TargetETag nvarchar(80)
            COLLATE Latin1_General_100_BIN2 NOT NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN RequestFingerprint char(64)
            COLLATE Latin1_General_100_BIN2 NOT NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN IdempotencyHash char(64)
            COLLATE Latin1_General_100_BIN2 NOT NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN RequesterSubjectId nvarchar(256)
            COLLATE Latin1_General_100_BIN2 NOT NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN RequestReasonHash char(64)
            COLLATE Latin1_General_100_BIN2 NOT NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN ReviewerSubjectId nvarchar(256)
            COLLATE Latin1_General_100_BIN2 NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN ReviewReasonHash char(64)
            COLLATE Latin1_General_100_BIN2 NULL;
          ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN OutcomeCode nvarchar(128)
            COLLATE Latin1_General_100_BIN2 NULL;

          ALTER TABLE dbo.AiMentorOperationsActions
            ADD CONSTRAINT PK_AiMentorOperationsActions PRIMARY KEY(Id);
          CREATE UNIQUE INDEX UX_AiMentorOperationsActions_Idempotency
            ON dbo.AiMentorOperationsActions(TenantId,IdempotencyHash);
          CREATE INDEX IX_AiMentorOperationsActions_Target
            ON dbo.AiMentorOperationsActions(TenantId,TargetType,TargetId,Status);
          CREATE INDEX IX_AiMentorOperationsActions_Queue
            ON dbo.AiMentorOperationsActions(TenantId,Status,ExpiresAt);
        END;
        COMMIT TRANSACTION;
        """;

    public void Dispose() => _initializationGate.Dispose();
}
