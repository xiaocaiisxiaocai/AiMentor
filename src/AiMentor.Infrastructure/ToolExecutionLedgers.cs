using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Text.Json;

namespace AiMentor.Infrastructure;

/// <summary>提供与 SQL 账本相同状态机的进程内实现，用于开发和快速回归测试。</summary>
public sealed class InMemoryToolExecutionLedger(TimeProvider timeProvider) : IToolExecutionLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IdempotencyAcquireResult> TryAcquireAsync(ToolExecutionLedgerRequest request,
        TimeSpan leaseDuration, TimeSpan retention, int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var stale in _entries.Where(pair => pair.Value.Status == LedgerStatus.Completed
                    && pair.Value.UpdatedAt.Add(retention) <= now).ToArray())
                _entries.Remove(stale.Key);
            if (!_entries.TryGetValue(request.ExecutionKey, out var entry))
            {
                if (_entries.Count >= maximumEntries)
                    return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.Capacity));
                var token = Guid.NewGuid().ToString("N");
                _entries.Add(request.ExecutionKey, new Entry(request.RequestFingerprint, request.RunId,
                    request.TenantId, request.SubjectId, request.ToolName, LedgerStatus.Reserved, token,
                    now.Add(leaseDuration), now, now));
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired, token));
            }
            if (!string.Equals(entry.RequestFingerprint, request.RequestFingerprint, StringComparison.Ordinal))
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.FingerprintMismatch));
            if (entry.Status == LedgerStatus.Completed)
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.Replay,
                    ReplayResult: entry.Result));
            if (entry.Status == LedgerStatus.OutcomeUnknown)
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.OutcomeUnknown));
            if (entry.LeaseExpiresAt > now)
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.InProgress));
            if (entry.Status == LedgerStatus.Executing)
            {
                _entries[request.ExecutionKey] = entry with { Status = LedgerStatus.OutcomeUnknown, UpdatedAt = now };
                return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.OutcomeUnknown));
            }
            var renewedToken = Guid.NewGuid().ToString("N");
            _entries[request.ExecutionKey] = entry with
            {
                RunId = request.RunId,
                LeaseToken = renewedToken,
                LeaseExpiresAt = now.Add(leaseDuration),
                UpdatedAt = now
            };
            return Task.FromResult(new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired, renewedToken));
        }
    }

    /// <inheritdoc />
    public Task MarkExecutingAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(executionKey, leaseToken, LedgerStatus.Reserved, LedgerStatus.Executing, null, cancellationToken);

    /// <inheritdoc />
    public Task CompleteAsync(string executionKey, string leaseToken, ToolExecutionResult result,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(executionKey, leaseToken, LedgerStatus.Executing, LedgerStatus.Completed, result, cancellationToken);

    /// <inheritdoc />
    public Task MarkOutcomeUnknownAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(executionKey, leaseToken, LedgerStatus.Executing, LedgerStatus.OutcomeUnknown, null,
            cancellationToken);

    /// <inheritdoc />
    public Task AbandonAsync(string executionKey, string leaseToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_entries.TryGetValue(executionKey, out var entry) && entry.Status == LedgerStatus.Reserved
                && FixedEquals(entry.LeaseToken, leaseToken))
                _entries.Remove(executionKey);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(string tenantId, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_gate)
        {
            IReadOnlyList<OutcomeUnknownToolExecution> result = _entries
                .Where(pair => pair.Value.Status == LedgerStatus.OutcomeUnknown
                    && string.Equals(pair.Value.TenantId, tenantId, StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Value.UpdatedAt)
                .Take(limit)
                .Select(pair => new OutcomeUnknownToolExecution(pair.Key, pair.Value.TenantId,
                    pair.Value.SubjectId, pair.Value.ToolName, pair.Value.RunId,
                    pair.Value.CreatedAt, pair.Value.UpdatedAt))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    private Task TransitionAsync(string executionKey, string leaseToken, LedgerStatus expected, LedgerStatus next,
        ToolExecutionResult? result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_entries.TryGetValue(executionKey, out var entry) || entry.Status != expected
                || !FixedEquals(entry.LeaseToken, leaseToken))
                throw new InvalidOperationException("幂等执行账本状态或租约不匹配。");
            _entries[executionKey] = entry with { Status = next, Result = result, UpdatedAt = timeProvider.GetUtcNow() };
        }
        return Task.CompletedTask;
    }

    private static bool FixedEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));

    private static void ValidateRequest(ToolExecutionLedgerRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ToolName);
    }

    private enum LedgerStatus { Reserved, Executing, Completed, OutcomeUnknown }

    private sealed record Entry(string RequestFingerprint, string RunId, string TenantId, string SubjectId,
        string ToolName, LedgerStatus Status, string LeaseToken, DateTimeOffset LeaseExpiresAt,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ToolExecutionResult? Result = null);
}

