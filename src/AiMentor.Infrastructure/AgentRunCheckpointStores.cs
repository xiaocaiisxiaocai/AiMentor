using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace AiMentor.Infrastructure;

/// <summary>
/// 为开发和测试提供进程内检查点存储。租约转换与 SQL 实现保持同一语义，便于验证多实例竞争逻辑。
/// </summary>
public sealed class InMemoryAgentRunCheckpointStore(TimeProvider timeProvider, int maximumEntries = 1_000)
    : IAgentRunCheckpointStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SavePendingAsync(AgentRunCheckpoint checkpoint, string? leaseToken = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Prune(checkpoint.CreatedAt);
            if (_entries.TryGetValue(checkpoint.RunId, out var existing))
            {
                // 二次暂停只能由持有当前租约的恢复者覆盖，防止旧实例篡改新检查点。
                if (existing.Status != StoredStatus.Leased
                    || string.IsNullOrWhiteSpace(leaseToken)
                    || !FixedEquals(existing.LeaseToken, leaseToken))
                    throw new AgentRunWorkflowException("AGENT_CHECKPOINT_CONFLICT", "Agent 暂停点已经存在或租约不匹配。",
                        AgentRunWorkflowErrorKind.Conflict);
                _entries[checkpoint.RunId] = new Entry(checkpoint, StoredStatus.Pending);
                return Task.CompletedTask;
            }
            if (leaseToken is not null)
                throw new AgentRunWorkflowException("AGENT_CHECKPOINT_NOT_FOUND", "找不到要更新的 Agent 暂停点。",
                    AgentRunWorkflowErrorKind.NotFound);
            if (_entries.Count >= maximumEntries)
                throw new AgentRunWorkflowException("AGENT_PENDING_CAPACITY_EXCEEDED", "等待审批的 Agent 运行已达到容量上限。",
                    AgentRunWorkflowErrorKind.Capacity);
            _entries.Add(checkpoint.RunId, new Entry(checkpoint, StoredStatus.Pending));
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AgentRunLeaseResult> TryAcquireAsync(string runId, AccessContext access, string leaseOwner,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (!_entries.TryGetValue(runId, out var entry))
                return Task.FromResult(new AgentRunLeaseResult(AgentRunLeaseStatus.NotFound));
            if (!SameOwner(entry.Checkpoint.Access, access))
                return Task.FromResult(new AgentRunLeaseResult(AgentRunLeaseStatus.Forbidden));
            // 审批过期仍需恢复一次，让上层形成明确拒绝并写入终态审计；不能在存储层静默删除。
            if (entry.Status == StoredStatus.Leased && entry.LeaseExpiresAt > now)
                return Task.FromResult(new AgentRunLeaseResult(AgentRunLeaseStatus.Busy));

            var token = Guid.NewGuid().ToString("N");
            _entries[runId] = entry with
            {
                Status = StoredStatus.Leased,
                LeaseToken = token,
                LeaseOwner = leaseOwner,
                LeaseExpiresAt = now.Add(leaseDuration)
            };
            return Task.FromResult(new AgentRunLeaseResult(AgentRunLeaseStatus.Acquired, token, entry.Checkpoint));
        }
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string runId, string leaseToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_entries.TryGetValue(runId, out var entry) && entry.Status == StoredStatus.Leased
                && FixedEquals(entry.LeaseToken, leaseToken))
                _entries[runId] = new Entry(entry.Checkpoint, StoredStatus.Pending);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CompleteAsync(string runId, string leaseToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_entries.TryGetValue(runId, out var entry) && entry.Status == StoredStatus.Leased
                && FixedEquals(entry.LeaseToken, leaseToken))
                _entries.Remove(runId);
        }
        return Task.CompletedTask;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var item in _entries.Where(item => item.Value.Checkpoint.ExpiresAt <= now).ToArray())
            _entries.Remove(item.Key);
    }

    private static bool SameOwner(AccessContext left, AccessContext right) =>
        string.Equals(left.TenantId, right.TenantId, StringComparison.Ordinal)
        && string.Equals(left.SubjectId, right.SubjectId, StringComparison.Ordinal);

    private static bool FixedEquals(string? left, string? right) =>
        left is not null && right is not null
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));

    private enum StoredStatus { Pending, Leased }

    private sealed record Entry(AgentRunCheckpoint Checkpoint, StoredStatus Status,
        string? LeaseToken = null, string? LeaseOwner = null, DateTimeOffset? LeaseExpiresAt = null);
}

/// <summary>配置 SQL Server 工作流连接、表名和检查点容量。</summary>
public sealed class SqlServerWorkflowOptions
{
    public required string ConnectionString { get; init; }
    public string AgentRunTable { get; init; } = "AiMentorAgentRuns";
    public int MaximumPendingRuns { get; init; } = 10_000;
    public bool InitializeSchema { get; init; } = true;
}

