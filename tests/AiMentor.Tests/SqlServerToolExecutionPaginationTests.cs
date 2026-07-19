using System.Security.Cryptography;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Sdk;

namespace AiMentor.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class SqlServerToolExecutionPaginationTests
{
    [Fact]
    public async Task KeysetCursorShouldReturnMoreThanOneHundredEqualTimestampRowsWithoutGapOrDuplicate()
    {
        var databaseName = $"AiMentorExecutionPageTest_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await CreateDatabaseAsync(master.ConnectionString, databaseName);
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            var root = FindRepositoryRoot();
            await ExecuteScriptAsync(connectionString, Path.Combine(root, "deploy", "sql",
                "003_tool_execution_ledger.sql"));
            await ExecuteScriptAsync(connectionString, Path.Combine(root, "deploy", "sql",
                "004_tool_execution_reconciliation.sql"));
            await InsertOutcomeUnknownRowsAsync(connectionString);
            var key = RandomNumberGenerator.GetBytes(32);
            var cipher = new AesGcmWorkflowStateCipher("v1", new Dictionary<string, byte[]> { ["v1"] = key });
            using var ledger = new SqlServerToolExecutionLedger(new SqlServerWorkflowOptions
            {
                ConnectionString = connectionString,
                InitializeSchema = false
            }, cipher, TimeProvider.System);
            var options = new ToolExecutionReconciliationOptions
            {
                MaximumPageSize = 100,
                MaximumOperationsScan = 500,
                CursorSigningKey = RandomNumberGenerator.GetBytes(32)
            };
            var service = new ToolExecutionReconciliationService(
                ledger, new InMemoryTraceSink(), options, TimeProvider.System, []);
            var access = AccessContext.Create("tenant-a", "reconciler-a", ["tool-reconcilers"]);

            var records = new List<OutcomeUnknownToolExecution>();
            string? cursor = null;
            do
            {
                var page = await service.ListOutcomeUnknownPageAsync(access, 100, cursor);
                records.AddRange(page.Items);
                cursor = page.NextCursor;
            } while (cursor is not null);

            var executionKeys = records.Select(item => item.ExecutionKey).ToArray();
            Assert.Equal(205, executionKeys.Length);
            Assert.Equal(205, executionKeys.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(executionKeys.Order(StringComparer.Ordinal), executionKeys);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await DropDatabaseAsync(master.ConnectionString, databaseName);
        }
    }

    private static async Task InsertOutcomeUnknownRowsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ;WITH Numbers AS
            (
                SELECT 0 AS Number
                UNION ALL
                SELECT Number + 1 FROM Numbers WHERE Number < 204
            )
            INSERT dbo.AiMentorToolExecutions
                (ExecutionKey,RequestFingerprint,RunId,Status,LeaseToken,LeaseExpiresAt,
                 CreatedAt,UpdatedAt,TenantId,SubjectId,ToolName)
            SELECT CONVERT(char(64),HASHBYTES('SHA2_256',CONCAT('execution-',Number)),2),
                   CONVERT(char(64),HASHBYTES('SHA2_256',CONCAT('fingerprint-',Number)),2),
                   CONCAT('run-',Number),3,N'',@updatedAt,@updatedAt,@updatedAt,
                   N'tenant-a',N'owner-a',N'memory.delete'
            FROM Numbers
            OPTION (MAXRECURSION 205);
            """;
        command.Parameters.AddWithValue("@updatedAt",
            new DateTimeOffset(2026, 7, 16, 3, 0, 0, TimeSpan.Zero));
        Assert.Equal(205, await command.ExecuteNonQueryAsync());
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

    private static async Task CreateDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteScriptAsync(string connectionString, string scriptPath)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(scriptPath);
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
}
