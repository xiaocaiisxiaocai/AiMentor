using System.Data;
using AiMentor.Application;
using AiMentor.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Sdk;

namespace AiMentor.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class SqlServerOperationsActionStoreTests
{
    [Fact]
    public async Task MigrationPersistsAuditAndConcurrentReviewHasOneWinner()
    {
        var database = $"AiMentorOpsTest_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString,
            $"CREATE DATABASE [{database}] COLLATE Latin1_General_100_CI_AS;");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var root = FindRepositoryRoot();
            await ApplyMigrationsAsync(connectionString, root, "010_operations_actions.sql",
                "011_operations_action_ordinal_identifiers.sql");
            var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
            var clock = new FixedTimeProvider(now);
            var options = new SqlServerWorkflowOptions { ConnectionString = connectionString, InitializeSchema = false };
            using var first = new SqlServerOperationsActionStore(options, clock);
            using var second = new SqlServerOperationsActionStore(options, clock);
            var record = new OperationsActionRecord("action-sql", "tenant-a", "approval", "approval-1", "approve",
                "\"etag\"", new string('A', 64), new string('B', 64), "operator-a", new string('C', 64),
                OperationsActionStatus.AwaitingReview, 1, now, now.AddMinutes(15));
            Assert.Equal(OperationsActionCreateStatus.Created, (await first.CreateAsync(record, 100)).Status);
            var reconstructed = await second.GetByIdempotencyAsync("tenant-a", new string('B', 64));
            Assert.NotNull(reconstructed);

            var reviews = await Task.WhenAll(
                first.TryAcquireReviewAsync("tenant-a", record.Id, 1, "reviewer-a", true,
                    new string('D', 64), now),
                second.TryAcquireReviewAsync("tenant-a", record.Id, 1, "reviewer-b", true,
                    new string('E', 64), now));
            var winner = Assert.Single(reviews, item => item.Status == OperationsActionAcquireStatus.Acquired);
            Assert.Single(reviews, item => item.Status == OperationsActionAcquireStatus.VersionConflict);
            await second.CompleteAsync("tenant-a", record.Id, winner.Record!.Version,
                OperationsActionStatus.Completed, "OPERATIONS_APPROVAL_APPROVED", now);

            var crashed = record with
            {
                Id = "action-crashed",
                TargetId = "approval-2",
                RequestFingerprint = new string('F', 64),
                IdempotencyHash = new string('0', 64)
            };
            Assert.Equal(OperationsActionCreateStatus.Created, (await first.CreateAsync(crashed, 100)).Status);
            Assert.Equal(OperationsActionAcquireStatus.Acquired,
                (await first.TryAcquireReviewAsync("tenant-a", crashed.Id, 1, "reviewer-c", true,
                    new string('1', 64), now)).Status);
            clock.Advance(TimeSpan.FromMinutes(16));

