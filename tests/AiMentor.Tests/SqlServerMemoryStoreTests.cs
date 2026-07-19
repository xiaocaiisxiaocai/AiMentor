using System.Data;
using System.Security.Cryptography;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Sdk;

namespace AiMentor.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class SqlServerMemoryStoreTests
{
    private static readonly byte[] CipherKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MigrationEncryptsContentAndPreservesLifecycleCompensationAndOrdinalOwnership()
    {
        var database = $"AiMentorMemory_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString,
            $"CREATE DATABASE [{database}] COLLATE Latin1_General_100_CI_AS;");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var migration = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "deploy", "sql",
                "012_memory_store.sql"));
            await ExecuteAsync(connectionString, migration);
            await ExecuteAsync(connectionString, migration);
            var options = new SqlServerWorkflowOptions { ConnectionString = connectionString, InitializeSchema = false };
            using var store = new SqlServerMemoryStore(options, new AesGcmMemoryCipher(CipherKey));
            var owner = AccessContext.Create("Tenant-A", "User-A", []);
            var tenantCaseVariant = AccessContext.Create("tenant-a", "User-A", []);
            var subjectCaseVariant = AccessContext.Create("Tenant-A", "user-a", []);
            var originalExpiry = Now.AddHours(2);
            var proposal = Proposal("proposal-a", owner, MemoryScope.UserPreference, null,
                "answer.format", "表格-secret", originalExpiry);

            await store.SaveProposalAsync(proposal);
            var proposalAtRest = await QueryRowAsync(connectionString, """
                SELECT KeyCipher,KeyFingerprint,ValueCipher
                FROM dbo.AiMentorMemoryProposals WHERE Id=N'proposal-a';
                """);
            Assert.DoesNotContain(proposal.Key, proposalAtRest[0], StringComparison.Ordinal);
            Assert.DoesNotContain(proposal.Value, proposalAtRest[2], StringComparison.Ordinal);
            Assert.Equal(64, proposalAtRest[1].Length);
            Assert.Equal(MemoryStoreStatus.NotFound,
                (await store.ApproveAsync(proposal.Id, tenantCaseVariant, Now)).Status);

            var approved = await store.ApproveAsync(proposal.Id, owner, Now);
            var memory = Assert.IsType<MemoryRecord>(approved.Value);
            Assert.Equal(MemoryStoreStatus.Success, approved.Status);
            Assert.Equal(MemoryStoreStatus.NotFound, (await store.ApproveAsync(proposal.Id, owner, Now)).Status);
            Assert.Equal(0L, await ScalarInt64Async(connectionString,
                "SELECT COUNT_BIG(1) FROM dbo.AiMentorMemoryProposals WHERE Id=N'proposal-a';"));
            Assert.Empty(await store.ListActiveAsync(tenantCaseVariant, null, null, Now));
            Assert.Empty(await store.ListActiveAsync(subjectCaseVariant, null, null, Now));
            Assert.Equal(memory, Assert.Single(await store.ListActiveAsync(owner, null, null, Now)));

            var memoryAtRest = await QueryRowAsync(connectionString, $"""
                SELECT KeyCipher,KeyFingerprint,ValueCipher
                FROM dbo.AiMentorMemories WHERE Id=N'{memory.Id}';
                """);
            Assert.DoesNotContain(memory.Key, memoryAtRest[0], StringComparison.Ordinal);
            Assert.DoesNotContain(memory.Value, memoryAtRest[2], StringComparison.Ordinal);
            Assert.Equal(64, memoryAtRest[1].Length);

            await store.SaveProposalAsync(Proposal("proposal-duplicate", owner, MemoryScope.UserPreference, null,
                "ANSWER.FORMAT", "另一值", originalExpiry));
            Assert.Equal(MemoryStoreStatus.AlreadyExists,
                (await store.ApproveAsync("proposal-duplicate", owner, Now)).Status);
            Assert.Equal(0L, await ScalarInt64Async(connectionString,
                "SELECT COUNT_BIG(1) FROM dbo.AiMentorMemoryProposals WHERE Id=N'proposal-duplicate';"));

            var lowerTenantProposal = Proposal("PROPOSAL-A", tenantCaseVariant, MemoryScope.UserPreference, null,
                "answer.format", "lower-tenant-value", originalExpiry);
            await store.SaveProposalAsync(lowerTenantProposal);
            var lowerFingerprint = await QueryRowAsync(connectionString, """
                SELECT KeyFingerprint FROM dbo.AiMentorMemoryProposals WHERE Id=N'PROPOSAL-A';
                """);
            Assert.NotEqual(proposalAtRest[1], lowerFingerprint[0]);
            Assert.Equal(MemoryStoreStatus.Success,
                (await store.ApproveAsync(lowerTenantProposal.Id, tenantCaseVariant, Now)).Status);
            Assert.Single(await store.ListActiveAsync(tenantCaseVariant, null, null, Now));

            await store.SaveProposalAsync(Proposal("session-upper", owner, MemoryScope.Session, "Session-A",
                "current.task", "upper-session", originalExpiry));
            await store.SaveProposalAsync(Proposal("session-lower", owner, MemoryScope.Session, "session-a",
                "current.task", "lower-session", originalExpiry));
            Assert.Equal(MemoryStoreStatus.Success,
                (await store.ApproveAsync("session-upper", owner, Now)).Status);
            Assert.Equal(MemoryStoreStatus.Success,
                (await store.ApproveAsync("session-lower", owner, Now)).Status);
            Assert.Equal("upper-session",
                Assert.Single(await store.ListActiveAsync(owner, MemoryScope.Session, "Session-A", Now)).Value);
            Assert.Equal("lower-session",
                Assert.Single(await store.ListActiveAsync(owner, MemoryScope.Session, "session-a", Now)).Value);

            Assert.Equal(MemoryStoreStatus.Conflict,
                (await store.UpdateAsync(memory.Id, owner, 99, "ignored", null, Now.AddMinutes(1))).Status);
            Assert.Equal(MemoryStoreStatus.RetentionExceeded,
                (await store.UpdateAsync(memory.Id, owner, 1, "ignored", originalExpiry.AddMinutes(1),
                    Now.AddMinutes(1))).Status);
            var shortenedExpiry = originalExpiry.AddMinutes(-30);
            var corrected = await store.UpdateAsync(memory.Id, owner, 1, "列表", shortenedExpiry,
                Now.AddMinutes(1));
            Assert.Equal(2, corrected.Value!.Version);
            Assert.Equal(MemoryTargetState.PresentAtDifferentVersion,
                await store.ProbeTargetStateAsync(memory.Id, owner, 1));
            Assert.Equal(MemoryTargetState.PresentAtExpectedVersion,
                await store.ProbeTargetStateAsync(memory.Id, owner, 2));
            Assert.Equal(MemoryTargetState.Inaccessible,
                await store.ProbeTargetStateAsync(memory.Id, tenantCaseVariant, 2));
            Assert.Equal(MemoryCorrectionCompensationState.NotApplied,
                await store.ProbeCorrectionCompensationAsync(memory.Id, owner, 2, memory.Value, originalExpiry));

            var restored = await store.RestoreCompensationAsync(memory.Id, owner, 2, memory.Value, originalExpiry,
                Now.AddMinutes(2));
            Assert.Equal(MemoryStoreStatus.Success, restored.Status);
            Assert.Equal(3, restored.Value!.Version);
            Assert.Equal(originalExpiry, restored.Value.ExpiresAt);
            Assert.Equal(MemoryCorrectionCompensationState.Applied,
                await store.ProbeCorrectionCompensationAsync(memory.Id, owner, 2, memory.Value, originalExpiry));
            var drifted = await store.UpdateAsync(memory.Id, owner, 3, "已漂移", null, Now.AddMinutes(3));
            Assert.Equal(MemoryStoreStatus.Success, drifted.Status);
            Assert.Equal(MemoryCorrectionCompensationState.Changed,
                await store.ProbeCorrectionCompensationAsync(memory.Id, owner, 2, memory.Value, originalExpiry));

            Assert.Equal(MemoryStoreStatus.Conflict, (await store.DeleteAsync(memory.Id, owner, 3)).Status);
            Assert.Equal(MemoryStoreStatus.Success, (await store.DeleteAsync(memory.Id, owner, 4)).Status);
            Assert.Equal(MemoryTargetState.Absent, await store.ProbeTargetStateAsync(memory.Id, owner, 4));
            Assert.Equal(MemoryCorrectionCompensationState.Inaccessible,
                await store.ProbeCorrectionCompensationAsync(memory.Id, owner, 4, memory.Value, originalExpiry));

            await store.SaveProposalAsync(Proposal("expired-proposal", owner, MemoryScope.LongTermFact, null,
                "project.name", "Orion", Now.AddMinutes(-1), approvalExpiresAt: Now.AddMinutes(-1)));
            Assert.Equal(MemoryStoreStatus.Expired,
                (await store.ApproveAsync("expired-proposal", owner, Now)).Status);
            Assert.Equal(0L, await ScalarInt64Async(connectionString,
                "SELECT COUNT_BIG(1) FROM dbo.AiMentorMemoryProposals WHERE Id=N'expired-proposal';"));

            var capacityOwner = AccessContext.Create("tenant-capacity", "user-capacity", []);
            using var capacityStore = new SqlServerMemoryStore(options, new AesGcmMemoryCipher(CipherKey),
                new MemoryWorkflowOptions { MaximumPendingProposalsPerTenant = 1 });
            await capacityStore.SaveProposalAsync(Proposal("capacity-a", capacityOwner,
                MemoryScope.LongTermFact, null, "capacity.a", "A", Now.AddHours(1)));
            await Assert.ThrowsAsync<MemoryStoreCapacityException>(() => capacityStore.SaveProposalAsync(
                Proposal("capacity-b", capacityOwner, MemoryScope.LongTermFact, null,
                    "capacity.b", "B", Now.AddHours(1))));
            Assert.Equal(MemoryStoreStatus.Success,
                (await capacityStore.ApproveAsync("capacity-a", capacityOwner, Now)).Status);
            await capacityStore.SaveProposalAsync(Proposal("capacity-b", capacityOwner,
                MemoryScope.LongTermFact, null, "capacity.b", "B", Now.AddHours(1)));
            await store.SaveProposalAsync(Proposal("soon-expired", owner, MemoryScope.LongTermFact, null,
                "project.code", "A-1", Now.AddMinutes(1)));
            var soonExpired = (await store.ApproveAsync("soon-expired", owner, Now)).Value!;
            Assert.DoesNotContain(soonExpired,
                await store.ListActiveAsync(owner, null, null, Now.AddMinutes(2)));
            Assert.Equal(MemoryStoreStatus.NotFound,
                (await store.UpdateAsync(soonExpired.Id, owner, 1, "A-2", null, Now.AddMinutes(2))).Status);

            await AssertOrdinalCollationsAndEncryptedShapeAsync(connectionString);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(master.ConnectionString,
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
        }
    }

    [Fact]
    public async Task RuntimeSchemaConcurrentApprovalHasExactlyOneWinner()
    {
        var database = $"AiMentorMemoryRace_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString,
            $"CREATE DATABASE [{database}] COLLATE Latin1_General_100_CI_AS;");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var options = new SqlServerWorkflowOptions { ConnectionString = connectionString, InitializeSchema = true };
            using var first = new SqlServerMemoryStore(options, new AesGcmMemoryCipher(CipherKey));
            using var second = new SqlServerMemoryStore(options, new AesGcmMemoryCipher(CipherKey));
            var owner = AccessContext.Create("tenant-race", "user-race", []);
            await Task.WhenAll(
                first.SaveProposalAsync(Proposal("race-a", owner, MemoryScope.UserPreference, null,
                    "answer.language", "中文", Now.AddHours(1))),
                second.SaveProposalAsync(Proposal("race-b", owner, MemoryScope.UserPreference, null,
                    "ANSWER.LANGUAGE", "English", Now.AddHours(1))));

            var approvals = await Task.WhenAll(first.ApproveAsync("race-a", owner, Now),
                second.ApproveAsync("race-b", owner, Now));

            Assert.Single(approvals, result => result.Status == MemoryStoreStatus.Success);
            Assert.Single(approvals, result => result.Status == MemoryStoreStatus.AlreadyExists);
            Assert.Single(await first.ListActiveAsync(owner, null, null, Now));
            Assert.Equal(1L, await ScalarInt64Async(connectionString,
                "SELECT COUNT_BIG(1) FROM dbo.AiMentorMemories;"));
            Assert.Equal(0L, await ScalarInt64Async(connectionString,
                "SELECT COUNT_BIG(1) FROM dbo.AiMentorMemoryProposals;"));
            await AssertOrdinalCollationsAndEncryptedShapeAsync(connectionString);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(master.ConnectionString,
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
        }
    }

    [Fact]
    public async Task RequestCleanupIsTenantBoundAndBoundedWhileExplicitPurgeCoversDormantTenants()
    {
        var database = $"AiMentorMemoryRetention_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString,
            $"CREATE DATABASE [{database}] COLLATE Latin1_General_100_CI_AS;");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var migration = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "deploy", "sql",
                "012_memory_store.sql"));
            await ExecuteAsync(connectionString, migration);
            var retentionStateMigration = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "deploy",
                "sql", "014_memory_retention_state.sql"));
            await ExecuteAsync(connectionString, retentionStateMigration);
            var storeOptions = new SqlServerWorkflowOptions
            { ConnectionString = connectionString, InitializeSchema = false };
            var retentionOptions = new MemoryWorkflowOptions
            { MaximumPendingProposalsPerTenant = 10, RequestCleanupBatchSize = 1 };
            using var store = new SqlServerMemoryStore(storeOptions, new AesGcmMemoryCipher(CipherKey),
                retentionOptions);
            var retention = new MemoryRetentionService(store);
            var activeTenant = AccessContext.Create("tenant-active", "user-a", []);
            var dormantTenant = AccessContext.Create("tenant-dormant", "user-b", []);

            foreach (var (owner, prefix) in new[] { (activeTenant, "active"), (dormantTenant, "dormant") })
            {
                for (var index = 0; index < 2; index++)
                {
                    var proposal = Proposal($"{prefix}-memory-{index}", owner, MemoryScope.LongTermFact, null,
                        $"{prefix}.key.{index}", $"value-{index}", Now.AddMinutes(1));
                    await store.SaveProposalAsync(proposal);
                    Assert.Equal(MemoryStoreStatus.Success,
                        (await store.ApproveAsync(proposal.Id, owner, Now)).Status);
                }
                await store.SaveProposalAsync(Proposal($"{prefix}-expired-proposal", owner,
                    MemoryScope.LongTermFact, null, $"{prefix}.expired", "expired", Now.AddHours(1),
                    approvalExpiresAt: Now.AddMinutes(-1)));
            }

            Assert.Empty(await store.ListActiveAsync(activeTenant, null, null, Now.AddMinutes(2)));
            Assert.Equal(0L, await ScalarInt64Async(connectionString, """
                SELECT COUNT_BIG(1) FROM dbo.AiMentorMemoryProposals WHERE TenantId=N'tenant-active';
                """));
            Assert.Equal(1L, await ScalarInt64Async(connectionString, """
                SELECT COUNT_BIG(1) FROM dbo.AiMentorMemories WHERE TenantId=N'tenant-active';
                """));
            Assert.Equal(1L, await ScalarInt64Async(connectionString, """
                SELECT COUNT_BIG(1) FROM dbo.AiMentorMemoryProposals WHERE TenantId=N'tenant-dormant';
                """));
            Assert.Equal(2L, await ScalarInt64Async(connectionString, """
                SELECT COUNT_BIG(1) FROM dbo.AiMentorMemories WHERE TenantId=N'tenant-dormant';
                """));

            Assert.Empty(await store.ListActiveAsync(activeTenant, null, null, Now.AddMinutes(2)));
            Assert.Equal(0L, await ScalarInt64Async(connectionString, """
                SELECT COUNT_BIG(1) FROM dbo.AiMentorMemories WHERE TenantId=N'tenant-active';
                """));

            var monitoringStartedAt = default(DateTimeOffset);
            await using (var lockConnection = new SqlConnection(connectionString))
            {
                await lockConnection.OpenAsync();
                await using var lockTransaction = (SqlTransaction)await lockConnection.BeginTransactionAsync();
                await using var lockCommand = lockConnection.CreateCommand();
                lockCommand.Transaction = lockTransaction;
                lockCommand.CommandText = """
                    DECLARE @result int;
                    EXEC @result=sys.sp_getapplock @Resource=N'AiMentor.Memory.Retention',
                        @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
                    SELECT @result;
                    """;
                Assert.True(Convert.ToInt32(await lockCommand.ExecuteScalarAsync(),
                    System.Globalization.CultureInfo.InvariantCulture) >= 0);
                var blocked = await retention.PurgeExpiredAsync(1);
                Assert.False(blocked.LockAcquired);
                Assert.Null(blocked.LastCompletedAt);
                Assert.NotEqual(default, blocked.MonitoringStartedAt);
                monitoringStartedAt = blocked.MonitoringStartedAt;
                await lockTransaction.RollbackAsync();
            }

            var first = await retention.PurgeExpiredAsync(1);
            Assert.True(first.LockAcquired);
            Assert.NotNull(first.LastCompletedAt);
            Assert.Equal(monitoringStartedAt, first.MonitoringStartedAt);
            Assert.Equal(1, first.ProposalsDeleted);
            Assert.Equal(1, first.MemoriesDeleted);
            var second = await retention.PurgeExpiredAsync(1);
            Assert.True(second.LockAcquired);
            Assert.True(second.LastCompletedAt >= first.LastCompletedAt);
            Assert.Equal(first.MonitoringStartedAt, second.MonitoringStartedAt);
            Assert.Equal(0, second.ProposalsDeleted);
            Assert.Equal(1, second.MemoriesDeleted);
            Assert.Equal(0L, await ScalarInt64Async(connectionString,
                "SELECT COUNT_BIG(1) FROM dbo.AiMentorMemoryProposals;"));
            Assert.Equal(0L, await ScalarInt64Async(connectionString,
                "SELECT COUNT_BIG(1) FROM dbo.AiMentorMemories;"));

            await store.ProbeReadinessAsync();
            await ExecuteAsync(connectionString, "DROP INDEX IX_AiMentorMemories_TenantExpiry ON dbo.AiMentorMemories;");
            await ExecuteAsync(connectionString, """
                CREATE INDEX IX_AiMentorMemories_TenantExpiry ON dbo.AiMentorMemories(ExpiresAt,TenantId);
                """);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ProbeReadinessAsync());
            await ExecuteAsync(connectionString, "DROP INDEX IX_AiMentorMemories_TenantExpiry ON dbo.AiMentorMemories;");
            await ExecuteAsync(connectionString, migration);
            await store.ProbeReadinessAsync();
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(master.ConnectionString,
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
        }
    }

    [Fact]
    public async Task ReadingWithRotatedKeyRingReencryptsSqlCiphertextWithoutChangingBusinessVersion()
    {
        var database = $"AiMentorMemoryRotation_{Guid.NewGuid():N}";
        var master = MasterConnection();
        await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}];");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var migration = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "deploy", "sql",
                "012_memory_store.sql"));
            await ExecuteAsync(connectionString, migration);
            var retentionStateMigration = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "deploy",
                "sql", "014_memory_retention_state.sql"));
            await ExecuteAsync(connectionString, retentionStateMigration);
            var v2 = RandomNumberGenerator.GetBytes(32);
            var fingerprintKey = RandomNumberGenerator.GetBytes(32);
            var options = new SqlServerWorkflowOptions
            {
                ConnectionString = connectionString,
                InitializeSchema = false
            };
            var owner = AccessContext.Create("tenant-rotation", "user-rotation", []);
            using (var oldStore = new SqlServerMemoryStore(options,
                       new AesGcmMemoryCipher("2026-01", new Dictionary<string, byte[]>
                       {
                           ["2026-01"] = CipherKey
                       }, fingerprintKey)))
            {
                await oldStore.SaveProposalAsync(Proposal("proposal-rotation", owner,
                    MemoryScope.UserPreference, null, "answer.style", "concise", Now.AddHours(1)));
                Assert.Equal(MemoryStoreStatus.Success,
                    (await oldStore.ApproveAsync("proposal-rotation", owner, Now)).Status);
            }

            using (var missingOldKeyStore = new SqlServerMemoryStore(options,
                       new AesGcmMemoryCipher("2026-07", new Dictionary<string, byte[]>
                       {
                           ["2026-07"] = v2
                       }, fingerprintKey)))
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    missingOldKeyStore.ProbeReadinessAsync());

            using (var wrongMaterialStore = new SqlServerMemoryStore(options,
                       new AesGcmMemoryCipher("2026-01", new Dictionary<string, byte[]>
                       {
                           ["2026-01"] = v2
                       }, fingerprintKey)))
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    wrongMaterialStore.ProbeReadinessAsync());

            using var rotatedStore = new SqlServerMemoryStore(options,
                new AesGcmMemoryCipher("2026-07", new Dictionary<string, byte[]>
                {
                    ["2026-01"] = CipherKey,
                    ["2026-07"] = v2
                }, fingerprintKey));
            await rotatedStore.ProbeReadinessAsync();
            var memory = Assert.Single(await rotatedStore.ListActiveAsync(owner, null, null, Now));
            Assert.Equal("concise", memory.Value);
            Assert.Equal(1, memory.Version);
            var cipherPrefixes = await QueryRowAsync(connectionString, """
                SELECT TOP(1) KeyVersion,KeyCipher,KeyFingerprint,ValueCipher
                FROM dbo.AiMentorMemories;
                """);
            Assert.Equal("2026-07", cipherPrefixes[0]);
            Assert.StartsWith("mem1.2026-07.", cipherPrefixes[1], StringComparison.Ordinal);
            Assert.StartsWith("mem1.2026-07.", cipherPrefixes[3], StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(master.ConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }

    private static MemoryProposal Proposal(string id, AccessContext owner, MemoryScope scope, string? sessionId,
        string key, string value, DateTimeOffset memoryExpiresAt, DateTimeOffset? approvalExpiresAt = null) => new(
        id, owner.TenantId, owner.SubjectId, scope, sessionId, key, value, Now,
        approvalExpiresAt ?? Now.AddMinutes(15), memoryExpiresAt, MemoryProposalStatus.PendingApproval);

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
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> QueryRowAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return Enumerable.Range(0, reader.FieldCount).Select(reader.GetString).ToArray();
    }

    private static async Task<long> ScalarInt64Async(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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

    private static async Task AssertOrdinalCollationsAndEncryptedShapeAsync(string connectionString)
    {
        var collations = await QueryStringsAsync(connectionString, """
            SELECT OBJECT_NAME(c.object_id) + N'.' + c.name + N':' + c.collation_name
            FROM sys.columns c
            WHERE c.object_id IN (OBJECT_ID(N'dbo.AiMentorMemoryProposals'),OBJECT_ID(N'dbo.AiMentorMemories'))
              AND c.name IN (N'Id',N'TenantId',N'SubjectId',N'SessionId',N'KeyVersion',N'KeyFingerprint')
            ORDER BY OBJECT_NAME(c.object_id),c.name;
            """);
        Assert.Equal(12, collations.Count);
        Assert.All(collations,
            value => Assert.EndsWith(":Latin1_General_100_BIN2", value, StringComparison.Ordinal));
        var columns = await QueryStringsAsync(connectionString, """
            SELECT c.name FROM sys.columns c
            WHERE c.object_id IN (OBJECT_ID(N'dbo.AiMentorMemoryProposals'),OBJECT_ID(N'dbo.AiMentorMemories'))
            ORDER BY c.name;
            """);
        Assert.DoesNotContain("Key", columns);
        Assert.DoesNotContain("Value", columns);
        Assert.Equal(2, columns.Count(name => name == "KeyCipher"));
        Assert.Equal(2, columns.Count(name => name == "ValueCipher"));
        var indexes = await QueryStringsAsync(connectionString, """
            SELECT i.name FROM sys.indexes i
            WHERE i.object_id IN (OBJECT_ID(N'dbo.AiMentorMemoryProposals'),OBJECT_ID(N'dbo.AiMentorMemories'))
              AND i.name IN (
                N'IX_AiMentorMemoryProposals_TenantStatusApprovalExpiry',
                N'IX_AiMentorMemoryProposals_TenantMemoryExpiry',
                N'IX_AiMentorMemoryProposals_Status',
                N'IX_AiMentorMemoryProposals_ApprovalExpiry',
                N'IX_AiMentorMemoryProposals_MemoryExpiry',
                N'IX_AiMentorMemoryProposals_KeyVersion',
                N'IX_AiMentorMemories_TenantExpiry',
                N'IX_AiMentorMemories_KeyVersion')
            ORDER BY i.name;
            """);
        Assert.Equal(8, indexes.Count);
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
        public override DateTimeOffset GetUtcNow() => now;
    }
}
