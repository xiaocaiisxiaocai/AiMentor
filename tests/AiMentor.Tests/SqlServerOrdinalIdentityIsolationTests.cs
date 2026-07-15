using System.Security.Cryptography;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Sdk;

namespace AiMentor.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class SqlServerOrdinalIdentityIsolationTests
{
    [Fact]
    public async Task LegacyCaseInsensitiveSchemaDoesNotLeakTasksOrDecryptAtlasAcrossCaseAliases()
    {
        var database = $"AiMentorOrdinalIdentity_{Guid.NewGuid():N}";
        var (master, externalSqlConfigured) = MasterConnection();
        try
        {
            await ExecuteAsync(master.ConnectionString,
                $"CREATE DATABASE [{database}] COLLATE SQL_Latin1_General_CP1_CI_AS;");
        }
        catch (SqlException) when (!externalSqlConfigured)
        {
            throw SkipException.ForSkip("LocalDB 不可用，序数身份隔离验收为 NotReady；没有把未执行记为通过。");
        }

        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var root = FindRoot();
            foreach (var migration in new[]
                     {
                         "001_workflow.sql", "002_workflow_key_version.sql",
                         "003_tool_execution_ledger.sql", "004_tool_execution_reconciliation.sql",
                         "005_tool_reconciliation_reviews.sql", "006_agent_run_cancellation.sql",
                         "007_tool_compensations.sql",
                         "008_tool_compensation_reconciliation.sql", "009_atlas_incident_runs.sql"
                     })
                await ExecuteAsync(connectionString,
                    await File.ReadAllTextAsync(Path.Combine(root, "deploy", "sql", migration)));

            // 模拟升级前继承数据库默认 CI 排序规则的生产表，证明安全不依赖先完成离线改列。
            await ExecuteAsync(connectionString, LegacyCaseInsensitiveIdentitySql);
            var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
            var clock = new FixedTimeProvider(now);
            var trace = new InMemoryTraceSink();
            var sqlOptions = new SqlServerWorkflowOptions
            {
                ConnectionString = connectionString,
                InitializeSchema = false
            };

            var approvalTool = new OrdinalMutationTool();
            var approvalRegistry = new ServerToolRegistry([approvalTool]);
            var safety = new RuleBasedToolInvocationSafetyService(new ToolSafetyOptions
            {
                AllowedTools = new HashSet<string>([approvalTool.Descriptor.Name],
                    StringComparer.OrdinalIgnoreCase)
            });
            using var approvals = new SqlServerToolApprovalService(approvalRegistry, safety, trace,
                new ToolApprovalOptions(), sqlOptions, clock);
            var owner = AccessContext.Create("Tenant-A", "User-A", ["readers", "tool-reconcilers"]);
            var arguments = JsonSerializer.SerializeToElement(new { memoryId = "ordinal-secret", expectedVersion = 1 });
            var approval = await approvals.RequestAsync(approvalTool.Descriptor.Name, arguments,
                "验证序数租户边界", owner);

            var compensationTool = new OrdinalCompensableTool();
            var compensationRegistry = new ServerToolRegistry([compensationTool]);
            var workflowCipher = new AesGcmWorkflowStateCipher("v1",
                new Dictionary<string, byte[]> { ["v1"] = RandomNumberGenerator.GetBytes(32) });
            using var compensations = new SqlServerToolCompensationService(compensationRegistry, workflowCipher,
                trace, new ToolCompensationOptions(), sqlOptions, clock);
            var preparation = await compensations.PrepareForwardAsync(new string('A', 64), compensationTool,
                new ToolExecutionContext(owner, "ordinal-forward"),
                JsonSerializer.SerializeToElement(new { value = "after" }));
            await compensations.ActivateAsync(preparation);

            var agentCipher = new CountingWorkflowStateCipher(workflowCipher);
            using var agentRuns = new SqlServerAgentRunCheckpointStore(sqlOptions, agentCipher, clock);
            var agentCheckpoint = CreateAgentCheckpoint(owner, now);
            await agentRuns.SavePendingAsync(agentCheckpoint);

            using var executionLedger = new SqlServerToolExecutionLedger(sqlOptions, workflowCipher, clock);
            var executionKey = new string('E', 64);
            var executionFingerprint = new string('F', 64);
            var executionLease = await executionLedger.TryAcquireAsync(new ToolExecutionLedgerRequest(executionKey,
                executionFingerprint, "ordinal-execution-run", owner.TenantId, owner.SubjectId, "memory.delete"),
                TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), 100);
            Assert.Equal(IdempotencyAcquireStatus.Acquired, executionLease.Status);
            await executionLedger.MarkExecutingAsync(executionKey, executionLease.LeaseToken!);
            await executionLedger.MarkOutcomeUnknownAsync(executionKey, executionLease.LeaseToken!);