            using var afterRestart = new SqlServerOperationsActionStore(options, clock);
            var rows = await afterRestart.ListAsync("tenant-a", 10);
            var persisted = Assert.Single(rows, item => item.Id == record.Id);
            var frozen = Assert.Single(rows, item => item.Id == crashed.Id);
            Assert.Equal(OperationsActionStatus.Completed, persisted.Status);
            Assert.Equal("OPERATIONS_APPROVAL_APPROVED", persisted.OutcomeCode);
            Assert.Equal(64, persisted.RequestReasonHash.Length);
            Assert.Equal(64, persisted.ReviewReasonHash!.Length);
            Assert.Equal(OperationsActionStatus.OutcomeUnknown, frozen.Status);
            Assert.Equal("OPERATIONS_ACTION_EXECUTION_EXPIRED_OUTCOME_UNKNOWN", frozen.OutcomeCode);
            Assert.Empty(await afterRestart.ListAsync("tenant-b", 10));
        }
        finally
        {
            await ExecuteAsync(master.ConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }

    [Fact]
    public async Task RuntimeSchemaKeepsCaseDistinctTenantAndResourceIdentifiers()
    {
        var database = $"AiMentorOpsOrdinal_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString,
            $"CREATE DATABASE [{database}] COLLATE Latin1_General_100_CI_AS;");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
            var clock = new FixedTimeProvider(now);
            var options = new SqlServerWorkflowOptions { ConnectionString = connectionString, InitializeSchema = true };
            using var store = new SqlServerOperationsActionStore(options, clock);
            var idempotencyHash = new string('B', 64);
            var upper = Record("Action-Case", "Tenant-A", "Approval", "Approval-1", idempotencyHash, 'A', now);
            var lower = Record("action-case", "tenant-a", "approval", "approval-1", idempotencyHash, 'F', now);

            Assert.Equal(OperationsActionCreateStatus.Created, (await store.CreateAsync(upper, 100)).Status);
            Assert.Equal(OperationsActionCreateStatus.Created, (await store.CreateAsync(lower, 100)).Status);

            Assert.Equal(upper.Id, Assert.Single(await store.ListAsync("Tenant-A", 10)).Id);
            Assert.Equal(lower.Id, Assert.Single(await store.ListAsync("tenant-a", 10)).Id);
            Assert.Empty(await store.ListAsync("TENANT-A", 10));
            Assert.Null(await store.GetAsync("Tenant-A", lower.Id));
            Assert.Null(await store.GetByIdempotencyAsync("TENANT-A", idempotencyHash));
            Assert.Equal(OperationsActionAcquireStatus.NotFound,
                (await store.TryAcquireReviewAsync("tenant-a", upper.Id, 1, "reviewer", true,
                    new string('D', 64), now)).Status);

            await AssertOrdinalIdentifierCollationsAsync(connectionString);
        }
        finally
        {
            await ExecuteAsync(master.ConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }

    [Fact]
    public async Task OrdinalMigrationFailsClosedOnTenantAliasesThenUpgradesWithoutDataLoss()
    {
        var database = $"AiMentorOpsMigration_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString,
            $"CREATE DATABASE [{database}] COLLATE Latin1_General_100_CI_AS;");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var root = FindRepositoryRoot();
            await ApplyMigrationsAsync(connectionString, root, "010_operations_actions.sql");
            await ExecuteAsync(connectionString, """
                INSERT dbo.AiMentorOperationsActions
                    (Id,TenantId,TargetType,TargetId,Action,TargetETag,RequestFingerprint,IdempotencyHash,
                     RequesterSubjectId,RequestReasonHash,Status,Version,CreatedAt,ExpiresAt)
                VALUES
                    (N'action-upper',N'Tenant-A',N'approval',N'approval-1',N'approve',N'"etag"',
                     REPLICATE('A',64),REPLICATE('B',64),N'operator-a',REPLICATE('C',64),0,1,
                     '2026-07-15T10:00:00+00:00','2026-07-15T10:15:00+00:00'),
                    (N'action-lower',N'tenant-a',N'approval',N'approval-2',N'approve',N'"etag"',
                     REPLICATE('D',64),REPLICATE('E',64),N'operator-b',REPLICATE('F',64),0,1,
                     '2026-07-15T10:00:00+00:00','2026-07-15T10:15:00+00:00');
                """);

            var migration = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "sql",
                "011_operations_action_ordinal_identifiers.sql"));
            var conflict = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connectionString, migration));

            Assert.Contains(conflict.Errors.Cast<SqlError>(), error => error.Number == 51011);
            Assert.Equal(2L, await ScalarInt64Async(connectionString,
                "SELECT COUNT_BIG(1) FROM dbo.AiMentorOperationsActions;"));
            Assert.NotEqual("Latin1_General_100_BIN2", await ScalarStringAsync(connectionString, """
                SELECT collation_name FROM sys.columns
                WHERE object_id=OBJECT_ID(N'dbo.AiMentorOperationsActions') AND name=N'TenantId';
                """));

            await ExecuteAsync(connectionString,
                "DELETE dbo.AiMentorOperationsActions WHERE Id=N'action-lower';");
            await ExecuteAsync(connectionString, migration);
            await ExecuteAsync(connectionString, migration);

            Assert.Equal(1L, await ScalarInt64Async(connectionString,
                "SELECT COUNT_BIG(1) FROM dbo.AiMentorOperationsActions;"));
            Assert.Equal("Tenant-A", await ScalarStringAsync(connectionString,
                "SELECT TenantId FROM dbo.AiMentorOperationsActions WHERE Id=N'action-upper';"));
            Assert.Equal("Latin1_General_100_BIN2", await ScalarStringAsync(connectionString, """
                SELECT collation_name FROM sys.columns
                WHERE object_id=OBJECT_ID(N'dbo.AiMentorOperationsActions') AND name=N'TenantId';
                """));
            await AssertOrdinalIdentifierCollationsAsync(connectionString);
            Assert.Equal(4L, await ScalarInt64Async(connectionString, """
                SELECT COUNT_BIG(1)
                FROM sys.indexes
                WHERE object_id=OBJECT_ID(N'dbo.AiMentorOperationsActions')
                  AND name IN (N'PK_AiMentorOperationsActions',N'UX_AiMentorOperationsActions_Idempotency',
                    N'IX_AiMentorOperationsActions_Target',N'IX_AiMentorOperationsActions_Queue');
                """));
        }
        finally
        {
            await ExecuteAsync(master.ConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }

    [Fact]
    public async Task CapacityIsTenantScopedAndUnknownOutcomeArchivesWithoutUnfreezingTarget()
    {
        var database = $"AiMentorOpsRetention_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString,
            $"CREATE DATABASE [{database}] COLLATE Latin1_General_100_CI_AS;");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var root = FindRepositoryRoot();
            await ApplyMigrationsAsync(connectionString, root, "010_operations_actions.sql",
                "011_operations_action_ordinal_identifiers.sql");
            var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
            var clock = new FixedTimeProvider(now);
            var sqlOptions = new SqlServerWorkflowOptions
            {
                ConnectionString = connectionString,
                InitializeSchema = false
            };
            var actionOptions = new OperationsActionOptions
            {
                MaximumEntries = 1,
                MaximumAuditEntriesPerTenant = 2,
                OutcomeUnknownRetention = TimeSpan.FromMinutes(1)
            };
            using var store = new SqlServerOperationsActionStore(sqlOptions, clock, actionOptions);
            var first = Record("unknown-a", "tenant-a", "approval", "target-a", new string('B', 64), 'A', now);
            var otherTenant = Record("pending-b", "tenant-b", "approval", "target-b", new string('G', 64), 'F', now);
            Assert.Equal(OperationsActionCreateStatus.Created, (await store.CreateAsync(first, 1)).Status);
            Assert.Equal(OperationsActionCreateStatus.Created, (await store.CreateAsync(otherTenant, 1)).Status);
            var acquired = await store.TryAcquireReviewAsync("tenant-a", first.Id, 1, "reviewer", true,
                new string('D', 64), now);
            await store.CompleteAsync("tenant-a", first.Id, acquired.Record!.Version,
                OperationsActionStatus.OutcomeUnknown, "OPERATIONS_ACTION_OUTCOME_UNKNOWN", now);
            var second = Record("pending-a", "tenant-a", "approval", "target-a-2", new string('L', 64), 'K', now);
            Assert.Equal(OperationsActionCreateStatus.Created, (await store.CreateAsync(second, 1)).Status);

            clock.Advance(TimeSpan.FromMinutes(2));
            var archived = Assert.Single(await store.ListAsync("tenant-a", 10), item => item.Id == first.Id);
            var taskState = Assert.Single(await store.ListTaskStateAsync("tenant-a"), item => item.Id == first.Id);
            var sameTarget = Record("replay-a", "tenant-a", "approval", "target-a", new string('Q', 64), 'P',
                clock.GetUtcNow());

            Assert.Equal(OperationsActionStatus.OutcomeUnknownArchived, archived.Status);
            Assert.Equal("OPERATIONS_ACTION_OUTCOME_UNKNOWN", archived.OutcomeCode);
            Assert.Equal(OperationsActionStatus.OutcomeUnknownArchived, taskState.Status);
            Assert.Equal(OperationsActionCreateStatus.TargetBusy,
                (await store.CreateAsync(sameTarget, 10)).Status);
            var capacity = Record("capacity-a", "tenant-a", "approval", "target-a-3", new string('V', 64),
                'U', clock.GetUtcNow());
            Assert.Equal(OperationsActionCreateStatus.Capacity,
                (await store.CreateAsync(capacity, 10)).Status);
        }
        finally
        {
            await ExecuteAsync(master.ConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
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
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            Encrypt = false
        };
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ApplyMigrationsAsync(string connectionString, string root,
        params string[] migrationNames)
    {
        foreach (var migrationName in migrationNames)
            await ExecuteAsync(connectionString,
                await File.ReadAllTextAsync(Path.Combine(root, "deploy", "sql", migrationName)));
    }

    private static async Task<long> ScalarInt64Async(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task<IReadOnlyList<string>> QueryStringsAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task AssertOrdinalIdentifierCollationsAsync(string connectionString)
    {
        var collations = await QueryStringsAsync(connectionString, """
            SELECT c.name + N':' + c.collation_name
            FROM sys.columns c
            WHERE c.object_id=OBJECT_ID(N'dbo.AiMentorOperationsActions')
              AND c.name IN (N'Id',N'TenantId',N'TargetType',N'TargetId',N'Action',N'TargetETag',
                N'RequestFingerprint',N'IdempotencyHash',N'RequesterSubjectId',N'RequestReasonHash',
                N'ReviewerSubjectId',N'ReviewReasonHash',N'OutcomeCode')
            ORDER BY c.name;
            """);
        Assert.Equal(13, collations.Count);
        Assert.All(collations,
            value => Assert.EndsWith(":Latin1_General_100_BIN2", value, StringComparison.Ordinal));
    }

    private static OperationsActionRecord Record(string id, string tenantId, string targetType, string targetId,
        string idempotencyHash, char marker, DateTimeOffset now) => new(id, tenantId, targetType, targetId, "approve",
        "\"etag\"", new string(marker, 64), idempotencyHash, $"operator-{marker}", new string(marker, 64),
        OperationsActionStatus.AwaitingReview, 1, now, now.AddMinutes(15));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AiMentor.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 AiMentor 仓库根目录。");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
