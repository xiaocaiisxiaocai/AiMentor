using System.Security.Cryptography;
using System.Diagnostics;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Sdk;

namespace AiMentor.Tests;

public sealed class SqlServerAtlasIncidentStoreTests
{
    [Fact]
    public async Task KilledLeaseOwnerIsBusyBeforeExpiryAndAnotherProcessRecoversAfterExpiry()
    {
        var database = $"AiMentorAtlasKillTest_{Guid.NewGuid():N}";
        var (master, externalSqlConfigured) = MasterConnection();
        try { await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}]"); }
        catch (SqlException) when (!externalSqlConfigured)
        {
            throw SkipException.ForSkip("LocalDB 不可用，强杀验收为 NotReady；没有把未执行记为通过。");
        }
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        var readyPath = Path.Combine(Path.GetTempPath(), $"aimentor-atlas-ready-{Guid.NewGuid():N}.signal");
        Process? helper = null;
        try
        {
            var migration = await File.ReadAllTextAsync(Path.Combine(FindRoot(), "deploy", "sql",
                "009_atlas_incident_runs.sql"));
            await ExecuteAsync(connectionString, migration);
            var key = RandomNumberGenerator.GetBytes(32);
            var cipher = new AesGcmWorkflowStateCipher("v1", new Dictionary<string, byte[]> { ["v1"] = key });
            var options = new SqlServerWorkflowOptions { ConnectionString = connectionString, InitializeSchema = false };
            var owner = AccessContext.Create("tenant-kill", "user-kill", []);
            var checkpoint = CreateCheckpoint("kill-node", owner, DateTimeOffset.UtcNow);
            using var parentStore = new SqlServerAtlasIncidentStore(options, cipher, TimeProvider.System);
            await parentStore.CreateAsync(checkpoint);

            var helperDll = HelperDllPath();
            Assert.True(File.Exists(helperDll), "强杀辅助进程尚未生成。");
            var start = new ProcessStartInfo("dotnet", $"\"{helperDll}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["AIMENTOR_CRASH_SQL"] = connectionString;
            start.Environment["AIMENTOR_CRASH_RUN_ID"] = checkpoint.RunId;
            start.Environment["AIMENTOR_CRASH_TENANT"] = owner.TenantId;
            start.Environment["AIMENTOR_CRASH_SUBJECT"] = owner.SubjectId;
            start.Environment["AIMENTOR_CRASH_READY_PATH"] = readyPath;
            start.Environment["AIMENTOR_CRASH_KEY"] = Convert.ToBase64String(key);
            helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动强杀辅助进程。");
            await WaitForReadyAsync(helper, readyPath);

            helper.Kill(entireProcessTree: true);
            await helper.WaitForExitAsync();
            var busy = await Assert.ThrowsAsync<AtlasIncidentWorkflowException>(() => parentStore.TryAcquireAsync(
                checkpoint.RunId, owner, checkpoint.Version, TimeSpan.FromSeconds(3)));
            Assert.Equal("ATLAS_RUN_BUSY", busy.Code);

            await Task.Delay(TimeSpan.FromSeconds(3.5));
            var recovered = await parentStore.TryAcquireAsync(checkpoint.RunId, owner, checkpoint.Version,
                TimeSpan.FromSeconds(3));
            Assert.True(recovered.Acquired);
            var completed = await parentStore.SaveAndReleaseAsync(checkpoint with
            {
                Version = checkpoint.Version + 1,
                Status = AtlasIncidentStatus.DiagnosisReady,
                UpdatedAt = DateTimeOffset.UtcNow
            }, recovered.LeaseToken!);
            Assert.Equal(2, completed.Version);
            Assert.Equal(AtlasIncidentStatus.DiagnosisReady,
                (await parentStore.GetAsync(checkpoint.RunId, owner))!.Status);
            Assert.Empty(await helper.StandardOutput.ReadToEndAsync());
            Assert.Empty(await helper.StandardError.ReadToEndAsync());
        }
        finally
        {
            if (helper is { HasExited: false }) helper.Kill(entireProcessTree: true);
            helper?.Dispose();
            if (File.Exists(readyPath)) File.Delete(readyPath);
            SqlConnection.ClearAllPools();
            await ExecuteAsync(master.ConnectionString,
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
        }
    }

    [Fact]
    public async Task SqlStoreEncryptsAndRecoversWithLeaseAndOwnerBoundaries()
    {
        var database = $"AiMentorAtlasTest_{Guid.NewGuid():N}";
        var (master, _) = MasterConnection();
        // CI 显式提供外部 SQL 时连接或建库失败必须失败，不能退回平台跳过并形成假绿。
        await ExecuteAsync(master.ConnectionString, $"CREATE DATABASE [{database}]");
        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var migration = await File.ReadAllTextAsync(Path.Combine(FindRoot(), "deploy", "sql",
                "009_atlas_incident_runs.sql"));
            await ExecuteAsync(connectionString, migration);
            var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
            var clock = new MutableClock(now);
            var key = RandomNumberGenerator.GetBytes(32);
            var cipher = new AesGcmWorkflowStateCipher("v1", new Dictionary<string, byte[]> { ["v1"] = key });
            var options = new SqlServerWorkflowOptions { ConnectionString = connectionString, InitializeSchema = false };
            var owner = AccessContext.Create("tenant-a", "user-a", ["readers"]);
            var checkpoint = CreateCheckpoint("atlas-secret-node", owner, now);
            using var first = new SqlServerAtlasIncidentStore(options, cipher, clock);
            using var second = new SqlServerAtlasIncidentStore(options, cipher, clock);
            await first.CreateAsync(checkpoint);

            var storedPayload = await ScalarAsync(connectionString,
                "SELECT PayloadCipher FROM dbo.AiMentorAtlasIncidentRuns WHERE RunId=@id", checkpoint.RunId);
            Assert.DoesNotContain("atlas-secret-node", storedPayload, StringComparison.Ordinal);

            var attempts = await Task.WhenAll(Acquire(first, checkpoint.RunId, owner),
                Acquire(second, checkpoint.RunId, owner));
            var winner = Assert.Single(attempts, item => item.Result?.Acquired == true).Result!;
            Assert.Single(attempts, item => item.Error == "ATLAS_RUN_BUSY");
            clock.Advance(TimeSpan.FromSeconds(31));
            var recovered = await second.TryAcquireAsync(checkpoint.RunId, owner, checkpoint.Version,
                TimeSpan.FromSeconds(30));
            Assert.True(recovered.Acquired);
            var stale = await Assert.ThrowsAsync<AtlasIncidentWorkflowException>(() => first.SaveAndReleaseAsync(
                checkpoint with { Version = 2, UpdatedAt = clock.GetUtcNow() }, winner.LeaseToken!));
            Assert.Equal("ATLAS_LEASE_LOST", stale.Code);
            var saved = await second.SaveAndReleaseAsync(checkpoint with
            {
                Version = 2,
                Status = AtlasIncidentStatus.DiagnosisReady,
                UpdatedAt = clock.GetUtcNow()
            }, recovered.LeaseToken!);

            var rotatedCipher = new AesGcmWorkflowStateCipher("v2", new Dictionary<string, byte[]>
            {
                ["v1"] = key,
                ["v2"] = RandomNumberGenerator.GetBytes(32)
            });
            using var reconstructed = new SqlServerAtlasIncidentStore(options, rotatedCipher, clock);
            var restored = await reconstructed.GetAsync(checkpoint.RunId, owner);
            Assert.NotNull(restored);
            Assert.Equal(saved.RunId, restored.RunId);
            Assert.Equal(saved.Version, restored.Version);
            Assert.Equal(saved.Status, restored.Status);
            Assert.Equal(saved.SafeInput, restored.SafeInput);
            Assert.Equal(saved.RequiredInputs, restored.RequiredInputs);
            Assert.Equal("v2", await ScalarAsync(connectionString,
                "SELECT KeyVersion FROM dbo.AiMentorAtlasIncidentRuns WHERE RunId=@id", checkpoint.RunId));
            var cancelled = await reconstructed.CancelAsync(checkpoint.RunId, owner, saved.Version);
            Assert.Equal(AtlasIncidentStatus.Cancelled, cancelled.Status);
            var terminal = await Assert.ThrowsAsync<AtlasIncidentWorkflowException>(() => reconstructed.TryAcquireAsync(
                checkpoint.RunId, owner, cancelled.Version, TimeSpan.FromSeconds(30)));
            Assert.Equal("ATLAS_RUN_TERMINAL", terminal.Code);

            var moved = CreateCheckpoint("move-secret", owner, now) with { RunId = Guid.NewGuid().ToString("N") };
            await first.CreateAsync(moved);
            await ExecuteAsync(connectionString,
                $"UPDATE dbo.AiMentorAtlasIncidentRuns SET TenantId=N'tenant-b' WHERE RunId=N'{moved.RunId}'");
            await Assert.ThrowsAnyAsync<CryptographicException>(() => first.GetAsync(moved.RunId,
                AccessContext.Create("tenant-b", "user-a", ["readers"])));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(master.ConnectionString,
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
        }
    }

    private static async Task<(AtlasIncidentLeaseResult? Result, string? Error)> Acquire(SqlServerAtlasIncidentStore store,
        string runId, AccessContext owner)
    {
        try
        {
            return (await store.TryAcquireAsync(runId, owner, 1, TimeSpan.FromSeconds(30)), null);
        }
        catch (AtlasIncidentWorkflowException exception) { return (null, exception.Code); }
    }

    private static (SqlConnectionStringBuilder Connection, bool ExternalSqlConfigured) MasterConnection()
    {
        var external = Environment.GetEnvironmentVariable("AIMENTOR_SQLSERVER_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
        {
            var configured = new SqlConnectionStringBuilder(external) { InitialCatalog = "master" };
            return (configured, true);
        }
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip(
                "非 Windows 平台需通过 AIMENTOR_SQLSERVER_TEST_CONNECTION 显式提供测试 SQL Server；未执行不能记为通过。");
        return (new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            Encrypt = false
        }, false);
    }

    private static AtlasIncidentCheckpoint CreateCheckpoint(string node, AccessContext owner, DateTimeOffset now)
    {
        var checkpoint = new AtlasIncidentCheckpoint(Guid.NewGuid().ToString("N"), owner, "BK-RUN-001", "2.2",
            AtlasIncidentStatus.RequiredInputs, new SafeAtlasIncidentInput(Region: "cn", Node: node),
            ["observedAt"], [], null, 1, now, now, now.AddHours(1));
        return checkpoint;
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

    private static async Task<string> ScalarAsync(string connectionString, string sql, string id)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", id);
        return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
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

    private static string HelperDllPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        return Path.Combine(FindRoot(), "tests", "AiMentor.AtlasCrashHelper", "bin", configuration, "net10.0",
            "AiMentor.AtlasCrashHelper.dll");
    }

    private static async Task WaitForReadyAsync(Process helper, string readyPath)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!File.Exists(readyPath) && !helper.HasExited && DateTimeOffset.UtcNow < timeout)
            await Task.Delay(50);
        if (helper.HasExited)
            Assert.Fail($"强杀辅助进程在 READY 前退出，稳定退出码为 {helper.ExitCode}。");
        Assert.True(File.Exists(readyPath), "强杀辅助进程未在时限内写入 READY 信号。");
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
