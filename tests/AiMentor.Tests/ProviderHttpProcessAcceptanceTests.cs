using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace AiMentor.Tests;

public sealed class ProviderHttpProcessAcceptanceTests
{
    [Fact]
    public async Task IndependentHttpFixtureCoversThreeProvidersRetryNotReadyAndSafeReports()
    {
        var root = FindRoot();
        var output = Path.Combine(Path.GetTempPath(), $"aimentor-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        try
        {
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
            start.ArgumentList.Add(Path.Combine(root, "scripts", "Test-ProviderHttpAcceptance.ps1"));
            start.ArgumentList.Add("-NoBuild");
            start.ArgumentList.Add("-Configuration");
            start.ArgumentList.Add(Configuration());
            start.ArgumentList.Add("-OutputDirectory");
            start.ArgumentList.Add(output);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("PROVIDER_SCRIPT_START_FAILED");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            var text = await stdout;
            var error = await stderr;

            Assert.True(process.ExitCode == 0, $"Provider HTTP acceptance failed: {error} {text}");
            using var summary = JsonDocument.Parse(text);
            Assert.Equal("Passed", summary.RootElement.GetProperty("status").GetString());
            Assert.Equal(16, summary.RootElement.GetProperty("success").GetProperty("candidatePassed").GetInt32());
            Assert.Equal(3, summary.RootElement.GetProperty("success").GetProperty("rateLimitedRequests").GetInt32());
            Assert.Equal(16, summary.RootElement.GetProperty("failure").GetProperty("candidateNotReady").GetInt32());
            Assert.Equal(48, summary.RootElement.GetProperty("failure").GetProperty("embeddingRequests").GetInt32());
            Assert.DoesNotContain("alice.fixture@example.test", text, StringComparison.Ordinal);
            Assert.DoesNotContain("sk_live_1234567890abcdef", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
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