/// <summary>
/// 将加密 Agent 会话写入 SQL Server，并以条件更新实现跨实例恢复租约。
/// 明文列只保存路由和生命周期元数据，参数、轨迹与会话均位于带上下文认证的密文载荷中。
/// </summary>
public sealed class SqlServerAgentRunCheckpointStore(
    SqlServerWorkflowOptions options,
    IMemoryCipher cipher,
    TimeProvider timeProvider) : IAgentRunCheckpointStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;
    private string TableName => ValidateTableName(options.AgentRunTable);

    /// <inheritdoc />
    public async Task SavePendingAsync(AgentRunCheckpoint checkpoint, string? leaseToken = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var payload = cipher.Protect(JsonSerializer.Serialize(checkpoint, SerializerOptions), Context(checkpoint.RunId));
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (leaseToken is not null)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = $"""
                UPDATE dbo.[{TableName}]
                SET ApprovalId=@approvalId, ExpiresAt=@expiresAt, PayloadCipher=@payload,
                    Status=0, LeaseToken=NULL, LeaseOwner=NULL, LeaseExpiresAt=NULL, UpdatedAt=@now
                WHERE RunId=@runId AND Status=1 AND LeaseToken=@leaseToken;
                """;
            AddCheckpointParameters(update, checkpoint, payload);
            AddString(update, "@leaseToken", 64, leaseToken);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new AgentRunWorkflowException("AGENT_CHECKPOINT_CONFLICT", "Agent 暂停点租约不匹配。",
                    AgentRunWorkflowErrorKind.Conflict);
            return;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = $"SELECT COUNT_BIG(1) FROM dbo.[{TableName}] WITH (UPDLOCK,HOLDLOCK);";
            var current = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (current >= options.MaximumPendingRuns)
                throw new AgentRunWorkflowException("AGENT_PENDING_CAPACITY_EXCEEDED", "等待审批的 Agent 运行已达到容量上限。",
                    AgentRunWorkflowErrorKind.Capacity);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT INTO dbo.[{TableName}]
                    (RunId,TenantId,SubjectId,ApprovalId,ExpiresAt,PayloadCipher,Status,CreatedAt,UpdatedAt)
                VALUES (@runId,@tenantId,@subjectId,@approvalId,@expiresAt,@payload,0,@now,@now);
                """;
            AddCheckpointParameters(insert, checkpoint, payload);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new AgentRunWorkflowException("AGENT_RUN_ALREADY_PENDING", "相同运行标识已有等待审批的 Agent 运行。",
                AgentRunWorkflowErrorKind.Conflict);
        }
    }

    /// <inheritdoc />
    public async Task<AgentRunLeaseResult> TryAcquireAsync(string runId, AccessContext access, string leaseOwner,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var token = Guid.NewGuid().ToString("N");
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE dbo.[{TableName}]
            SET Status=1, LeaseToken=@token, LeaseOwner=@owner, LeaseExpiresAt=@leaseExpiresAt, UpdatedAt=@now
            OUTPUT inserted.PayloadCipher
            WHERE RunId=@runId AND TenantId=@tenantId AND SubjectId=@subjectId
              AND (Status=0 OR (Status=1 AND LeaseExpiresAt<=@now));
            """;
        AddString(command, "@runId", 128, runId);
        AddString(command, "@tenantId", 128, access.TenantId);
        AddString(command, "@subjectId", 256, access.SubjectId);
        AddString(command, "@token", 64, token);
        AddString(command, "@owner", 256, leaseOwner);
        AddDateTimeOffset(command, "@leaseExpiresAt", now.Add(leaseDuration));
        AddDateTimeOffset(command, "@now", now);
        var encrypted = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (encrypted is not null)
        {
            var json = cipher.Unprotect(encrypted, Context(runId));
            var checkpoint = JsonSerializer.Deserialize<AgentRunCheckpoint>(json, SerializerOptions)
                ?? throw new InvalidOperationException("SQL Server 中的 Agent 检查点载荷无效。");
            return new AgentRunLeaseResult(AgentRunLeaseStatus.Acquired, token, checkpoint);
        }

        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"SELECT TenantId,SubjectId,Status FROM dbo.[{TableName}] WHERE RunId=@runId;";
        AddString(inspect, "@runId", 128, runId);
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new AgentRunLeaseResult(AgentRunLeaseStatus.NotFound);
        if (!string.Equals(reader.GetString(0), access.TenantId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), access.SubjectId, StringComparison.Ordinal))
            return new AgentRunLeaseResult(AgentRunLeaseStatus.Forbidden);
        return new AgentRunLeaseResult(AgentRunLeaseStatus.Busy);
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string runId, string leaseToken, CancellationToken cancellationToken = default) =>
        ChangeLeaseAsync(runId, leaseToken, false, cancellationToken);

    /// <inheritdoc />
    public Task CompleteAsync(string runId, string leaseToken, CancellationToken cancellationToken = default) =>
        ChangeLeaseAsync(runId, leaseToken, true, cancellationToken);

    private async Task ChangeLeaseAsync(string runId, string leaseToken, bool delete,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = delete
            ? $"DELETE FROM dbo.[{TableName}] WHERE RunId=@runId AND Status=1 AND LeaseToken=@leaseToken;"
            : $"UPDATE dbo.[{TableName}] SET Status=0,LeaseToken=NULL,LeaseOwner=NULL,LeaseExpiresAt=NULL,UpdatedAt=@now WHERE RunId=@runId AND Status=1 AND LeaseToken=@leaseToken;";
        AddString(command, "@runId", 128, runId);
        AddString(command, "@leaseToken", 64, leaseToken);
        if (!delete) AddDateTimeOffset(command, "@now", timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            ValidateOptions();
            if (!options.InitializeSchema)
            {
                _initialized = true;
                return;
            }
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                DECLARE @lockResult int;
                EXEC @lockResult=sys.sp_getapplock @Resource=N'AiMentor.Workflow.Schema',
                    @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000;
                IF @lockResult < 0 THROW 51000, '无法获取 AiMentor 数据库架构锁。', 1;
                IF OBJECT_ID(N'dbo.{TableName}', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.[{TableName}] (
                        RunId nvarchar(128) NOT NULL CONSTRAINT PK_{TableName} PRIMARY KEY,
                        TenantId nvarchar(128) NOT NULL,
                        SubjectId nvarchar(256) NOT NULL,
                        ApprovalId nvarchar(128) NOT NULL,
                        ExpiresAt datetimeoffset(7) NOT NULL,
                        PayloadCipher nvarchar(max) NOT NULL,
                        Status tinyint NOT NULL,
                        LeaseToken nvarchar(64) NULL,
                        LeaseOwner nvarchar(256) NULL,
                        LeaseExpiresAt datetimeoffset(7) NULL,
                        CreatedAt datetimeoffset(7) NOT NULL,
                        UpdatedAt datetimeoffset(7) NOT NULL,
                        RowVersion rowversion NOT NULL
                    );
                    CREATE INDEX IX_{TableName}_OwnerStatus ON dbo.[{TableName}](TenantId,SubjectId,Status);
                    CREATE INDEX IX_{TableName}_LeaseExpiry ON dbo.[{TableName}](Status,LeaseExpiresAt);
                END;
                COMMIT TRANSACTION;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString) || options.MaximumPendingRuns <= 0)
            throw new InvalidOperationException("SQL Server 工作流配置无效。");
        _ = TableName;
    }

    private void AddCheckpointParameters(SqlCommand command, AgentRunCheckpoint checkpoint, string payload)
    {
        AddString(command, "@runId", 128, checkpoint.RunId);
        AddString(command, "@tenantId", 128, checkpoint.Access.TenantId);
        AddString(command, "@subjectId", 256, checkpoint.Access.SubjectId);
        AddString(command, "@approvalId", 128, checkpoint.ApprovalId);
        AddDateTimeOffset(command, "@expiresAt", checkpoint.ExpiresAt);
        AddString(command, "@payload", -1, payload);
        AddDateTimeOffset(command, "@now", timeProvider.GetUtcNow());
    }

    private static void AddString(SqlCommand command, string name, int size, string value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;

    private static void AddDateTimeOffset(SqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value;

    private static string ValidateTableName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(character => char.IsLetterOrDigit(character) || character == '_')
            ? value : throw new InvalidOperationException("SQL Server 工作流表名无效。");

    private static string Context(string runId) => $"agent-run:{runId}";

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var serializerOptions = new JsonSerializerOptions();
        serializerOptions.Converters.Add(new ReadOnlyStringSetJsonConverter());
        return serializerOptions;
    }

    /// <summary>释放仅用于一次性架构初始化的同步资源。</summary>
    public void Dispose() => _initializationGate.Dispose();
}

/// <summary>把只读字符串集合还原为使用序号比较的 HashSet，保持身份组匹配语义。</summary>
internal sealed class ReadOnlyStringSetJsonConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var values = JsonSerializer.Deserialize<string[]>(ref reader, options) ?? [];
        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value,
        JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Order(StringComparer.Ordinal).ToArray(), options);
}
