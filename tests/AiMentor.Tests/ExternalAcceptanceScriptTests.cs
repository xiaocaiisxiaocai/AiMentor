using System.Diagnostics;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class ExternalAcceptanceScriptTests
{
    [Fact]
    public async Task OidcAcceptanceWithoutCredentialsIsBoundedNotReadyAndCannotLeaveACompletedRun()
    {
        var root = FindRoot();
        var script = Path.Combine(root, "scripts", "Test-OidcAcceptance.ps1");
        var text = File.ReadAllText(script);
        Assert.Contains("HttpTimeoutSeconds", text, StringComparison.Ordinal);
        Assert.Contains("/cancel", text, StringComparison.Ordinal);
        Assert.Contains("OIDC_TEST_RUN_CLEANUP_FAILED", text, StringComparison.Ordinal);

        var shell = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
        }
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.Environment.Remove("AIMENTOR_OIDC_API_BASE");
        start.Environment.Remove("AIMENTOR_OIDC_TOKEN_A");
        start.Environment.Remove("AIMENTOR_OIDC_TOKEN_B");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("OIDC_SCRIPT_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("OIDC_ACCEPTANCE_NOT_READY code=OIDC_ENV_MISSING", await stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("OIDC_ACCEPTANCE_PASSED", await stderr, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot();
        return Directory.GetParent(Directory.GetParent(knowledgeRoot)!.FullName)!.FullName;
    }
}
