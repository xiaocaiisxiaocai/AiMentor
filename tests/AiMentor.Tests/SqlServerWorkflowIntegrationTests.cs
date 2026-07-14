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
            await ExecuteScriptAsync(testConnection, Path.Combine(repositoryRoot, "deploy", "sql", "003_tool_execution_ledger.sql"));
            await ExecuteScriptAsync(testConnection, Path.Combine(repositoryRoot, "deploy", "sql", "004_tool_execution_reconciliation.sql"));
            await ExecuteScriptAsync(testConnection, Path.Combine(repositoryRoot, "deploy", "sql", "005_tool_reconciliation_reviews.sql"));
            await ExecuteScriptAsync(testConnection, Path.Combine(repositoryRoot, "deploy", "sql", "006_agent_run_cancellation.sql"));
            await ExecuteScriptAsync(testConnection, Path.Combine(repositoryRoot, "deploy", "sql", "007_tool_compensations.sql"));
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
            var crashedOwner = leases[0].Status == AgentRunLeaseStatus.Acquired ? "node-a" : "node-b";

            // 当前租约持有者可以延长租约，错误实例和错误令牌均不能阻止或冒充续租。
            clock.Advance(TimeSpan.FromSeconds(20));
            var wrongRenewal = await checkpoints.RenewAsync(checkpoint.RunId, "wrong-token", crashedOwner,
                TimeSpan.FromSeconds(30));
            var renewed = await checkpoints.RenewAsync(checkpoint.RunId, crashedLease.LeaseToken!, crashedOwner,
                TimeSpan.FromSeconds(30));
            clock.Advance(TimeSpan.FromSeconds(11));
            var protectedByRenewal = await checkpoints.TryAcquireAsync(checkpoint.RunId, requester, "node-c",
                TimeSpan.FromSeconds(30));

            // 续租后的实例再失联，只有新租约也过期后其他实例才能用新活动密钥接管。
            clock.Advance(TimeSpan.FromSeconds(20));
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

            using var executionLedger = new SqlServerToolExecutionLedger(sqlOptions, rotatedCipher, clock);
            var executionKey = new string('A', 64);
            var fingerprint = JsonArgumentFingerprint.Create("memory.delete", arguments, requester);
            var executing = await executionLedger.TryAcquireAsync(new ToolExecutionLedgerRequest(executionKey,
                    fingerprint, "tool-run-1", "tenant-a", "requester-a", "memory.delete"),
                TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
            await executionLedger.MarkExecutingAsync(executionKey, executing.LeaseToken!);
            clock.Advance(TimeSpan.FromSeconds(31));
            var outcomeUnknown = await executionLedger.TryAcquireAsync(new ToolExecutionLedgerRequest(executionKey,
                    fingerprint, "tool-run-2", "tenant-a", "requester-a", "memory.delete"),
                TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
            var tenantUnknown = await executionLedger.ListOutcomeUnknownAsync("tenant-a", 50);
            var otherTenantUnknown = await executionLedger.ListOutcomeUnknownAsync("tenant-b", 50);
            var reconciliation = new ToolExecutionReconciliationService(executionLedger, trace,
                new ToolExecutionReconciliationOptions(), clock,
                [new MemoryDeleteOutcomeProbe(new InMemoryMemoryStore(), clock)]);
            var sqlProbe = await reconciliation.ProbeOutcomeAsync(
                AccessContext.Create("tenant-a", "reconciler-a", ["tool-reconcilers"]), executionKey, arguments);
            var firstReview = await reconciliation.ReviewOutcomeAsync(
                AccessContext.Create("tenant-a", "reconciler-a", ["tool-reconcilers"]), executionKey,
                arguments, true, "已核对目标状态");
            var secondReview = await reconciliation.ReviewOutcomeAsync(
                AccessContext.Create("tenant-a", "reconciler-b", ["tool-reconcilers"]), executionKey,
                arguments, true, "独立复核目标状态");
            var reconciledRetry = await executionLedger.TryAcquireAsync(new ToolExecutionLedgerRequest(executionKey,
                    fingerprint, "tool-run-reconciled", "tenant-a", "requester-a", "memory.delete"),
                TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);

            var completedKey = new string('C', 64);
            using var oldExecutionLedger = new SqlServerToolExecutionLedger(sqlOptions, workflowCipher, clock);
            var completed = await oldExecutionLedger.TryAcquireAsync(new ToolExecutionLedgerRequest(completedKey,
                    new string('D', 64), "tool-run-3", "tenant-a", "requester-a", "memory.delete"),
                TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
            await oldExecutionLedger.MarkExecutingAsync(completedKey, completed.LeaseToken!);
            await oldExecutionLedger.CompleteAsync(completedKey, completed.LeaseToken!, new ToolExecutionResult(
                "tool-run-3", "memory.delete", ToolExecutionStatus.Completed,
                JsonSerializer.SerializeToElement(new { deleted = true, secret = "sensitive-result" }),
                SafetyDecision.Allowed, false,
                [new TraceStep("tool.completed", "ok", clock.GetUtcNow(),
                    new Dictionary<string, object?> { ["code"] = "TOOL_EXECUTION_SAFE" })]));
            using var rotatingReplayLedger = new SqlServerToolExecutionLedger(sqlOptions, rotatedCipher, clock);
            var rotatingReplay = await rotatingReplayLedger.TryAcquireAsync(new ToolExecutionLedgerRequest(completedKey,
                    new string('D', 64), "tool-run-4", "tenant-a", "requester-a", "memory.delete"),
                TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
            using var replayLedger = new SqlServerToolExecutionLedger(sqlOptions,
                new AesGcmWorkflowStateCipher("v2", new Dictionary<string, byte[]> { ["v2"] = nextKey }), clock);
            var replay = await replayLedger.TryAcquireAsync(new ToolExecutionLedgerRequest(completedKey,
                    new string('D', 64), "tool-run-5", "tenant-a", "requester-a", "memory.delete"),
                TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
            var rawExecutionResult = await ReadExecutionResultAsync(testConnection, completedKey);

            // 使用两个服务实例和两代密钥贯通补偿，证明审批、密文轮换和反向幂等不依赖进程内状态。
            var reversibleTool = new ReversibleMutationTool();
            var compensationRegistry = new ServerToolRegistry([reversibleTool]);
            using var compensationV1 = new SqlServerToolCompensationService(compensationRegistry, workflowCipher,
                trace, new ToolCompensationOptions(), sqlOptions, clock);
            var compensationArguments = JsonSerializer.SerializeToElement(new { value = "updated-value" });
            var preparation = await compensationV1.PrepareForwardAsync(new string('E', 64), reversibleTool,
                new ToolExecutionContext(requester, "forward-compensation-run"), compensationArguments);
            await reversibleTool.ExecuteAsync(new ToolExecutionContext(requester, "forward-compensation-run"),
                compensationArguments);
            await compensationV1.ActivateAsync(preparation);
            var rawCompensationBefore = await ReadCompensationAsync(testConnection, preparation.Id);
            var compensationPending = await compensationV1.RequestApprovalAsync(preparation.Id,
                "恢复补偿前状态", requester);
            using var compensationV2 = new SqlServerToolCompensationService(compensationRegistry, rotatedCipher,
                trace, new ToolCompensationOptions(), sqlOptions, clock);
            await compensationV2.DecideAsync(preparation.Id, compensationPending.ApprovalId!, true,
                "secret-decision-reason", approver);
            var compensationCompleted = await compensationV2.ExecuteAsync(preparation.Id,
                compensationPending.ApprovalId!, "independent-reverse-key", requester);
            var compensationReplay = await compensationV1.ExecuteAsync(preparation.Id,
                compensationPending.ApprovalId!, "independent-reverse-key", requester);
            var rawCompensationAfter = await ReadCompensationAsync(testConnection, preparation.Id);

            // 反向工具开始后让租约过期；第二实例只能冻结结果不确定，不能自动接管或重放。
            var blockingTool = new BlockingCompensableTool();
            var compensationBarrier = new RecordingExecutionBarrier();
            var blockingRegistry = new ServerToolRegistry([blockingTool]);
            using var blockingServiceA = new SqlServerToolCompensationService(blockingRegistry, rotatedCipher,
                trace, new ToolCompensationOptions(), sqlOptions, clock, compensationBarrier);
            using var blockingServiceB = new SqlServerToolCompensationService(blockingRegistry, rotatedCipher,
                trace, new ToolCompensationOptions(), sqlOptions, clock);
            var blockingArguments = JsonSerializer.SerializeToElement(new { value = "after" });
            var blockingPreparation = await blockingServiceA.PrepareForwardAsync(new string('F', 64), blockingTool,
                new ToolExecutionContext(requester, "blocking-forward"), blockingArguments);
            await blockingTool.ExecuteAsync(new ToolExecutionContext(requester, "blocking-forward"),
                blockingArguments);
            await blockingServiceA.ActivateAsync(blockingPreparation);
            var blockingApproval = await blockingServiceA.RequestApprovalAsync(blockingPreparation.Id,
                "验证补偿租约过期", requester);
            await blockingServiceB.DecideAsync(blockingPreparation.Id, blockingApproval.ApprovalId!, true,
                "批准故障注入", approver);
            var blockedExecution = blockingServiceA.ExecuteAsync(blockingPreparation.Id,
                blockingApproval.ApprovalId!, "blocking-reverse-key", requester);
            var barrierSignal = await compensationBarrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("test.blocking.restore", barrierSignal.ToolName);
            Assert.Equal(0, blockingTool.CompensationCount);
            var concurrentExecution = await Assert.ThrowsAsync<ToolCompensationException>(() =>
                blockingServiceB.ExecuteAsync(blockingPreparation.Id, blockingApproval.ApprovalId!,
                    "blocking-reverse-key", requester));
            clock.Advance(TimeSpan.FromSeconds(46));
            var frozenSummary = Assert.Single(await blockingServiceB.ListAsync(
                    requester, ToolCompensationStatus.OutcomeUnknown),
                item => item.Id == blockingPreparation.Id);
            compensationBarrier.Release.TrySetResult();
            await blockingTool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            blockingTool.Release.TrySetResult();
            var lostLease = await Assert.ThrowsAsync<ToolCompensationException>(() => blockedExecution);
            var blockedRetry = await Assert.ThrowsAsync<ToolCompensationException>(() => blockingServiceB.ExecuteAsync(
                blockingPreparation.Id, blockingApproval.ApprovalId!, "blocking-reverse-key", requester));

            var cancellationCheckpoint = CreateCheckpoint(requester, arguments, clock.GetUtcNow()) with
            {
                RunId = "sql-run-cancel"
            };
            await checkpoints.SavePendingAsync(cancellationCheckpoint);
            var cancellationLease = await checkpoints.TryAcquireAsync(cancellationCheckpoint.RunId, requester,
                "node-cancel", TimeSpan.FromSeconds(30));
            var forbiddenCancellation = await checkpoints.RequestCancellationAsync(cancellationCheckpoint.RunId,
                AccessContext.Create("tenant-a", "other-user", ["readers"]), new string('E', 64));
            var requestedCancellation = await checkpoints.RequestCancellationAsync(cancellationCheckpoint.RunId,
                requester, new string('F', 64));
            var cancellationRenewal = await checkpoints.RenewAsync(cancellationCheckpoint.RunId,
                cancellationLease.LeaseToken!, "node-cancel", TimeSpan.FromSeconds(30));
            var cancellationCompletion = await checkpoints.CompleteAsync(cancellationCheckpoint.RunId,
                cancellationLease.LeaseToken!);
            var cancelledResume = await checkpoints.TryAcquireAsync(cancellationCheckpoint.RunId, requester,
                "node-after-cancel", TimeSpan.FromSeconds(30));

            Assert.Single(consumptions, result => result.Allowed);
            Assert.Single(consumptions, result => result.Decision.Code == "TOOL_APPROVAL_ALREADY_CONSUMED");
            Assert.Single(leases, result => result.Status == AgentRunLeaseStatus.Acquired);
            Assert.Single(leases, result => result.Status == AgentRunLeaseStatus.Busy);
            Assert.NotNull(crashedLease.LeaseToken);
            Assert.Equal(AgentRunLeaseRenewalStatus.LeaseLost, wrongRenewal);
            Assert.Equal(AgentRunLeaseRenewalStatus.Renewed, renewed);
            Assert.Equal(AgentRunLeaseStatus.Busy, protectedByRenewal.Status);
            Assert.Equal("v1", beforeRotation.KeyVersion);
            Assert.Equal(AgentRunLeaseStatus.Acquired, takeover.Status);
            Assert.Equal("v2", afterRotation.KeyVersion);
            Assert.NotEqual(beforeRotation.Payload, afterRotation.Payload);
            Assert.Equal(AgentRunLeaseStatus.Acquired, newKeyOnly.Status);
            Assert.DoesNotContain("secret-memory-id", afterRotation.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("memoryId", afterRotation.Payload, StringComparison.Ordinal);
            Assert.Equal(IdempotencyAcquireStatus.OutcomeUnknown, outcomeUnknown.Status);
            Assert.Equal("tenant-a", Assert.Single(tenantUnknown).TenantId);
            Assert.Empty(otherTenantUnknown);
            Assert.Equal(ToolOutcomeProbeState.Applied, sqlProbe.State);
            Assert.Equal(ToolReconciliationReviewStatus.AwaitingSecondReviewer, firstReview.Status);
            Assert.Equal(ToolReconciliationReviewStatus.ResolvedApplied, secondReview.Status);
            Assert.Equal(IdempotencyAcquireStatus.ReconciledApplied, reconciledRetry.Status);
            Assert.Equal(IdempotencyAcquireStatus.Replay, rotatingReplay.Status);
            Assert.Equal(IdempotencyAcquireStatus.Replay, replay.Status);
            Assert.Equal("tool-run-3", replay.ReplayResult!.RunId);
            Assert.Single(replay.ReplayResult.Trace);
            Assert.DoesNotContain("sensitive-result", rawExecutionResult, StringComparison.Ordinal);
            Assert.Equal("v1", rawCompensationBefore.KeyVersion);
            Assert.DoesNotContain("sensitive-before-value", rawCompensationBefore.SnapshotCipher,
                StringComparison.Ordinal);
            Assert.DoesNotContain("previousValue", rawCompensationBefore.SnapshotCipher, StringComparison.Ordinal);
            Assert.Equal(ToolCompensationStatus.Completed, compensationCompleted.Status);
            Assert.True(compensationReplay.IdempotentReplay);
            Assert.Equal(1, reversibleTool.CompensationCount);
            Assert.Equal("sensitive-before-value", reversibleTool.State);
            Assert.Equal("v2", rawCompensationAfter.KeyVersion);
            Assert.NotEqual(rawCompensationBefore.SnapshotCipher, rawCompensationAfter.SnapshotCipher);
            Assert.Equal(64, rawCompensationAfter.DecisionReasonHash?.Length);
            Assert.Equal(64, rawCompensationAfter.PreparationTokenHash.Length);
            Assert.Equal((byte)ToolCompensationStatus.Completed, rawCompensationAfter.Status);
            Assert.Equal(ToolCompensationStatus.OutcomeUnknown, frozenSummary.Status);
            Assert.Equal("TOOL_COMPENSATION_EXECUTION_IN_PROGRESS", concurrentExecution.Code);
            Assert.Equal("TOOL_COMPENSATION_LEASE_LOST", lostLease.Code);
            Assert.Equal("TOOL_COMPENSATION_OUTCOME_UNKNOWN", blockedRetry.Code);
            Assert.Equal(1, blockingTool.CompensationCount);
            Assert.Equal(AgentRunCancellationStatus.Forbidden, forbiddenCancellation.Status);
            Assert.Equal(AgentRunCancellationStatus.Requested, requestedCancellation.Status);
            Assert.Equal(AgentRunLeaseRenewalStatus.CancellationRequested, cancellationRenewal);
            Assert.Equal(AgentRunLeaseTransitionStatus.CancellationRequested, cancellationCompletion);
            Assert.Equal(AgentRunLeaseStatus.Cancelled, cancelledResume.Status);

            // 删除迁移创建的补偿表后启用开发初始化，验证运行时建表路径与 007 契约保持一致。
            await DropCompensationTableAsync(testConnection);
            var runtimeSchemaOptions = new SqlServerWorkflowOptions
            {
                ConnectionString = testConnection,
                InitializeSchema = true
            };
            using var runtimeSchemaService = new SqlServerToolCompensationService(compensationRegistry,
                rotatedCipher, trace, new ToolCompensationOptions(), runtimeSchemaOptions, clock);
            Assert.Empty(await runtimeSchemaService.ListAsync(requester));
            Assert.True(await CompensationTableExistsAsync(testConnection));
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

    private static async Task<string> ReadExecutionResultAsync(string connectionString, string executionKey)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ResultCipher FROM dbo.AiMentorToolExecutions WHERE ExecutionKey=@key;";
        command.Parameters.AddWithValue("@key", executionKey);
        return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
    }

    private static async Task<(string KeyVersion, string SnapshotCipher, string? DecisionReasonHash,
        string PreparationTokenHash, byte Status)> ReadCompensationAsync(string connectionString, string id)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT KeyVersion,SnapshotCipher,DecisionReasonHash,PreparationTokenHash,Status
            FROM dbo.AiMentorToolCompensations WHERE Id=@id;
            """;
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3), reader.GetByte(4));
    }

    private static async Task DropCompensationTableAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE dbo.AiMentorToolCompensations;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> CompensationTableExistsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.AiMentorToolCompensations',N'U') IS NULL THEN 0 ELSE 1 END;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
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

    private sealed class ReversibleMutationTool : ICompensableServerTool
    {
        public string State { get; private set; } = "sensitive-before-value";
        public int CompensationCount { get; private set; }
        public string CompensationToolName => "test.state.restore";
        public ToolDescriptor Descriptor { get; } = new("test.state.update", "更新测试状态。",
            ToolOperationRisk.Mutation, TimeSpan.FromSeconds(1), 4_096, true);
        public SafetyDecision ValidateArguments(JsonElement arguments) => SafetyDecision.Allowed;
        public Task<JsonElement> CaptureCompensationStateAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { previousValue = State }));
        public Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            State = arguments.GetProperty("value").GetString()!;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { State }));
        }
        public Task<JsonElement> CompensateAsync(ToolExecutionContext context, JsonElement compensationState,
            CancellationToken cancellationToken = default)
        {
            State = compensationState.GetProperty("previousValue").GetString()!;
            CompensationCount++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { State }));
        }
    }

    private sealed class BlockingCompensableTool : ICompensableServerTool
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CompensationCount { get; private set; }
        public string CompensationToolName => "test.blocking.restore";
        public ToolDescriptor Descriptor { get; } = new("test.blocking.update", "阻塞补偿测试工具。",
            ToolOperationRisk.Mutation, TimeSpan.FromSeconds(1), 4_096, true);
        public SafetyDecision ValidateArguments(JsonElement arguments) => SafetyDecision.Allowed;
        public Task<JsonElement> CaptureCompensationStateAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { previousValue = "before" }));
        public Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { updated = true }));
        public async Task<JsonElement> CompensateAsync(ToolExecutionContext context, JsonElement compensationState,
            CancellationToken cancellationToken = default)
        {
            CompensationCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return JsonSerializer.SerializeToElement(new { restored = true });
        }
    }

    /// <summary>在 SQL 已提交 Executing 后阻塞，证明反向工具调用前存在可观察的强杀窗口。</summary>
    private sealed class RecordingExecutionBarrier : IToolExecutionBarrier
    {
        public TaskCompletionSource<(string ExecutionKey, string ToolName)> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAfterExecutingAsync(string executionKey, string toolName,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult((executionKey, toolName));
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
