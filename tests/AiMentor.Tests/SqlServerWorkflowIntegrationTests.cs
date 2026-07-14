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
            var clock = TimeProvider.System;
            var trace = new InMemoryTraceSink();
            var tool = new MutationTool();
            var registry = new ServerToolRegistry([tool]);
            var safety = new RuleBasedToolInvocationSafetyService(new ToolSafetyOptions
            {
                AllowedTools = new HashSet<string>([tool.Descriptor.Name], StringComparer.OrdinalIgnoreCase)
            });
            var sqlOptions = new SqlServerWorkflowOptions { ConnectionString = testConnection };
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

            using var checkpoints = new SqlServerAgentRunCheckpointStore(sqlOptions,
                new AesGcmMemoryCipher(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), clock);
            var checkpoint = CreateCheckpoint(requester, arguments, clock.GetUtcNow());
            await checkpoints.SavePendingAsync(checkpoint);
            var leases = await Task.WhenAll(
                checkpoints.TryAcquireAsync(checkpoint.RunId, requester, "node-a", TimeSpan.FromSeconds(30)),
                checkpoints.TryAcquireAsync(checkpoint.RunId, requester, "node-b", TimeSpan.FromSeconds(30)));
            var rawPayload = await ReadPayloadAsync(testConnection, checkpoint.RunId);

            Assert.Single(consumptions, result => result.Allowed);
            Assert.Single(consumptions, result => result.Decision.Code == "TOOL_APPROVAL_ALREADY_CONSUMED");
            Assert.Single(leases, result => result.Status == AgentRunLeaseStatus.Acquired);
            Assert.Single(leases, result => result.Status == AgentRunLeaseStatus.Busy);
            Assert.DoesNotContain("secret-memory-id", rawPayload, StringComparison.Ordinal);
            Assert.DoesNotContain("memoryId", rawPayload, StringComparison.Ordinal);
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

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadPayloadAsync(string connectionString, string runId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PayloadCipher FROM dbo.AiMentorAgentRuns WHERE RunId=@runId;";
        command.Parameters.AddWithValue("@runId", runId);
        return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
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
}
