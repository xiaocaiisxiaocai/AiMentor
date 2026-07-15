using System.Diagnostics;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class OpenSearchContainerAcceptanceScriptTests
{
    [Fact]
    public void ScriptPinsRealVersionAndExercisesFullAliasLifecycle()
    {
        var script = File.ReadAllText(ScriptPath());
        var root = Path.GetDirectoryName(Path.GetDirectoryName(ScriptPath()))!;
        var publish = File.ReadAllText(Path.Combine(root, "scripts", "Publish-OpenSearchIndex.ps1"));
        var rollback = File.ReadAllText(Path.Combine(root, "scripts", "Rollback-OpenSearchIndex.ps1"));

        Assert.Contains("opensearchproject/opensearch:3.5.0", script, StringComparison.Ordinal);
        Assert.Contains("_bulk?refresh=true", script, StringComparison.Ordinal);
        Assert.Contains("Publish-OpenSearchIndex.ps1", script, StringComparison.Ordinal);
        Assert.Contains("Rollback-OpenSearchIndex.ps1", script, StringComparison.Ordinal);
        Assert.Contains("Assert-AliasDocument $currentAlias 'DOC-V2'", script, StringComparison.Ordinal);
        Assert.Contains("Assert-AliasDocument $currentAlias 'DOC-V1'", script, StringComparison.Ordinal);
        Assert.Contains("IMAGE_PULL_TIMEOUT", script, StringComparison.Ordinal);
        Assert.Contains("HttpTimeoutSeconds", publish, StringComparison.Ordinal);
        Assert.Contains("HttpTimeoutSeconds", rollback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingImageWithoutPullIsNotReadyInsteadOfPassing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = CommandExists("pwsh") ? "pwsh" : CommandExists("powershell.exe") ? "powershell.exe" : null;
        if (shell is null) return;

        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(ScriptPath());
        start.ArgumentList.Add("-Endpoint");
        start.ArgumentList.Add("http://127.0.0.1:1");
        start.ArgumentList.Add("-Image");
        start.ArgumentList.Add("aimentor.invalid/not-present:acceptance");
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("OPENSEARCH_ACCEPTANCE_NOT_READY", output, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENSEARCH_ACCEPTANCE_PASSED", output + error, StringComparison.Ordinal);
    }

    private static string ScriptPath()
    {
        var root = Directory.GetParent(Directory.GetParent(WorkspacePathLocator.FindKnowledgeRoot())!.FullName)!.FullName;
        return Path.Combine(root, "scripts", "Test-OpenSearchContainerAcceptance.ps1");
    }

    private static bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("where.exe", command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            process!.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
