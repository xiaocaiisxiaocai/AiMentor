using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace AiMentor.Tests;

public sealed class OidcJwtProcessAcceptanceTests
{
    [Fact]
    public async Task IndependentProcessesCoverDiscoveryValidationRevocationAndKeyRotation()
    {
        var root = FindRoot();
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
        start.ArgumentList.Add(Path.Combine(root, "scripts", "Test-OidcHttpAcceptance.ps1"));
        start.ArgumentList.Add("-NoBuild");
        start.ArgumentList.Add("-Configuration");
        start.ArgumentList.Add(Configuration());

        using var process = Process.Start(start) ?? throw new InvalidOperationException("OIDC_SCRIPT_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        var text = await stdout;
        var error = await stderr;

        Assert.True(process.ExitCode == 0, $"OIDC HTTP acceptance failed: {error} {text}");
        using var summary = JsonDocument.Parse(text);
        var rootElement = summary.RootElement;
        Assert.Equal("Passed", rootElement.GetProperty("status").GetString());
        Assert.Equal(2, rootElement.GetProperty("subjects").GetInt32());
        Assert.True(rootElement.GetProperty("issuerRejected").GetBoolean());
        Assert.True(rootElement.GetProperty("audienceRejected").GetBoolean());
        Assert.True(rootElement.GetProperty("expiryRejected").GetBoolean());
        Assert.True(rootElement.GetProperty("revokedNewTokenForbidden").GetBoolean());
        Assert.True(rootElement.GetProperty("boundedOldTokenAccepted").GetBoolean());
        Assert.True(rootElement.GetProperty("rotatedKidAccepted").GetBoolean());
        Assert.True(rootElement.GetProperty("jwksRefreshObserved").GetBoolean());
        Assert.True(rootElement.GetProperty("discoveryRequests").GetInt32() >= 2);
        Assert.True(rootElement.GetProperty("jwksRequests").GetInt32() >= 2);
    }

    private static string Configuration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        return directory.Parent?.Name is "Release" ? "Release" : "Debug";
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AiMentor.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("AIMENTOR_ROOT_NOT_FOUND");
    }
}
