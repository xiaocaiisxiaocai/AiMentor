using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class LinuxContainerCiTests
{
    [Fact]
    public void WorkflowRunsRealSqlAndOpenSearchAcceptanceOnLinux()
    {
        var root = FindRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "linux-containers.yml"));

        Assert.Contains("runs-on: ubuntu-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("mcr.microsoft.com/mssql/server:2022-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("job.services.sqlserver.id", workflow, StringComparison.Ordinal);
        Assert.Contains("Category!=RequiresSqlServer", workflow, StringComparison.Ordinal);
        Assert.Contains("Category=RequiresSqlServer", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("FullyQualifiedName~SqlServer", workflow, StringComparison.Ordinal);
        Assert.Contains("Test-DistributedReconciliation.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-Configuration Release", workflow, StringComparison.Ordinal);
        Assert.Contains("Test-OpenSearchContainerAcceptance.ps1 -PullImage", workflow, StringComparison.Ordinal);
        Assert.Contains("evaluation-critical-v2.jsonl", workflow, StringComparison.Ordinal);
        Assert.Contains("--vulnerable --include-transitive", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet package list --project AiMentor.slnx", workflow, StringComparison.Ordinal);
        Assert.Contains("--format json", workflow, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json", workflow, StringComparison.Ordinal);
        Assert.Contains("VULNERABLE_DEPENDENCIES_FOUND", workflow, StringComparison.Ordinal);
        Assert.Contains("azure/setup-helm@v4.3.0", workflow, StringComparison.Ordinal);
        Assert.Contains("helm lint --strict deploy/helm/aimentor", workflow, StringComparison.Ordinal);
        Assert.Contains("migration.existingSecret=aimentor-migration", workflow, StringComparison.Ordinal);
        Assert.Contains("image.digest=\"${image_digest}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("releaseId=\"${image_digest}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("monitoring.prometheusRule.enabled=true", workflow, StringComparison.Ordinal);
        Assert.Contains("monitoring.prometheusRule.collectorJobRegex", workflow, StringComparison.Ordinal);
        Assert.Contains("kind: PrometheusRule", workflow, StringComparison.Ordinal);
        Assert.Contains("docker build --pull --tag aimentor:ci", workflow, StringComparison.Ordinal);
        Assert.Contains("--read-only --cap-drop ALL", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void DistributedAcceptanceUsesPortableReleasePathsAndCoversAtlasOperations()
    {
        var root = FindRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "Test-DistributedReconciliation.ps1"));
        var atlasTests = File.ReadAllText(Path.Combine(root, "tests", "AiMentor.Tests",
            "SqlServerAtlasIncidentStoreTests.cs"));
        var operationsTests = File.ReadAllText(Path.Combine(root, "tests", "AiMentor.Tests",
            "SqlServerOperationsActionStoreTests.cs"));
        var workflowIntegrationTests = File.ReadAllText(Path.Combine(root, "tests", "AiMentor.Tests",
            "SqlServerWorkflowIntegrationTests.cs"));

        Assert.Contains("[ValidateSet('Debug', 'Release')]", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Path]::Combine", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-WindowStyle Hidden", script, StringComparison.Ordinal);
        Assert.DoesNotContain("src\\AiMentor.Api\\bin\\Debug", script, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1::1433", script, StringComparison.Ordinal);
        Assert.Contains("docker port $SqlContainer 1433/tcp", script, StringComparison.Ordinal);
        Assert.Contains("AiMentor.Migrations.dll", script, StringComparison.Ordinal);
        Assert.Contains("AIMENTOR_MIGRATIONS_ROOT", script, StringComparison.Ordinal);
        Assert.Contains("AIMENTOR_RELEASE_ID", script, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach ($migration", script, StringComparison.Ordinal);
        Assert.Contains("Authentication__Development__Groups__*", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item Env:Authentication__Development__Groups__0", script,
            StringComparison.Ordinal);
        Assert.Contains("/api/v1/operations/tasks?type=incident", script, StringComparison.Ordinal);
        Assert.Contains("OperationsPayloadRedacted", script, StringComparison.Ordinal);
        Assert.Contains("ConcurrentCompensationApprovalWinners", script, StringComparison.Ordinal);
        Assert.Contains("ConcurrentCompensationExecutionWinners", script, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal 1 $stressDecisionWinners.Count", script, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal 1 $stressExecutionWinners.Count", script, StringComparison.Ordinal);
        Assert.Contains("RESTORE VERIFYONLY", script, StringComparison.Ordinal);
        Assert.Contains("DBCC CHECKDB", script, StringComparison.Ordinal);
        Assert.Contains("RestoredReplay", script, StringComparison.Ordinal);
        Assert.Contains("AIMENTOR_SQLSERVER_TEST_CONNECTION", atlasTests, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!OperatingSystem.IsWindows()) return;", atlasTests, StringComparison.Ordinal);
        Assert.Contains("AIMENTOR_SQLSERVER_TEST_CONNECTION", operationsTests, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!OperatingSystem.IsWindows()) return;", operationsTests, StringComparison.Ordinal);
        Assert.Contains("[Trait(\"Category\", \"RequiresSqlServer\")]", workflowIntegrationTests,
            StringComparison.Ordinal);
        Assert.Contains("AIMENTOR_SQLSERVER_TEST_CONNECTION", workflowIntegrationTests, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!OperatingSystem.IsWindows()) return;", workflowIntegrationTests,
            StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot();
        return Directory.GetParent(Directory.GetParent(knowledgeRoot)!.FullName)!.FullName;
    }
}