/// <summary>
/// 使用 SQL Server 条件事务持久化幂等占位和加密结果；执行租约过期后把已开始副作用的记录冻结为结果不确定。
/// </summary>
public sealed class SqlServerToolExecutionLedger(
    SqlServerWorkflowOptions options,
    IWorkflowStateCipher cipher,
    TimeProvider timeProvider) : IToolExecutionLedger, IDisposable
{
    private const string TableName = "AiMentorToolExecutions";
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    /// <inheritdoc />
    public async Task<IdempotencyAcquireResult> TryAcquireAsync(ToolExecutionLedgerRequest request,
        TimeSpan leaseDuration, TimeSpan retention, int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await EnsureInitializedAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = $"DELETE FROM dbo.{TableName} WHERE Status=2 AND UpdatedAt<=@cutoff;";
            AddDateTimeOffset(prune, "@cutoff", now.Subtract(retention));
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        var entry = await ReadForUpdateAsync(connection, transaction, request.ExecutionKey, cancellationToken);
        if (entry is null)
        {
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = $"SELECT COUNT_BIG(1) FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK);";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
                >= maximumEntries)
                return await CommitAsync(transaction, new IdempotencyAcquireResult(IdempotencyAcquireStatus.Capacity),
                    cancellationToken);
            var token = Guid.NewGuid().ToString("N");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT dbo.{TableName}
                  (ExecutionKey,RequestFingerprint,RunId,TenantId,SubjectId,ToolName,Status,LeaseToken,LeaseExpiresAt,CreatedAt,UpdatedAt)
                VALUES (@key,@fingerprint,@runId,@tenantId,@subjectId,@toolName,0,@token,@leaseExpires,@now,@now);
                """;
            AddAnsiString(insert, "@key", 64, request.ExecutionKey);
            AddAnsiString(insert, "@fingerprint", 64, request.RequestFingerprint);
            AddString(insert, "@runId", 128, request.RunId); AddString(insert, "@tenantId", 128, request.TenantId);
            AddString(insert, "@subjectId", 256, request.SubjectId); AddString(insert, "@toolName", 128, request.ToolName);
            AddString(insert, "@token", 64, token);
            AddDateTimeOffset(insert, "@leaseExpires", now.Add(leaseDuration)); AddDateTimeOffset(insert, "@now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired, token), cancellationToken);
        }
        if (!string.Equals(entry.RequestFingerprint, request.RequestFingerprint, StringComparison.Ordinal))
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.FingerprintMismatch), cancellationToken);
        if (entry.Status == 2)
        {
            if (entry.ResultCipher is null)
                throw new InvalidOperationException("已完成的幂等记录缺少结果密文。");
            var json = cipher.Unprotect(entry.KeyVersion, entry.ResultCipher, Context(request.ExecutionKey));
            var result = JsonSerializer.Deserialize<ToolExecutionResult>(json)
                ?? throw new InvalidOperationException("幂等执行结果密文无效。");
            if (cipher.RequiresReencryption(entry.KeyVersion))
            {
                var reencrypted = cipher.Protect(json, Context(request.ExecutionKey));
                await using var rotate = connection.CreateCommand();
                rotate.Transaction = transaction;
                rotate.CommandText = $"UPDATE dbo.{TableName} SET KeyVersion=@version,ResultCipher=@result,UpdatedAt=@now WHERE ExecutionKey=@key AND Status=2;";
                AddString(rotate, "@version", 64, reencrypted.KeyVersion);
                AddString(rotate, "@result", -1, reencrypted.Ciphertext);
                AddDateTimeOffset(rotate, "@now", now); AddAnsiString(rotate, "@key", 64, request.ExecutionKey);
                await rotate.ExecuteNonQueryAsync(cancellationToken);
            }
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.Replay, ReplayResult: result), cancellationToken);
        }
        if (entry.Status == 3)
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.OutcomeUnknown), cancellationToken);
        if (entry.LeaseExpiresAt > now)
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.InProgress), cancellationToken);
        if (entry.Status == 1)
        {
            await UpdateStatusAsync(connection, transaction, request.ExecutionKey, entry.LeaseToken, 1, 3, now,
                cancellationToken);
            return await CommitAsync(transaction,
                new IdempotencyAcquireResult(IdempotencyAcquireStatus.OutcomeUnknown), cancellationToken);
        }
        var renewed = Guid.NewGuid().ToString("N");
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"UPDATE dbo.{TableName} SET RunId=@runId,LeaseToken=@token,LeaseExpiresAt=@expires,UpdatedAt=@now WHERE ExecutionKey=@key AND Status=0;";
            AddString(update, "@runId", 128, request.RunId); AddString(update, "@token", 64, renewed);
            AddDateTimeOffset(update, "@expires", now.Add(leaseDuration)); AddDateTimeOffset(update, "@now", now);
            AddAnsiString(update, "@key", 64, request.ExecutionKey); await update.ExecuteNonQueryAsync(cancellationToken);
        }
        return await CommitAsync(transaction,
            new IdempotencyAcquireResult(IdempotencyAcquireStatus.Acquired, renewed), cancellationToken);
    }

    /// <inheritdoc />
    public Task MarkExecutingAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(executionKey, leaseToken, 0, 1, null, cancellationToken);

    /// <inheritdoc />
    public Task CompleteAsync(string executionKey, string leaseToken, ToolExecutionResult result,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(executionKey, leaseToken, 1, 2, result, cancellationToken);

    /// <inheritdoc />
    public Task MarkOutcomeUnknownAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(executionKey, leaseToken, 1, 3, null, cancellationToken);

    /// <inheritdoc />
    public async Task AbandonAsync(string executionKey, string leaseToken,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM dbo.{TableName} WHERE ExecutionKey=@key AND Status=0 AND LeaseToken=@token;";
        AddAnsiString(command, "@key", 64, executionKey); AddString(command, "@token", 64, leaseToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(string tenantId, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP (@limit) ExecutionKey,TenantId,SubjectId,ToolName,RunId,CreatedAt,UpdatedAt
            FROM dbo.{TableName}
            WHERE TenantId=@tenantId AND Status=3
            ORDER BY UpdatedAt DESC;
            """;
        AddInt(command, "@limit", limit);
        AddString(command, "@tenantId", 128, tenantId);
        var records = new List<OutcomeUnknownToolExecution>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(new OutcomeUnknownToolExecution(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        return records;
    }

    private async Task TransitionAsync(string executionKey, string leaseToken, int expected, int next,
        ToolExecutionResult? result, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var protectedResult = result is null ? null : cipher.Protect(JsonSerializer.Serialize(result), Context(executionKey));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=@next,KeyVersion=@version,ResultCipher=@result,UpdatedAt=@now
            WHERE ExecutionKey=@key AND Status=@expected AND LeaseToken=@token;
            """;
        AddInt(command, "@next", next); AddNullableString(command, "@version", 64, protectedResult?.KeyVersion);
        AddNullableString(command, "@result", -1, protectedResult?.Ciphertext);
        AddDateTimeOffset(command, "@now", timeProvider.GetUtcNow()); AddAnsiString(command, "@key", 64, executionKey);
        AddInt(command, "@expected", expected); AddString(command, "@token", 64, leaseToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("幂等执行账本状态或租约不匹配。");
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            if (!options.InitializeSchema) { _initialized = true; return; }
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF OBJECT_ID(N'dbo.{TableName}',N'U') IS NULL
                BEGIN
                  CREATE TABLE dbo.{TableName}(
                    ExecutionKey char(64) NOT NULL CONSTRAINT PK_{TableName} PRIMARY KEY,
                    RequestFingerprint char(64) NOT NULL,RunId nvarchar(128) NOT NULL,Status tinyint NOT NULL,
                    TenantId nvarchar(128) NULL,SubjectId nvarchar(256) NULL,ToolName nvarchar(128) NULL,
                    LeaseToken nvarchar(64) NOT NULL,LeaseExpiresAt datetimeoffset(7) NOT NULL,
                    KeyVersion nvarchar(64) NULL,ResultCipher nvarchar(max) NULL,
                    CreatedAt datetimeoffset(7) NOT NULL,UpdatedAt datetimeoffset(7) NOT NULL,RowVersion rowversion NOT NULL);
                  CREATE INDEX IX_{TableName}_StatusUpdated ON dbo.{TableName}(Status,UpdatedAt);
                END;
                IF COL_LENGTH(N'dbo.{TableName}',N'TenantId') IS NULL
                    ALTER TABLE dbo.{TableName} ADD TenantId nvarchar(128) NULL;
                IF COL_LENGTH(N'dbo.{TableName}',N'SubjectId') IS NULL
                    ALTER TABLE dbo.{TableName} ADD SubjectId nvarchar(256) NULL;
                IF COL_LENGTH(N'dbo.{TableName}',N'ToolName') IS NULL
                    ALTER TABLE dbo.{TableName} ADD ToolName nvarchar(128) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.{TableName}')
                    AND name=N'IX_{TableName}_TenantStatusUpdated')
                    CREATE INDEX IX_{TableName}_TenantStatusUpdated
                        ON dbo.{TableName}(TenantId,Status,UpdatedAt DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken); _initialized = true;
        }
        finally { _initializationGate.Release(); }
    }

    private static async Task<Entry?> ReadForUpdateAsync(SqlConnection connection, SqlTransaction transaction,
        string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT RequestFingerprint,Status,LeaseToken,LeaseExpiresAt,KeyVersion,ResultCipher FROM dbo.{TableName} WITH (UPDLOCK,HOLDLOCK) WHERE ExecutionKey=@key;";
        AddAnsiString(command, "@key", 64, key); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new Entry(reader.GetString(0), reader.GetByte(1), reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static async Task UpdateStatusAsync(SqlConnection connection, SqlTransaction transaction, string key,
        string token, int expected, int next, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"UPDATE dbo.{TableName} SET Status=@next,UpdatedAt=@now WHERE ExecutionKey=@key AND Status=@expected AND LeaseToken=@token;";
        AddInt(command, "@next", next); AddDateTimeOffset(command, "@now", now); AddAnsiString(command, "@key", 64, key);
        AddInt(command, "@expected", expected); AddString(command, "@token", 64, token);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IdempotencyAcquireResult> CommitAsync(SqlTransaction transaction,
        IdempotencyAcquireResult result, CancellationToken cancellationToken)
    { await transaction.CommitAsync(cancellationToken); return result; }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    { var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync(cancellationToken); return connection; }
    private static string Context(string key) => $"tool-execution:{key}";
    private static void ValidateRequest(ToolExecutionLedgerRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ToolName);
    }
    private static void AddString(SqlCommand command, string name, int size, string value) => command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;
    private static void AddNullableString(SqlCommand command, string name, int size, string? value) => command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value is null ? DBNull.Value : value;
    private static void AddAnsiString(SqlCommand command, string name, int size, string value) => command.Parameters.Add(name, SqlDbType.Char, size).Value = value;
    private static void AddInt(SqlCommand command, string name, int value) => command.Parameters.Add(name, SqlDbType.Int).Value = value;
    private static void AddDateTimeOffset(SqlCommand command, string name, DateTimeOffset value) => command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value;
    private sealed record Entry(string RequestFingerprint, byte Status, string LeaseToken,
        DateTimeOffset LeaseExpiresAt, string? KeyVersion, string? ResultCipher);

    /// <summary>释放架构初始化同步资源。</summary>
    public void Dispose() => _initializationGate.Dispose();
}
