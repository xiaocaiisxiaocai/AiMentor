using System.Data;
using AiMentor.Application;
using AiMentor.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Sdk;

namespace AiMentor.Tests;

public sealed class SqlServerOperationsActionStoreTests
{
    [Fact]
    public async Task MigrationPersistsAuditAndConcurrentReviewHasOneWinner()
    {
        var database = $"AiMentorOpsTest_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var root = FindRepositoryRoot();
            await ExecuteAsync(connectionString,
                await File.ReadAllTextAsync(Path.Combine(root, "deploy", "sql", "010_operations_actions.sql")));
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
