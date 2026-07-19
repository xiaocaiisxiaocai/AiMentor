using AiMentor.Migrations;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Sdk;

namespace AiMentor.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class SqlServerMigrationRunnerTests
{
    [Fact]
    public async Task EmptyDatabaseAppliesAllMigrationsAndTamperingOrPhysicalContractDriftFails()
    {
        var database = $"AiMentorMigrationTest_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        using var migrationCopy = CopyMigrations();

        try
        {
            var scripts = MigrationDiscovery.Discover(migrationCopy.Path);
            var runner = CreateRunner(connectionString, "release-first");

            var first = await runner.RunAsync(scripts);
            var rerun = await CreateRunner(connectionString, "release-rerun").RunAsync(scripts);
            var verified = await new SqlServerMigrationVerifier(connectionString).VerifyAsync(scripts);
            File.AppendAllText(Path.Combine(migrationCopy.Path, scripts[0].FileName), "\nSELECT 1;");
            var tampered = MigrationDiscovery.Discover(migrationCopy.Path);
            var error = await Assert.ThrowsAsync<MigrationException>(() =>
                CreateRunner(connectionString, "release-tampered").RunAsync(tampered));
            await ExecuteAsync(connectionString,
                "UPDATE dbo.AiMentorSchemaMigrations SET Checksum = REPLICATE('0', 64) WHERE MigrationVersion = 1;");
            var ledgerError = await Assert.ThrowsAsync<MigrationException>(() =>
                new SqlServerMigrationVerifier(connectionString).VerifyAsync(scripts));
            await ExecuteAsync(connectionString,
                $"UPDATE dbo.AiMentorSchemaMigrations SET Checksum = '{scripts[0].Sha256}' WHERE MigrationVersion = 1;");
            await ExecuteAsync(connectionString, """
                DROP INDEX IX_AiMentorOperationsActions_Queue ON dbo.AiMentorOperationsActions;
                CREATE INDEX IX_AiMentorOperationsActions_Queue ON dbo.AiMentorToolApprovals(Status);
                """);
            var wrongTableIndexError = await Assert.ThrowsAsync<MigrationException>(() =>
                new SqlServerMigrationVerifier(connectionString).VerifyAsync(scripts));
            await ExecuteAsync(connectionString, """
                DROP INDEX IX_AiMentorOperationsActions_Queue ON dbo.AiMentorToolApprovals;
                CREATE INDEX IX_AiMentorOperationsActions_Queue
                    ON dbo.AiMentorOperationsActions(TenantId, Status, ExpiresAt);
                """);
            await ExecuteAsync(connectionString, """
                DROP INDEX UX_AiMentorToolCompensations_Approval ON dbo.AiMentorToolCompensations;
                CREATE INDEX UX_AiMentorToolCompensations_Approval
                    ON dbo.AiMentorToolCompensations(ApprovalId);
                """);
            var filteredUniqueIndexError = await Assert.ThrowsAsync<MigrationException>(() =>
                new SqlServerMigrationVerifier(connectionString).VerifyAsync(scripts));
            await ExecuteAsync(connectionString, """
                SET QUOTED_IDENTIFIER ON;
                DROP INDEX UX_AiMentorToolCompensations_Approval ON dbo.AiMentorToolCompensations;
                CREATE UNIQUE INDEX UX_AiMentorToolCompensations_Approval
                    ON dbo.AiMentorToolCompensations(ApprovalId) WHERE ApprovalId IS NOT NULL;
                """);
            await ExecuteAsync(connectionString, """
                DROP INDEX IX_AiMentorAgentRuns_OwnerStatus ON dbo.AiMentorAgentRuns;
                CREATE INDEX IX_AiMentorAgentRuns_OwnerStatus
                    ON dbo.AiMentorAgentRuns(Status, TenantId, SubjectId);
                """);
            var indexOrderError = await Assert.ThrowsAsync<MigrationException>(() =>
                new SqlServerMigrationVerifier(connectionString).VerifyAsync(scripts));
            await ExecuteAsync(connectionString, """
                DROP INDEX IX_AiMentorAgentRuns_OwnerStatus ON dbo.AiMentorAgentRuns;
                CREATE INDEX IX_AiMentorAgentRuns_OwnerStatus
                    ON dbo.AiMentorAgentRuns(TenantId, SubjectId, Status);
                """);
            await ExecuteAsync(connectionString, """
                ALTER TABLE dbo.AiMentorToolReconciliations
                    DROP CONSTRAINT FK_AiMentorToolReconciliations_Execution;
                ALTER TABLE dbo.AiMentorToolCompensationReconciliations
                    DROP CONSTRAINT FK_AiMentorToolCompensationReconciliations_Compensation;
                ALTER TABLE dbo.AiMentorToolReconciliations
                    ADD CONSTRAINT FK_AiMentorToolCompensationReconciliations_Compensation
                    FOREIGN KEY (ExecutionKey) REFERENCES dbo.AiMentorToolExecutions(ExecutionKey)
                    ON DELETE CASCADE;
                ALTER TABLE dbo.AiMentorToolCompensationReconciliations
                    ADD CONSTRAINT FK_AiMentorToolReconciliations_Execution
                    FOREIGN KEY (CompensationId) REFERENCES dbo.AiMentorToolCompensations(Id)
                    ON DELETE CASCADE;
                """);
            var foreignKeyContractError = await Assert.ThrowsAsync<MigrationException>(() =>
                new SqlServerMigrationVerifier(connectionString).VerifyAsync(scripts));
            await ExecuteAsync(connectionString, """
                ALTER TABLE dbo.AiMentorToolReconciliations
                    DROP CONSTRAINT FK_AiMentorToolCompensationReconciliations_Compensation;
                ALTER TABLE dbo.AiMentorToolCompensationReconciliations
                    DROP CONSTRAINT FK_AiMentorToolReconciliations_Execution;
                ALTER TABLE dbo.AiMentorToolReconciliations
                    ADD CONSTRAINT FK_AiMentorToolReconciliations_Execution
                    FOREIGN KEY (ExecutionKey) REFERENCES dbo.AiMentorToolExecutions(ExecutionKey)
                    ON DELETE CASCADE;
                ALTER TABLE dbo.AiMentorToolCompensationReconciliations
                    ADD CONSTRAINT FK_AiMentorToolCompensationReconciliations_Compensation
                    FOREIGN KEY (CompensationId) REFERENCES dbo.AiMentorToolCompensations(Id)
                    ON DELETE CASCADE;
                """);
            await ExecuteAsync(connectionString,
                "ALTER TABLE dbo.AiMentorAgentRuns DROP COLUMN PayloadCipher;");
            var schemaError = await Assert.ThrowsAsync<MigrationException>(() =>
                new SqlServerMigrationVerifier(connectionString).VerifyAsync(scripts));

            Assert.Equal(scripts.Count, first.AppliedCount);
            Assert.Equal(0, first.PreviouslyAppliedCount);
            Assert.Equal(0, rerun.AppliedCount);
            Assert.Equal(scripts.Count, rerun.PreviouslyAppliedCount);
            Assert.Equal(scripts.Count, verified.AppliedCount);
            Assert.Equal("MIGRATION_CHECKSUM_MISMATCH", error.Code);
            Assert.Equal("MIGRATION_CHECKSUM_MISMATCH", ledgerError.Code);
            Assert.Equal("MIGRATION_SCHEMA_DRIFT", wrongTableIndexError.Code);
            Assert.Equal("MIGRATION_SCHEMA_DRIFT", filteredUniqueIndexError.Code);
            Assert.Equal("MIGRATION_SCHEMA_DRIFT", indexOrderError.Code);
            Assert.Equal("MIGRATION_SCHEMA_DRIFT", foreignKeyContractError.Code);
            Assert.Equal("MIGRATION_SCHEMA_DRIFT", schemaError.Code);
            Assert.Equal(scripts.Count, await CountLedgerRowsAsync(connectionString));
        }
        finally
        {
            await DropDatabaseAsync(master.ConnectionString, database);
        }
    }

