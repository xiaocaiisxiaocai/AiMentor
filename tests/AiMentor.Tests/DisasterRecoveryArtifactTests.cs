using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class DisasterRecoveryArtifactTests
{
    [Fact]
    public void SqlDisasterRecoveryAcceptanceVerifiesBackupRestoreIntegrityAndIdempotency()
    {
        var root = FindRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "Test-DistributedReconciliation.ps1"));

        var writersStopped = script.IndexOf("# 灾备验收在一致性检查点停止所有写入者", StringComparison.Ordinal);
        var backupStarted = script.IndexOf("BACKUP DATABASE [$database]", StringComparison.Ordinal);
        Assert.True(writersStopped >= 0 && backupStarted > writersStopped);
        Assert.Contains("WITH COPY_ONLY, INIT, CHECKSUM", script, StringComparison.Ordinal);
        Assert.Contains("RESTORE VERIFYONLY FROM DISK=N'$backupFile' WITH CHECKSUM", script,
            StringComparison.Ordinal);
        Assert.Contains("RESTORE DATABASE [$database]", script, StringComparison.Ordinal);
        Assert.Contains("MOVE N'$dataLogicalName' TO N'$restoredDataFile'", script, StringComparison.Ordinal);
        Assert.Contains("MOVE N'$logLogicalName' TO N'$restoredLogFile'", script, StringComparison.Ordinal);
        Assert.Contains("DBCC CHECKDB([$database]) WITH NO_INFOMSGS, ALL_ERRORMSGS", script,
            StringComparison.Ordinal);
        Assert.Contains("AIMENTOR_MIGRATIONS_VERIFY_ONLY = 'true'", script, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal '14' $restoredMigrationRows", script, StringComparison.Ordinal);
        Assert.Contains("$atlasObservedAt.ToString('O'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("observedAt = [DateTimeOffset]::UtcNow", script, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal $true $restoredReplay.Json.idempotentReplay", script,
            StringComparison.Ordinal);
        Assert.Contains("Assert-Equal '1' $executionRowsAfterRestore", script, StringComparison.Ordinal);
        Assert.Contains("BackupVerified = $backupVerified", script, StringComparison.Ordinal);
        Assert.Contains("DatabaseCheckPassed = $databaseCheckPassed", script, StringComparison.Ordinal);
        Assert.Contains("RestoredMemoryValue = $restoredMemory[0].value", script, StringComparison.Ordinal);
        Assert.Contains("RestoredReplay = $restoredReplay.Json.status", script, StringComparison.Ordinal);
        Assert.Contains("RestoredMigrationRows = [long]$restoredMigrationRows", script, StringComparison.Ordinal);
        Assert.Contains("foreach ($sqlFile in @($backupFile, $restoredDataFile, $restoredLogFile))", script,
            StringComparison.Ordinal);
        Assert.Contains("docker exec -u 0 $SqlContainer rm -f -- $sqlFile", script, StringComparison.Ordinal);
        Assert.Contains("if ($LASTEXITCODE -ne 0) { $cleanupFailures.Add", script, StringComparison.Ordinal);
        Assert.Contains("Restore-ProcessEnvironment", script, StringComparison.Ordinal);
        Assert.Contains("Stop-EphemeralSqlContainerAfterSetupFailure", script, StringComparison.Ordinal);
        Assert.Contains("$cleanupFailed = $LASTEXITCODE -ne 0", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Variable -Scope Script -Name masterKey", script, StringComparison.Ordinal);
        Assert.Contains("if ($succeeded) { throw \"分布式验收清理失败", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DisasterRecoveryRunbookKeepsDataAndKeyDependenciesFailClosed()
    {
        var root = FindRoot();
        var runbook = File.ReadAllText(Path.Combine(root, "docs", "operations", "disaster-recovery.md"));

        Assert.Contains("恢复点目标（RPO）：不超过 15 分钟", runbook, StringComparison.Ordinal);
        Assert.Contains("恢复时间目标（RTO）：不超过 60 分钟", runbook, StringComparison.Ordinal);
        Assert.Contains("OpenSearch", runbook, StringComparison.Ordinal);
        Assert.Contains("Data Protection", runbook, StringComparison.Ordinal);
        Assert.Contains("Workflow:Encryption:Keys", runbook, StringComparison.Ordinal);
        Assert.Contains("AIMENTOR_MEMORY_ENCRYPTION_KEY", runbook, StringComparison.Ordinal);
        Assert.Contains("OutcomeUnknown", runbook, StringComparison.Ordinal);
        Assert.Contains("不得通过降级配置带流量启动", runbook, StringComparison.Ordinal);
        Assert.Contains("RestoredExecutionRows=1", runbook, StringComparison.Ordinal);
        Assert.Contains("RestoredMigrationRows=14", runbook, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot();
        return Directory.GetParent(Directory.GetParent(knowledgeRoot)!.FullName)!.FullName;
    }
}
