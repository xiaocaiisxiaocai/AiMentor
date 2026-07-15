using System.Diagnostics;
using System.Globalization;
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
        var cleanupOwnership = script.IndexOf("$startedContainer = $true", StringComparison.Ordinal);
        var dockerRun = script.IndexOf("Invoke-Docker -Arguments @('run'", StringComparison.Ordinal);
        Assert.True(cleanupOwnership >= 0 && cleanupOwnership < dockerRun,
            "必须先取得容器名的清理责任，再调用可能超时的 docker run。");
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

    [Fact]
    public async Task PullTimeoutKillsProcessTreeAndRemovesSensitiveLogs()
    {
        if (!OperatingSystem.IsWindows() || !CommandExists("powershell.exe")) return;

        var tempRoot = Path.Combine(Path.GetTempPath(), $"aimentor-opensearch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var fakeDocker = Path.Combine(tempRoot, "fake-docker.cmd");
        var sleeperPidPath = Path.Combine(tempRoot, "sleeper.pid");
        var escapedPidPath = sleeperPidPath.Replace("'", "''", StringComparison.Ordinal);
        File.WriteAllText(fakeDocker, $"""
            @echo off
            echo %* | %SystemRoot%\System32\findstr.exe /I /C:"pull" >nul
            if not errorlevel 1 (
              >&2 echo PROXY_CREDENTIAL_SHOULD_NOT_LEAK
              powershell.exe -NoProfile -Command "[System.IO.File]::WriteAllText('{escapedPidPath}', [string]$PID); Start-Sleep -Seconds 60"
              exit /b 0
            )
            echo %* | %SystemRoot%\System32\findstr.exe /I /C:"image inspect" >nul
            if not errorlevel 1 exit /b 1
            exit /b 0
            """);

        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.Environment["TEMP"] = tempRoot;
            start.Environment["TMP"] = tempRoot;
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(ScriptPath());
            start.ArgumentList.Add("-Endpoint");
            start.ArgumentList.Add("http://127.0.0.1:1");
            start.ArgumentList.Add("-Image");
            start.ArgumentList.Add("aimentor.invalid/timeout:acceptance");
            start.ArgumentList.Add("-DockerCommand");
            start.ArgumentList.Add(fakeDocker);
            start.ArgumentList.Add("-PullImage");
            start.ArgumentList.Add("-PullTimeoutSeconds");
            start.ArgumentList.Add("5");

            using var process = Process.Start(start)!;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }

            var output = await outputTask;
            var error = await errorTask;
            Assert.Equal(2, process.ExitCode);
            Assert.True(output.Contains("OPENSEARCH_ACCEPTANCE_NOT_READY code=IMAGE_PULL_TIMEOUT", StringComparison.Ordinal),
                $"未获得预期拉取超时状态，实际输出：{output}");
            Assert.DoesNotContain("PROXY_CREDENTIAL_SHOULD_NOT_LEAK", output + error, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(tempRoot, "aimentor-opensearch-docker-*"));
            Assert.True(File.Exists(sleeperPidPath));

            var sleeperPid = int.Parse((await File.ReadAllTextAsync(sleeperPidPath)).Trim(), CultureInfo.InvariantCulture);
            try
            {
                using var sleeper = Process.GetProcessById(sleeperPid);
                Assert.True(sleeper.HasExited, $"超时后仍遗留拉取子进程 PID={sleeperPid}");
            }
            catch (ArgumentException)
            {
                // 进程不存在即为预期清理结果。
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
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
