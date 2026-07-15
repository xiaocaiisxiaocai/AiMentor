using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.Data.SqlClient;

namespace AiMentor.Infrastructure;

/// <summary>以认证密文持久化 AtlasID 检查点，并用版本号和短租约保证多实例单胜者。</summary>
public sealed class SqlServerAtlasIncidentStore(
    SqlServerWorkflowOptions options,
    IWorkflowStateCipher cipher,
    TimeProvider timeProvider) : IAtlasIncidentStore, IDisposable
{
    private const string TableName = "AiMentorAtlasIncidentRuns";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public async Task CreateAsync(AtlasIncidentCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var payload = Protect(checkpoint);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO dbo.{TableName}
                (RunId,TenantId,SubjectId,RunbookId,RunbookVersion,Status,Version,ExpiresAt,KeyVersion,
                 PayloadCipher,CreatedAt,UpdatedAt)
            VALUES (@runId,@tenantId,@subjectId,@runbookId,@runbookVersion,@status,@version,@expiresAt,@keyVersion,
                    @payload,@createdAt,@updatedAt);
            """;
        AddCheckpoint(command, checkpoint, payload);
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw Failure("ATLAS_RUN_CONFLICT", "排查运行标识冲突。", AtlasIncidentErrorKind.Conflict);
        }
    }

    public async Task<IReadOnlyList<AtlasIncidentCheckpoint>> ListAsync(AccessContext access, int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP (@limit) RunId,TenantId,SubjectId,RunbookId,RunbookVersion,Status,Version,ExpiresAt,
                KeyVersion,PayloadCipher,LeaseToken,LeaseExpiresAt
            FROM dbo.{TableName}
            WHERE TenantId COLLATE Latin1_General_100_BIN2=@tenantId
              AND SubjectId COLLATE Latin1_General_100_BIN2=@subjectId
            ORDER BY UpdatedAt DESC,RunId ASC;
            """;
        command.Parameters.Add("@limit", System.Data.SqlDbType.Int).Value = limit;
        AddString(command, "@tenantId", 128, access.TenantId);
        AddString(command, "@subjectId", 256, access.SubjectId);
        var result = new List<AtlasIncidentCheckpoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Row(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), (AtlasIncidentStatus)reader.GetByte(5),
                reader.GetInt64(6), reader.GetDateTimeOffset(7), reader.GetString(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetDateTimeOffset(11));
            EnsureOwner(row, access);
            result.Add(Decode(row));
        }
        return result;
    }

    public async Task<AtlasIncidentCheckpoint?> GetAsync(string runId, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var row = await ReadAsync(connection, null, runId, access.TenantId, access.SubjectId, cancellationToken);
        if (row is null) return null;
        EnsureOwner(row, access);
        if (row.ExpiresAt <= timeProvider.GetUtcNow() && !IsTerminal(row.Status))
        {
            await ExpireAsync(connection, row, cancellationToken);
            row = await ReadAsync(connection, null, runId, access.TenantId, access.SubjectId, cancellationToken)
                ?? throw Failure("ATLAS_RUN_NOT_FOUND", "没有找到排查运行。", AtlasIncidentErrorKind.NotFound);
        }
        return await DecodeAndRotateAsync(connection, row, cancellationToken);
    }

    public async Task<AtlasIncidentTaskState?> GetTaskStateAsync(string tenantId, string runId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var row = await ReadAsync(connection, null, runId, tenantId, null, cancellationToken);
        if (row is null || !string.Equals(row.RunId, runId, StringComparison.Ordinal)
            || !string.Equals(row.TenantId, tenantId, StringComparison.Ordinal)) return null;
        if (row.ExpiresAt <= timeProvider.GetUtcNow() && !IsTerminal(row.Status))
        {
            await ExpireAsync(connection, row, cancellationToken);
            row = await ReadAsync(connection, null, runId, tenantId, null, cancellationToken);
            if (row is null || !string.Equals(row.RunId, runId, StringComparison.Ordinal)
                || !string.Equals(row.TenantId, tenantId, StringComparison.Ordinal)) return null;
        }
        var checkpoint = await DecodeAndRotateAsync(connection, row, cancellationToken);
        return new AtlasIncidentTaskState(checkpoint.RunId, checkpoint.Access.TenantId, checkpoint.Status,
            checkpoint.Version, checkpoint.CreatedAt, checkpoint.UpdatedAt, checkpoint.ExpiresAt);
    }

    public async Task<AtlasIncidentLeaseResult> TryAcquireAsync(string runId, AccessContext access,
        long expectedVersion, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        var now = timeProvider.GetUtcNow();
        var token = Guid.NewGuid().ToString("N");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var row = await ReadAsync(connection, transaction, runId, access.TenantId, access.SubjectId,
            cancellationToken, lockRow: true);
        if (row is null) { await transaction.CommitAsync(cancellationToken); return new(false, null, null); }
        EnsureOwner(row, access);
        if (row.ExpiresAt <= now && !IsTerminal(row.Status))
        {
            await SetExpiredAsync(connection, transaction, row, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw Failure("ATLAS_RUN_TERMINAL", "排查运行已经结束，不能继续推进。", AtlasIncidentErrorKind.Conflict);
        }
        if (row.Version != expectedVersion)
            throw Failure("ATLAS_VERSION_CONFLICT", "排查状态已被其他请求推进，请重新读取。", AtlasIncidentErrorKind.Conflict);
        if (IsTerminal(row.Status))
            throw Failure("ATLAS_RUN_TERMINAL", "排查运行已经结束，不能继续推进。", AtlasIncidentErrorKind.Conflict);
        if (row.LeaseExpiresAt > now)
            throw Failure("ATLAS_RUN_BUSY", "排查运行正在由其他请求推进。", AtlasIncidentErrorKind.Conflict);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE dbo.{TableName} SET LeaseToken=@token,LeaseOwner=@owner,LeaseExpiresAt=@leaseExpiresAt,
                    UpdatedAt=@now
                WHERE RunId COLLATE Latin1_General_100_BIN2=@runId
                  AND TenantId COLLATE Latin1_General_100_BIN2=@tenantId
                  AND SubjectId COLLATE Latin1_General_100_BIN2=@subjectId
                  AND Version=@version AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt<=@now);
                """;
            AddString(update, "@token", 64, token);
            AddString(update, "@owner", 256, $"pid-{Environment.ProcessId}");
            AddDateTime(update, "@leaseExpiresAt", now.Add(leaseDuration));
            AddDateTime(update, "@now", now);
            AddString(update, "@runId", 128, runId);
            AddString(update, "@tenantId", 128, row.TenantId);
            AddString(update, "@subjectId", 256, row.SubjectId);
            AddLong(update, "@version", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw Failure("ATLAS_RUN_BUSY", "排查运行正在由其他请求推进。", AtlasIncidentErrorKind.Conflict);
        }
        await transaction.CommitAsync(cancellationToken);
        var checkpoint = Decode(row);
        // 只有租约持有者可以在线轮换，避免无条件重加密覆盖并发更新。
        if (cipher.RequiresReencryption(row.KeyVersion))
            await ReencryptLeasedAsync(connection, row, checkpoint, token, cancellationToken);
        return new(true, token, checkpoint);
    }

    public async Task<AtlasIncidentCheckpoint> SaveAndReleaseAsync(AtlasIncidentCheckpoint checkpoint,
        string leaseToken, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var payload = Protect(checkpoint);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE dbo.{TableName} SET RunbookId=@runbookId,RunbookVersion=@runbookVersion,Status=@status,
                Version=@version,ExpiresAt=@expiresAt,KeyVersion=@keyVersion,PayloadCipher=@payload,
                LeaseToken=NULL,LeaseOwner=NULL,LeaseExpiresAt=NULL,UpdatedAt=@updatedAt
            WHERE RunId COLLATE Latin1_General_100_BIN2=@runId
              AND TenantId COLLATE Latin1_General_100_BIN2=@tenantId
              AND SubjectId COLLATE Latin1_General_100_BIN2=@subjectId AND Version=@oldVersion
              AND LeaseToken=@leaseToken AND LeaseExpiresAt>@now;
            """;
        AddCheckpoint(command, checkpoint, payload);
        AddLong(command, "@oldVersion", checkpoint.Version - 1);
        AddString(command, "@leaseToken", 64, leaseToken);
        AddDateTime(command, "@now", now);
        if (checkpoint.Version <= 1 || await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw Failure("ATLAS_LEASE_LOST", "排查推进租约已失效。", AtlasIncidentErrorKind.Conflict);
        return checkpoint;
    }

    public async Task<AtlasIncidentCheckpoint> CancelAsync(string runId, AccessContext access, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var row = await ReadAsync(connection, transaction, runId, access.TenantId, access.SubjectId,
            cancellationToken, lockRow: true)
            ?? throw Failure("ATLAS_RUN_NOT_FOUND", "没有找到排查运行。", AtlasIncidentErrorKind.NotFound);
        EnsureOwner(row, access);
        if (row.ExpiresAt <= now && !IsTerminal(row.Status))
        {
            await SetExpiredAsync(connection, transaction, row, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetAsync(runId, access, cancellationToken))!;
        }
        if (row.Version != expectedVersion)
            throw Failure("ATLAS_VERSION_CONFLICT", "排查状态已被其他请求推进，请重新读取。", AtlasIncidentErrorKind.Conflict);
        if (IsTerminal(row.Status)) { await transaction.CommitAsync(cancellationToken); return Decode(row); }
        if (row.LeaseExpiresAt > now)
            throw Failure("ATLAS_RUN_BUSY", "排查运行正在由其他请求推进。", AtlasIncidentErrorKind.Conflict);
        var current = Decode(row);
        var cancelled = current with { Status = AtlasIncidentStatus.Cancelled, Version = current.Version + 1, UpdatedAt = now };
        var payload = Protect(cancelled);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=@status,Version=@newVersion,KeyVersion=@keyVersion,
                PayloadCipher=@payload,LeaseToken=NULL,LeaseOwner=NULL,LeaseExpiresAt=NULL,UpdatedAt=@now
            WHERE RunId COLLATE Latin1_General_100_BIN2=@runId
              AND TenantId COLLATE Latin1_General_100_BIN2=@tenantId
              AND SubjectId COLLATE Latin1_General_100_BIN2=@subjectId
              AND Version=@oldVersion AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt<=@now);
            """;
        AddInt(update, "@status", (byte)cancelled.Status);
        AddLong(update, "@newVersion", cancelled.Version);
        AddString(update, "@keyVersion", 64, payload.KeyVersion);
        AddString(update, "@payload", -1, payload.Ciphertext);
        AddDateTime(update, "@now", now);
        AddString(update, "@runId", 128, runId);
        AddString(update, "@tenantId", 128, row.TenantId);
        AddString(update, "@subjectId", 256, row.SubjectId);
        AddLong(update, "@oldVersion", expectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw Failure("ATLAS_RUN_BUSY", "排查运行正在由其他请求推进。", AtlasIncidentErrorKind.Conflict);
        await transaction.CommitAsync(cancellationToken);
        return cancelled;
    }

    private async Task<AtlasIncidentCheckpoint> DecodeAndRotateAsync(SqlConnection connection, Row row,
        CancellationToken cancellationToken)
    {
        var checkpoint = Decode(row);
        if (!cipher.RequiresReencryption(row.KeyVersion)) return checkpoint;
        var payload = Protect(checkpoint);
        await using var update = connection.CreateCommand();
        update.CommandText = $"""
            UPDATE dbo.{TableName} SET KeyVersion=@newKey,PayloadCipher=@newPayload,UpdatedAt=@now
            WHERE RunId COLLATE Latin1_General_100_BIN2=@runId
              AND TenantId COLLATE Latin1_General_100_BIN2=@tenantId
              AND SubjectId COLLATE Latin1_General_100_BIN2=@subjectId
              AND Version=@version AND KeyVersion=@oldKey AND PayloadCipher=@oldPayload;
            """;
        AddString(update, "@newKey", 64, payload.KeyVersion);
        AddString(update, "@newPayload", -1, payload.Ciphertext);
        AddDateTime(update, "@now", timeProvider.GetUtcNow());
        AddString(update, "@runId", 128, row.RunId);
        AddString(update, "@tenantId", 128, row.TenantId);
        AddString(update, "@subjectId", 256, row.SubjectId);
        AddLong(update, "@version", row.Version);
        AddString(update, "@oldKey", 64, row.KeyVersion);
        AddString(update, "@oldPayload", -1, row.PayloadCipher);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return checkpoint;
    }

    private async Task ReencryptLeasedAsync(SqlConnection connection, Row row, AtlasIncidentCheckpoint checkpoint,
        string leaseToken, CancellationToken cancellationToken)
    {
        var payload = Protect(checkpoint);
        await using var update = connection.CreateCommand();
        update.CommandText = $"""
            UPDATE dbo.{TableName} SET KeyVersion=@keyVersion,PayloadCipher=@payload,UpdatedAt=@now
            WHERE RunId COLLATE Latin1_General_100_BIN2=@runId
              AND TenantId COLLATE Latin1_General_100_BIN2=@tenantId
              AND SubjectId COLLATE Latin1_General_100_BIN2=@subjectId
              AND Version=@version AND LeaseToken=@leaseToken;
            """;
        AddString(update, "@keyVersion", 64, payload.KeyVersion);
        AddString(update, "@payload", -1, payload.Ciphertext);
        AddDateTime(update, "@now", timeProvider.GetUtcNow());
        AddString(update, "@runId", 128, row.RunId);
        AddString(update, "@tenantId", 128, row.TenantId);
        AddString(update, "@subjectId", 256, row.SubjectId);
        AddLong(update, "@version", row.Version);
        AddString(update, "@leaseToken", 64, leaseToken);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw Failure("ATLAS_LEASE_LOST", "排查推进租约在密钥轮换期间失效。", AtlasIncidentErrorKind.Conflict);
    }

    private AtlasIncidentCheckpoint Decode(Row row)
    {
        var json = cipher.Unprotect(row.KeyVersion, row.PayloadCipher, Context(row.RunId, row.TenantId, row.SubjectId));
        var checkpoint = JsonSerializer.Deserialize<AtlasIncidentCheckpoint>(json, SerializerOptions)
            ?? throw new CryptographicException("AtlasID 检查点密文载荷无效。");
        // 明文列只用于查找；任何路由字段与认证密文不一致都可能是搬移或篡改，必须失败关闭。
        if (checkpoint.RunId != row.RunId || checkpoint.Access.TenantId != row.TenantId
            || checkpoint.Access.SubjectId != row.SubjectId || checkpoint.Version != row.Version
            || checkpoint.Status != row.Status || checkpoint.RunbookId != row.RunbookId
            || checkpoint.RunbookVersion != row.RunbookVersion || checkpoint.ExpiresAt != row.ExpiresAt)
            throw new CryptographicException("AtlasID 检查点路由元数据与认证密文不一致。");
        return checkpoint;
    }

    private ProtectedWorkflowState Protect(AtlasIncidentCheckpoint checkpoint) => cipher.Protect(
        JsonSerializer.Serialize(checkpoint, SerializerOptions), Context(checkpoint.RunId, checkpoint.Access.TenantId,
            checkpoint.Access.SubjectId));

    private async Task ExpireAsync(SqlConnection connection, Row row, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        await SetExpiredAsync(connection, transaction, row, timeProvider.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SetExpiredAsync(SqlConnection connection, SqlTransaction transaction, Row row,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var checkpoint = Decode(row) with
        {
            Status = AtlasIncidentStatus.Expired,
            Version = row.Version + 1,
            UpdatedAt = now
        };
        var payload = Protect(checkpoint);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"""
            UPDATE dbo.{TableName} SET Status=@status,Version=@newVersion,KeyVersion=@keyVersion,
                PayloadCipher=@payload,LeaseToken=NULL,LeaseOwner=NULL,LeaseExpiresAt=NULL,UpdatedAt=@now
            WHERE RunId COLLATE Latin1_General_100_BIN2=@runId
              AND TenantId COLLATE Latin1_General_100_BIN2=@tenantId
              AND SubjectId COLLATE Latin1_General_100_BIN2=@subjectId
              AND Version=@oldVersion AND ExpiresAt<=@now AND Status NOT IN (@cancelled,@expired);
            """;
        AddInt(update, "@status", (byte)AtlasIncidentStatus.Expired);
        AddLong(update, "@newVersion", checkpoint.Version);
        AddString(update, "@keyVersion", 64, payload.KeyVersion);
        AddString(update, "@payload", -1, payload.Ciphertext);
        AddDateTime(update, "@now", now);
        AddString(update, "@runId", 128, row.RunId);
        AddString(update, "@tenantId", 128, row.TenantId);
        AddString(update, "@subjectId", 256, row.SubjectId);
        AddLong(update, "@oldVersion", row.Version);
        AddInt(update, "@cancelled", (byte)AtlasIncidentStatus.Cancelled);
        AddInt(update, "@expired", (byte)AtlasIncidentStatus.Expired);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsTerminal(AtlasIncidentStatus status) =>
        status is AtlasIncidentStatus.Cancelled or AtlasIncidentStatus.Expired;

    private static void EnsureOwner(Row row, AccessContext access)
    {
        if (!string.Equals(row.TenantId, access.TenantId, StringComparison.Ordinal)
            || !string.Equals(row.SubjectId, access.SubjectId, StringComparison.Ordinal))
            throw Failure("ATLAS_RUN_FORBIDDEN", "只有原始调用者可以访问排查运行。", AtlasIncidentErrorKind.Forbidden);
    }

    private static async Task<Row?> ReadAsync(SqlConnection connection, SqlTransaction? transaction, string runId,
        string tenantId, string? subjectId, CancellationToken cancellationToken, bool lockRow = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT RunId,TenantId,SubjectId,RunbookId,RunbookVersion,Status,Version,ExpiresAt,KeyVersion,
                   PayloadCipher,LeaseToken,LeaseExpiresAt
            FROM dbo.{TableName} {(lockRow ? "WITH (UPDLOCK,HOLDLOCK)" : string.Empty)}
            WHERE RunId COLLATE Latin1_General_100_BIN2=@runId
              AND TenantId COLLATE Latin1_General_100_BIN2=@tenantId
              AND (@subjectId IS NULL OR SubjectId COLLATE Latin1_General_100_BIN2=@subjectId);
            """;
        AddString(command, "@runId", 128, runId);
        AddString(command, "@tenantId", 128, tenantId);
        AddNullableString(command, "@subjectId", 256, subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), (AtlasIncidentStatus)reader.GetByte(5), reader.GetInt64(6),
            reader.GetDateTimeOffset(7), reader.GetString(8), reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetDateTimeOffset(11));
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
                throw new InvalidOperationException("SQL Server 工作流配置无效。");
            if (options.InitializeSchema)
            {
                await using var connection = await OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    IF OBJECT_ID(N'dbo.{TableName}', N'U') IS NULL
                    BEGIN
                        CREATE TABLE dbo.{TableName}(
                            RunId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL
                                CONSTRAINT PK_{TableName} PRIMARY KEY,
                            TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                            SubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                            RunbookId nvarchar(128) NOT NULL,RunbookVersion nvarchar(64) NOT NULL,
                            Status tinyint NOT NULL,Version bigint NOT NULL,ExpiresAt datetimeoffset(7) NOT NULL,
                            KeyVersion nvarchar(64) NOT NULL,PayloadCipher nvarchar(max) NOT NULL,
                            LeaseToken nvarchar(64) NULL,
                            LeaseOwner nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
                            LeaseExpiresAt datetimeoffset(7) NULL,CreatedAt datetimeoffset(7) NOT NULL,
                            UpdatedAt datetimeoffset(7) NOT NULL,RowVersion rowversion NOT NULL);
                        CREATE INDEX IX_{TableName}_OwnerStatus ON dbo.{TableName}(TenantId,SubjectId,Status);
                        CREATE INDEX IX_{TableName}_LeaseExpiry ON dbo.{TableName}(Status,LeaseExpiresAt);
                        CREATE INDEX IX_{TableName}_ExpiresAt ON dbo.{TableName}(ExpiresAt);
                        CREATE INDEX IX_{TableName}_KeyVersion ON dbo.{TableName}(KeyVersion);
                    END;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            _initialized = true;
        }
        finally { _initializationGate.Release(); }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddCheckpoint(SqlCommand command, AtlasIncidentCheckpoint checkpoint,
        ProtectedWorkflowState payload)
    {
        AddString(command, "@runId", 128, checkpoint.RunId);
        AddString(command, "@tenantId", 128, checkpoint.Access.TenantId);
        AddString(command, "@subjectId", 256, checkpoint.Access.SubjectId);
        AddString(command, "@runbookId", 128, checkpoint.RunbookId);
        AddString(command, "@runbookVersion", 64, checkpoint.RunbookVersion);
        AddInt(command, "@status", (byte)checkpoint.Status);
        AddLong(command, "@version", checkpoint.Version);
        AddDateTime(command, "@expiresAt", checkpoint.ExpiresAt);
        AddString(command, "@keyVersion", 64, payload.KeyVersion);
        AddString(command, "@payload", -1, payload.Ciphertext);
        AddDateTime(command, "@createdAt", checkpoint.CreatedAt);
        AddDateTime(command, "@updatedAt", checkpoint.UpdatedAt);
    }

    private static void AddString(SqlCommand command, string name, int size, string value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;
    private static void AddNullableString(SqlCommand command, string name, int size, string? value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value is null ? DBNull.Value : value;
    private static void AddLong(SqlCommand command, string name, long value) =>
        command.Parameters.Add(name, SqlDbType.BigInt).Value = value;
    private static void AddInt(SqlCommand command, string name, byte value) =>
        command.Parameters.Add(name, SqlDbType.TinyInt).Value = value;
    private static void AddDateTime(SqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value;
    private static string Context(string runId, string tenantId, string subjectId) =>
        $"atlas-incident:{runId}:{tenantId}:{subjectId}";
    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var serializerOptions = new JsonSerializerOptions();
        serializerOptions.Converters.Add(new ReadOnlyStringSetJsonConverter());
        return serializerOptions;
    }
    private static AtlasIncidentWorkflowException Failure(string code, string message, AtlasIncidentErrorKind kind) =>
        new(code, message, kind);

    public void Dispose() => _initializationGate.Dispose();

    private sealed record Row(string RunId, string TenantId, string SubjectId, string RunbookId,
        string RunbookVersion, AtlasIncidentStatus Status, long Version, DateTimeOffset ExpiresAt,
        string KeyVersion, string PayloadCipher, string? LeaseToken, DateTimeOffset? LeaseExpiresAt);
}
