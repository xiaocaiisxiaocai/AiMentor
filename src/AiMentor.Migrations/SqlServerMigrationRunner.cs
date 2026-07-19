using System.Data;
using Microsoft.Data.SqlClient;

namespace AiMentor.Migrations;

public sealed class MigrationRunnerOptions
{
    public MigrationRunnerOptions(string connectionString, string releaseId)
    {
        ConnectionString = connectionString;
        ReleaseId = releaseId;
    }

    public string ConnectionString { get; }
    public string ReleaseId { get; }
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int CommandTimeoutSeconds { get; init; } = 300;
}

public sealed record MigrationRunResult(
    string ReleaseId,
    int DiscoveredCount,
    int PreviouslyAppliedCount,
    int AppliedCount);

public sealed class SqlServerMigrationRunner
{
    private const string LockResource = "AiMentor.SchemaMigrations.v1";
    private readonly MigrationRunnerOptions _options;

    public SqlServerMigrationRunner(MigrationRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new MigrationException("MIGRATION_CONNECTION_MISSING", "必须提供工作流 SQL Server 连接串。");
        if (string.IsNullOrWhiteSpace(options.ReleaseId))
            throw new MigrationException("MIGRATION_RELEASE_ID_MISSING", "必须提供发布标识。");
        if (options.ReleaseId.Length > 128 || options.ReleaseId.Any(char.IsControl))
            throw new MigrationException("MIGRATION_RELEASE_ID_INVALID", "发布标识长度或字符无效。");
        if (options.LockTimeout <= TimeSpan.Zero || options.LockTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "迁移锁等待时间必须处于有效范围内。");
        if (options.CommandTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "迁移命令超时必须大于零。");

        _options = options;
    }

    public async Task<MigrationRunResult> RunAsync(
        IReadOnlyList<MigrationScript> scripts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        if (scripts.Count == 0)
            throw new MigrationException("MIGRATION_SET_EMPTY", "没有可执行的迁移脚本。");

        MigrationLedgerValidator.ValidateScripts(scripts);
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var lockAcquired = false;
        try
        {
            await AcquireLockAsync(connection, cancellationToken);
            lockAcquired = true;
            await EnsureLedgerAsync(connection, cancellationToken);
            var applied = await ReadLedgerAsync(connection, cancellationToken);
            var previouslyApplied = MigrationLedgerValidator.Validate(scripts, applied);

            for (var index = previouslyApplied; index < scripts.Count; index++)
                await ApplyAsync(connection, scripts[index], cancellationToken);

            return new MigrationRunResult(_options.ReleaseId, scripts.Count, previouslyApplied,
                scripts.Count - previouslyApplied);
        }
        finally
        {
            if (lockAcquired && connection.State == ConnectionState.Open)
                await ReleaseLockAsync(connection);
        }
    }

    private async Task AcquireLockAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(_options.LockTimeout.TotalSeconds) + 5);
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = N'Exclusive',
                @LockOwner = N'Session',
                @LockTimeout = @lockTimeout;
            SELECT @result;
            """;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = LockResource });
        command.Parameters.Add(new SqlParameter("@lockTimeout", SqlDbType.Int)
        {
            Value = checked((int)Math.Ceiling(_options.LockTimeout.TotalMilliseconds))
        });
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (result < 0)
            throw new MigrationException("MIGRATION_LOCK_UNAVAILABLE", "无法在等待期限内获取迁移锁。");
    }

    private static async Task ReleaseLockAsync(SqlConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 15;
            command.CommandText = """
                EXEC sys.sp_releaseapplock
                    @Resource = @resource,
                    @LockOwner = N'Session';
                """;
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = LockResource });
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (SqlException)
        {
            // 连接关闭会释放 session 锁；清理失败不能掩盖原始迁移结果。
        }
    }

    private async Task EnsureLedgerAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = _options.CommandTimeoutSeconds;
            command.CommandText = """
                SET XACT_ABORT ON;
                IF OBJECT_ID(N'dbo.AiMentorSchemaMigrations') IS NOT NULL
                   AND OBJECT_ID(N'dbo.AiMentorSchemaMigrations', N'U') IS NULL
                    THROW 51090, N'迁移账本名称已被非表对象占用。', 1;

                IF OBJECT_ID(N'dbo.AiMentorSchemaMigrations', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.AiMentorSchemaMigrations (
                        MigrationVersion int NOT NULL
                            CONSTRAINT PK_AiMentorSchemaMigrations PRIMARY KEY,
                        MigrationName nvarchar(260) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        Checksum char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        ReleaseId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        AppliedAt datetimeoffset(7) NOT NULL
                    );
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await TryRollbackAsync(transaction);
            throw new MigrationException("MIGRATION_LEDGER_INVALID", "无法创建或验证迁移账本。", exception);
        }
    }

    private async Task<IReadOnlyList<AppliedMigration>> ReadLedgerAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _options.CommandTimeoutSeconds;
            command.CommandText = """
                SELECT MigrationVersion, MigrationName, Checksum, ReleaseId, AppliedAt
                FROM dbo.AiMentorSchemaMigrations
                ORDER BY MigrationVersion;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var applied = new List<AppliedMigration>();
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(new AppliedMigration(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDateTimeOffset(4)));
            }

            return applied;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new MigrationException("MIGRATION_LEDGER_INVALID", "无法读取迁移账本。", exception);
        }
    }

    private async Task ApplyAsync(
        SqlConnection connection,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            foreach (var batch in SqlBatchSplitter.Split(script.SqlText))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = _options.CommandTimeoutSeconds;
                command.CommandText = "SET XACT_ABORT ON;\n" + batch;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureTransactionDepthAsync(connection, transaction, script, cancellationToken);
            await InsertLedgerRowAsync(connection, transaction, script, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await TryRollbackAsync(transaction);
            if (exception is MigrationException) throw;
            throw new MigrationException("MIGRATION_EXECUTION_FAILED", $"迁移 {script.Version:000} 执行失败。", exception);
        }
    }

    private async Task EnsureTransactionDepthAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.CommandText = "SELECT @@TRANCOUNT;";
        var depth = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (depth != 1)
        {
            // 外层事务必须仍由执行器持有；脚本遗留嵌套事务会让账本与架构提交边界失真。
            throw new MigrationException("MIGRATION_TRANSACTION_UNBALANCED",
                $"迁移 {script.Version:000} 执行后事务层级不平衡。");
        }
    }

    private async Task InsertLedgerRowAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.CommandText = """
            INSERT INTO dbo.AiMentorSchemaMigrations
                (MigrationVersion, MigrationName, Checksum, ReleaseId, AppliedAt)
            VALUES
                (@version, @name, @checksum, @releaseId, SYSUTCDATETIME());
            """;
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = script.Version });
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 260) { Value = script.FileName });
        command.Parameters.Add(new SqlParameter("@checksum", SqlDbType.Char, 64) { Value = script.Sha256 });
        command.Parameters.Add(new SqlParameter("@releaseId", SqlDbType.NVarChar, 128) { Value = _options.ReleaseId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TryRollbackAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // XACT_ABORT 可能已经回滚外层事务，此时无需再次回滚。
        }
        catch (SqlException)
        {
            // 连接关闭或服务端已终止事务时只能依赖连接清理，保留原始失败原因。
        }
    }
}
