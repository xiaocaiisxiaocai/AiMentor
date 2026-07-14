using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace AiMentor.Tests;

public sealed class SqlServerWorkflowIntegrationTests
{
    [Fact]
    public async Task SqlServerShouldConsumeApprovalOnceLeaseOnceAndEncryptCheckpointPayload()
    {
        if (!OperatingSystem.IsWindows()) return;

        var databaseName = $"AiMentorWorkflowTest_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            Encrypt = false
        };
        await CreateDatabaseAsync(master.ConnectionString, databaseName);
        var testConnection = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            var repositoryRoot = FindRepositoryRoot();
            await ExecuteScriptAsync(testConnection, Path.Combine(repositoryRoot, "deploy", "sql", "001_workflow.sql"));
            await ExecuteScriptAsync(testConnection, Path.Combine(repositoryRoot, "deploy", "sql", "002_workflow_key_version.sql"));
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 14, 1, 0, 0, TimeSpan.Zero));
            var trace = new InMemoryTraceSink();
            var tool = new MutationTool();
            var registry = new ServerToolRegistry([tool]);
            var safety = new RuleBasedToolInvocationSafetyService(new ToolSafetyOptions
            {
                AllowedTools = new HashSet<string>([tool.Descriptor.Name], StringComparer.OrdinalIgnoreCase)
            });
            // 关闭运行时建表，确保测试覆盖 Production 使用迁移脚本的路径，而不只覆盖开发降级路径。
            var sqlOptions = new SqlServerWorkflowOptions { ConnectionString = testConnection, InitializeSchema = false };
            using var approvals = new SqlServerToolApprovalService(registry, safety, trace,
                new ToolApprovalOptions(), sqlOptions, clock);
            var requester = AccessContext.Create("tenant-a", "requester-a", ["readers"]);
            var approver = AccessContext.Create("tenant-a", "approver-a", ["tool-approvers"]);
            var arguments = JsonSerializer.SerializeToElement(new { memoryId = "secret-memory-id", expectedVersion = 7 });
            var requested = await approvals.RequestAsync(tool.Descriptor.Name, arguments, "集成测试", requester);
            await approvals.DecideAsync(requested.Id, true, "批准集成测试", approver);

            var consumptions = await Task.WhenAll(
                approvals.ConsumeAsync(requested.Id, tool.Descriptor.Name, arguments, requester),
                approvals.ConsumeAsync(requested.Id, tool.Descriptor.Name, arguments, requester));

            var workflowKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var workflowCipher = new AesGcmWorkflowStateCipher("v1",
                new Dictionary<string, byte[]> { ["v1"] = workflowKey });
            using var checkpoints = new SqlServerAgentRunCheckpointStore(sqlOptions, workflowCipher, clock);
            var checkpoint = CreateCheckpoint(requester, arguments, clock.GetUtcNow());
            await checkpoints.SavePendingAsync(checkpoint);
            var leases = await Task.WhenAll(
                checkpoints.TryAcquireAsync(checkpoint.RunId, requester, "node-a", TimeSpan.FromSeconds(30)),
                checkpoints.TryAcquireAsync(checkpoint.RunId, requester, "node-b", TimeSpan.FromSeconds(30)));
            var beforeRotation = await ReadPayloadAsync(testConnection, checkpoint.RunId);
            var crashedLease = Assert.Single(leases, result => result.Status == AgentRunLeaseStatus.Acquired);

            // 不释放实例 A 的租约来模拟进程强杀；只有租约过期后实例 B 才能用新活动密钥接管。
            clock.Advance(TimeSpan.FromSeconds(31));
            var nextKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var rotatedCipher = new AesGcmWorkflowStateCipher("v2", new Dictionary<string, byte[]>
            {
                ["v1"] = workflowKey,
                ["v2"] = nextKey
            });
            using var rotatedStore = new SqlServerAgentRunCheckpointStore(sqlOptions, rotatedCipher, clock);
            var takeover = await rotatedStore.TryAcquireAsync(checkpoint.RunId, requester, "node-c",
                TimeSpan.FromSeconds(30));
            var afterRotation = await ReadPayloadAsync(testConnection, checkpoint.RunId);
            await rotatedStore.ReleaseAsync(checkpoint.RunId, takeover.LeaseToken!);

            using var newKeyOnlyStore = new SqlServerAgentRunCheckpointStore(sqlOptions,
                new AesGcmWorkflowStateCipher("v2", new Dictionary<string, byte[]> { ["v2"] = nextKey }), clock);
            var newKeyOnly = await newKeyOnlyStore.TryAcquireAsync(checkpoint.RunId, requester, "node-d",
                TimeSpan.FromSeconds(30));
            await newKeyOnlyStore.CompleteAsync(checkpoint.RunId, newKeyOnly.LeaseToken!);

            Assert.Single(consumptions, result => result.Allowed);
            Assert.Single(consumptions, result => result.Decision.Code == "TOOL_APPROVAL_ALREADY_CONSUMED");
            Assert.Single(leases, result => result.Status == AgentRunLeaseStatus.Acquired);
            Assert.Single(leases, result => result.Status == AgentRunLeaseStatus.Busy);
            Assert.NotNull(crashedLease.LeaseToken);
            Assert.Equal("v1", beforeRotation.KeyVersion);
            Assert.Equal(AgentRunLeaseStatus.Acquired, takeover.Status);
            Assert.Equal("v2", afterRotation.KeyVersion);
            Assert.NotEqual(beforeRotation.Payload, afterRotation.Payload);
            Assert.Equal(AgentRunLeaseStatus.Acquired, newKeyOnly.Status);
            Assert.DoesNotContain("secret-memory-id", afterRotation.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("memoryId", afterRotation.Payload, StringComparison.Ordinal);
        }
        finally
        {
            await DropDatabaseAsync(master.ConnectionString, databaseName);
        }
    }

    private static AgentRunCheckpoint CreateCheckpoint(AccessContext access, JsonElement arguments,
        DateTimeOffset now) => new(
        "sql-run-1", access, "approval-1", "framework-1", "call-1", "memory_delete",
        new Dictionary<string, object?> { ["arguments"] = arguments },
        JsonSerializer.SerializeToElement(new { history = "sensitive-session" }), [], [], [], 0,
        new AgentApprovalCheckpoint("approval-1", "memory.delete", ToolOperationRisk.Mutation, ["memoryId"],
            now, now.AddMinutes(15)), now, now.AddMinutes(15));

    private static async Task CreateDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}];";
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

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string? KeyVersion, string Payload)> ReadPayloadAsync(string connectionString,
        string runId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT KeyVersion,PayloadCipher FROM dbo.AiMentorAgentRuns WHERE RunId=@runId;";
        command.Parameters.AddWithValue("@runId", runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1));
    }

    private sealed class MutationTool : IServerTool
    {
        public ToolDescriptor Descriptor { get; } = new("memory.delete", "删除指定记忆。",
            ToolOperationRisk.Mutation, TimeSpan.FromSeconds(1), 4_096, true);

        public SafetyDecision ValidateArguments(JsonElement arguments) =>
            arguments.TryGetProperty("memoryId", out _) && arguments.TryGetProperty("expectedVersion", out _)
                ? SafetyDecision.Allowed
                : new SafetyDecision(SafetyAction.Refuse, "ARGUMENTS_INVALID", "参数无效。");

        public Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