            var countingCipher = new CountingWorkflowStateCipher(workflowCipher);
            using var atlas = new SqlServerAtlasIncidentStore(sqlOptions, countingCipher, clock);
            var checkpoint = CreateCheckpoint(owner, now);
            await atlas.CreateAsync(checkpoint);

            var tenantAlias = AccessContext.Create("tenant-a", "User-A",
                ["readers", "tool-approvers", "tool-reconcilers"]);
            var subjectAlias = AccessContext.Create("Tenant-A", "user-a", ["readers"]);
            Assert.Empty(await approvals.ListAsync(tenantAlias));
            Assert.Empty(await approvals.ListAsync(subjectAlias));
            var hiddenApproval = await Assert.ThrowsAsync<ToolApprovalException>(() =>
                approvals.GetAsync(approval.Id, tenantAlias));
            Assert.Equal("TOOL_APPROVAL_NOT_FOUND", hiddenApproval.Code);
            Assert.Empty(await compensations.ListAsync(tenantAlias));
            Assert.Empty(await compensations.ListAsync(subjectAlias));
            Assert.Empty(await atlas.ListAsync(tenantAlias, 100));
            Assert.Empty(await atlas.ListAsync(subjectAlias, 100));
            Assert.Null(await atlas.GetAsync(checkpoint.RunId, tenantAlias));
            Assert.Equal(0, countingCipher.UnprotectCount);
            Assert.Equal(AgentRunLeaseStatus.Forbidden, (await agentRuns.TryAcquireAsync(agentCheckpoint.RunId,
                tenantAlias, "alias-tenant-node", TimeSpan.FromMinutes(1))).Status);
            Assert.Equal(AgentRunLeaseStatus.Forbidden, (await agentRuns.TryAcquireAsync(agentCheckpoint.RunId,
                subjectAlias, "alias-subject-node", TimeSpan.FromMinutes(1))).Status);
            Assert.Equal(0, agentCipher.UnprotectCount);
            Assert.Empty(await executionLedger.ListOutcomeUnknownAsync(tenantAlias.TenantId, 100));
            Assert.Null(await executionLedger.GetOutcomeUnknownAsync(tenantAlias.TenantId, executionKey));
            var aliasReview = await executionLedger.SubmitReconciliationReviewAsync(new ToolReconciliationReview(
                executionKey, tenantAlias.TenantId, tenantAlias.SubjectId, ToolOutcomeProbeState.Applied,
                "ORDINAL_ALIAS_MUST_NOT_REVIEW", now, now.AddMinutes(5), true, new string('A', 64)));
            Assert.Equal(ToolReconciliationReviewStatus.NotFound, aliasReview.Status);
            Assert.Equal(executionKey,
                Assert.Single(await executionLedger.ListOutcomeUnknownAsync(owner.TenantId, 100)).ExecutionKey);

            var executionOptions = new ToolExecutionReconciliationOptions();
            var executionService = new ToolExecutionReconciliationService(
                executionLedger, trace, executionOptions, clock, []);
            var actionOptions = new OperationsActionOptions();
            var operations = new OperationsTaskService(approvals, compensations, executionService, atlas,
                new ToolApprovalOptions(), new ToolCompensationOptions(), executionOptions,
                new InMemoryOperationsActionStore(clock, actionOptions), actionOptions, clock);
            Assert.Empty((await operations.ListAsync(tenantAlias, null, null, null, 100)).Items);