    [Fact]
    public async Task VerificationOnEmptyDatabaseFailsWithoutCreatingLedger()
    {
        var database = $"AiMentorMigrationProbe_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        var scripts = MigrationDiscovery.Discover(FindMigrationsRoot());

        try
        {
            var error = await Assert.ThrowsAsync<MigrationException>(() =>
                new SqlServerMigrationVerifier(connectionString).VerifyAsync(scripts));

            Assert.Equal("MIGRATION_LEDGER_MISSING", error.Code);
            Assert.False(await LedgerExistsAsync(connectionString));
        }
        finally
        {
            await DropDatabaseAsync(master.ConnectionString, database);
        }
    }

    [Fact]
    public async Task ConcurrentRunnersHaveExactlyOneMigrationWinner()
    {
        var database = $"AiMentorMigrationRace_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        var scripts = MigrationDiscovery.Discover(FindMigrationsRoot());

        try
        {
            var results = await Task.WhenAll(
                CreateRunner(connectionString, "release-concurrent-a").RunAsync(scripts),
                CreateRunner(connectionString, "release-concurrent-b").RunAsync(scripts));

            Assert.Single(results, result => result.AppliedCount == scripts.Count);
            Assert.Single(results, result => result.AppliedCount == 0);
            Assert.Equal(scripts.Count, await CountLedgerRowsAsync(connectionString));
        }
        finally
        {
            await DropDatabaseAsync(master.ConnectionString, database);
        }
    }

    private static SqlServerMigrationRunner CreateRunner(string connectionString, string releaseId) =>
        new(new MigrationRunnerOptions(connectionString, releaseId)
        {
            LockTimeout = TimeSpan.FromSeconds(90),
            CommandTimeoutSeconds = 300
        });

    private static TemporaryDirectory CopyMigrations()
    {
        var directory = new TemporaryDirectory();
        foreach (var source in Directory.EnumerateFiles(FindMigrationsRoot(), "*.sql"))
            File.Copy(source, Path.Combine(directory.Path, Path.GetFileName(source)));
        return directory;
    }

    private static string FindMigrationsRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "deploy", "sql");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("找不到 deploy/sql 迁移目录。");
    }

    private static SqlConnectionStringBuilder MasterConnection()
    {
        var external = Environment.GetEnvironmentVariable("AIMENTOR_SQLSERVER_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
            return new SqlConnectionStringBuilder(external) { InitialCatalog = "master" };
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip(
                "非 Windows 平台需显式提供 AIMENTOR_SQLSERVER_TEST_CONNECTION；未执行不能记为通过。");
        return new SqlConnectionStringBuilder
        {
            DataSource = "(localdb)\\MSSQLLocalDB",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true
        };
    }

    private static async Task<int> CountLedgerRowsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.AiMentorSchemaMigrations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> LedgerExistsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.AiMentorSchemaMigrations', N'U') IS NULL THEN 0 ELSE 1 END;";
        return Convert.ToBoolean(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string masterConnectionString, string database)
    {
        SqlConnection.ClearAllPools();
        await ExecuteAsync(masterConnectionString,
            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aimentor-migrations-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