            var exactTasks = (await operations.ListAsync(owner, null, null, null, 100)).Items;
            Assert.Contains(exactTasks, item => item.Type == "approval" && item.Id == approval.Id);
            Assert.Contains(exactTasks, item => item.Type == "compensation" && item.Id == preparation.Id);
            Assert.Contains(exactTasks, item => item.Type == "incident" && item.Id == checkpoint.RunId);
            Assert.Contains(exactTasks, item => item.Type == "execution" && item.Id == executionKey);
            Assert.Equal(1, countingCipher.UnprotectCount);

            var ordinalMigration = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "sql",
                "013_workflow_ordinal_identities.sql"));
            await ExecuteAsync(connectionString, ordinalMigration);
            await ExecuteAsync(connectionString, ordinalMigration);
            Assert.Equal(30, await CountOrdinalIdentityColumnsAsync(connectionString));

            await ExecuteAsync(connectionString, """
                DROP TABLE dbo.AiMentorToolReconciliations;
                DROP TABLE dbo.AiMentorToolExecutions;
                DROP TABLE dbo.AiMentorAgentRuns;
                """);
            var runtimeOptions = new SqlServerWorkflowOptions
            {
                ConnectionString = connectionString,
                InitializeSchema = true
            };
            using var runtimeAgentRuns = new SqlServerAgentRunCheckpointStore(runtimeOptions, workflowCipher, clock);
            await runtimeAgentRuns.SavePendingAsync(CreateAgentCheckpoint(owner, now));
            using var runtimeExecutions = new SqlServerToolExecutionLedger(runtimeOptions, workflowCipher, clock);
            Assert.Empty(await runtimeExecutions.ListOutcomeUnknownAsync(owner.TenantId, 10));
            Assert.Equal(30, await CountOrdinalIdentityColumnsAsync(connectionString));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(master.ConnectionString,
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
        }
    }

    private static AtlasIncidentCheckpoint CreateCheckpoint(AccessContext owner, DateTimeOffset now) => new(
        Guid.NewGuid().ToString("N"), owner, "BK-RUN-001", "2.2", AtlasIncidentStatus.RequiredInputs,
        new SafeAtlasIncidentInput(Region: "cn", Node: "ordinal-secret-node"), ["observedAt"], [], null,
        1, now, now, now.AddHours(1));

    private static AgentRunCheckpoint CreateAgentCheckpoint(AccessContext owner, DateTimeOffset now) => new(
        "Agent-Run-Ordinal", owner, "Approval-Ordinal", "framework-ordinal", "call-ordinal", "memory_delete",
        new Dictionary<string, object?> { ["memoryId"] = "ordinal-secret" },
        JsonSerializer.SerializeToElement(new { history = "ordinal-sensitive-session" }), [], [], [], 0,
        new AgentApprovalCheckpoint("Approval-Ordinal", "memory.delete", ToolOperationRisk.Mutation, ["memoryId"],
            now, now.AddMinutes(15)), now, now.AddMinutes(15));

    private static async Task<int> CountOrdinalIdentityColumnsAsync(string connectionString)
    {
        const string sql = """
            SELECT COUNT(*) FROM sys.columns
            WHERE collation_name=N'Latin1_General_100_BIN2'
              AND ((object_id=OBJECT_ID(N'dbo.AiMentorToolApprovals')
                    AND name IN (N'Id',N'TenantId',N'RequesterSubjectId',N'ApproverSubjectId'))
                OR (object_id=OBJECT_ID(N'dbo.AiMentorToolCompensations')
                    AND name IN (N'Id',N'TenantId',N'RequesterSubjectId',N'ApprovalId',N'ApproverSubjectId'))
                OR (object_id=OBJECT_ID(N'dbo.AiMentorToolCompensationReconciliations')
                    AND name IN (N'CompensationId',N'TenantId',N'FirstReviewerSubjectId',N'SecondReviewerSubjectId'))
                OR (object_id=OBJECT_ID(N'dbo.AiMentorAtlasIncidentRuns')
                    AND name IN (N'RunId',N'TenantId',N'SubjectId',N'LeaseOwner'))
                OR (object_id=OBJECT_ID(N'dbo.AiMentorAgentRuns')
                    AND name IN (N'RunId',N'TenantId',N'SubjectId',N'ApprovalId',N'LeaseOwner'))
                OR (object_id=OBJECT_ID(N'dbo.AiMentorToolExecutions')
                    AND name IN (N'ExecutionKey',N'RunId',N'TenantId',N'SubjectId'))
                OR (object_id=OBJECT_ID(N'dbo.AiMentorToolReconciliations')
                    AND name IN (N'ExecutionKey',N'TenantId',N'FirstReviewerSubjectId',
                      N'SecondReviewerSubjectId')));
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (SqlConnectionStringBuilder Connection, bool ExternalSqlConfigured) MasterConnection()
    {
        var external = Environment.GetEnvironmentVariable("AIMENTOR_SQLSERVER_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
            return (new SqlConnectionStringBuilder(external) { InitialCatalog = "master" }, true);
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip(
                "非 Windows 平台需通过 AIMENTOR_SQLSERVER_TEST_CONNECTION 显式提供 SQL Server；未执行不能记为通过。");
        return (new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            Encrypt = false
        }, false);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AiMentor.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class OrdinalMutationTool : IServerTool
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

    private sealed class OrdinalCompensableTool : ICompensableServerTool
    {
        public string CompensationToolName => "test.ordinal.restore";
        public ToolDescriptor Descriptor { get; } = new("test.ordinal.update", "更新测试状态。",
            ToolOperationRisk.Mutation, TimeSpan.FromSeconds(1), 4_096, true);
        public SafetyDecision ValidateArguments(JsonElement arguments) => SafetyDecision.Allowed;
        public Task<JsonElement> CaptureCompensationStateAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { previousValue = "sensitive-before" }));
        public Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> CompensateAsync(ToolExecutionContext context, JsonElement compensationState,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CountingWorkflowStateCipher(IWorkflowStateCipher inner) : IWorkflowStateCipher
    {
        public int UnprotectCount { get; private set; }
        public string ActiveKeyVersion => inner.ActiveKeyVersion;
        public ProtectedWorkflowState Protect(string plaintext, string context) => inner.Protect(plaintext, context);
        public string Unprotect(string? keyVersion, string ciphertext, string context)
        {
            UnprotectCount++;
            return inner.Unprotect(keyVersion, ciphertext, context);
        }
        public bool RequiresReencryption(string? keyVersion) => inner.RequiresReencryption(keyVersion);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private const string LegacyCaseInsensitiveIdentitySql = """
        DROP INDEX IX_AiMentorAgentRuns_OwnerStatus ON dbo.AiMentorAgentRuns;
        DROP INDEX IX_AiMentorAgentRuns_Cancellation ON dbo.AiMentorAgentRuns;
        ALTER TABLE dbo.AiMentorAgentRuns DROP CONSTRAINT PK_AiMentorAgentRuns;
        ALTER TABLE dbo.AiMentorAgentRuns ALTER COLUMN RunId nvarchar(128)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorAgentRuns ALTER COLUMN TenantId nvarchar(128)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorAgentRuns ALTER COLUMN SubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorAgentRuns ALTER COLUMN ApprovalId nvarchar(128)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorAgentRuns ALTER COLUMN LeaseOwner nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NULL;
        ALTER TABLE dbo.AiMentorAgentRuns ADD CONSTRAINT PK_AiMentorAgentRuns PRIMARY KEY(RunId);
        CREATE INDEX IX_AiMentorAgentRuns_OwnerStatus
            ON dbo.AiMentorAgentRuns(TenantId,SubjectId,Status);
        CREATE INDEX IX_AiMentorAgentRuns_Cancellation
            ON dbo.AiMentorAgentRuns(Status,CancelRequestedAt) INCLUDE(TenantId,SubjectId);

        ALTER TABLE dbo.AiMentorToolReconciliations
            DROP CONSTRAINT FK_AiMentorToolReconciliations_Execution;
        DROP INDEX IX_AiMentorToolReconciliations_TenantStatusExpiry
            ON dbo.AiMentorToolReconciliations;
        ALTER TABLE dbo.AiMentorToolReconciliations
            DROP CONSTRAINT PK_AiMentorToolReconciliations;
        DROP INDEX IX_AiMentorToolExecutions_TenantStatusUpdated ON dbo.AiMentorToolExecutions;
        ALTER TABLE dbo.AiMentorToolExecutions DROP CONSTRAINT PK_AiMentorToolExecutions;
        ALTER TABLE dbo.AiMentorToolExecutions ALTER COLUMN ExecutionKey char(64)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolExecutions ALTER COLUMN RunId nvarchar(128)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolExecutions ALTER COLUMN TenantId nvarchar(128)
            COLLATE SQL_Latin1_General_CP1_CI_AS NULL;
        ALTER TABLE dbo.AiMentorToolExecutions ALTER COLUMN SubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NULL;
        ALTER TABLE dbo.AiMentorToolReconciliations ALTER COLUMN ExecutionKey char(64)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolReconciliations ALTER COLUMN TenantId nvarchar(128)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolReconciliations ALTER COLUMN FirstReviewerSubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolReconciliations ALTER COLUMN SecondReviewerSubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NULL;
        ALTER TABLE dbo.AiMentorToolExecutions
            ADD CONSTRAINT PK_AiMentorToolExecutions PRIMARY KEY(ExecutionKey);
        CREATE INDEX IX_AiMentorToolExecutions_TenantStatusUpdated
            ON dbo.AiMentorToolExecutions(TenantId,Status,UpdatedAt DESC);
        ALTER TABLE dbo.AiMentorToolReconciliations
            ADD CONSTRAINT PK_AiMentorToolReconciliations PRIMARY KEY(ExecutionKey);
        ALTER TABLE dbo.AiMentorToolReconciliations
            ADD CONSTRAINT FK_AiMentorToolReconciliations_Execution FOREIGN KEY(ExecutionKey)
            REFERENCES dbo.AiMentorToolExecutions(ExecutionKey) ON DELETE CASCADE;
        CREATE INDEX IX_AiMentorToolReconciliations_TenantStatusExpiry
            ON dbo.AiMentorToolReconciliations(TenantId,Status,EvidenceExpiresAt);

        DROP INDEX IX_AiMentorToolApprovals_TenantStatus ON dbo.AiMentorToolApprovals;
        ALTER TABLE dbo.AiMentorToolApprovals ALTER COLUMN TenantId nvarchar(128)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolApprovals ALTER COLUMN RequesterSubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolApprovals ALTER COLUMN ApproverSubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NULL;
        CREATE INDEX IX_AiMentorToolApprovals_TenantStatus
            ON dbo.AiMentorToolApprovals(TenantId,Status,CreatedAt DESC);

        DROP INDEX IX_AiMentorToolCompensations_TenantStatusCreated ON dbo.AiMentorToolCompensations;
        ALTER TABLE dbo.AiMentorToolCompensations ALTER COLUMN TenantId nvarchar(128)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolCompensations ALTER COLUMN RequesterSubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolCompensations ALTER COLUMN ApproverSubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NULL;
        CREATE INDEX IX_AiMentorToolCompensations_TenantStatusCreated
            ON dbo.AiMentorToolCompensations(TenantId,Status,CreatedAt DESC);

        DROP INDEX IX_AiMentorToolCompensationReconciliations_TenantStatusExpiry
            ON dbo.AiMentorToolCompensationReconciliations;
        ALTER TABLE dbo.AiMentorToolCompensationReconciliations ALTER COLUMN TenantId nvarchar(128)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolCompensationReconciliations ALTER COLUMN FirstReviewerSubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorToolCompensationReconciliations ALTER COLUMN SecondReviewerSubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NULL;
        CREATE INDEX IX_AiMentorToolCompensationReconciliations_TenantStatusExpiry
            ON dbo.AiMentorToolCompensationReconciliations(TenantId,Status,EvidenceExpiresAt);

        DROP INDEX IX_AiMentorAtlasIncidentRuns_OwnerStatus ON dbo.AiMentorAtlasIncidentRuns;
        ALTER TABLE dbo.AiMentorAtlasIncidentRuns ALTER COLUMN TenantId nvarchar(128)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorAtlasIncidentRuns ALTER COLUMN SubjectId nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
        ALTER TABLE dbo.AiMentorAtlasIncidentRuns ALTER COLUMN LeaseOwner nvarchar(256)
            COLLATE SQL_Latin1_General_CP1_CI_AS NULL;
        CREATE INDEX IX_AiMentorAtlasIncidentRuns_OwnerStatus
            ON dbo.AiMentorAtlasIncidentRuns(TenantId,SubjectId,Status);
        """;
}
