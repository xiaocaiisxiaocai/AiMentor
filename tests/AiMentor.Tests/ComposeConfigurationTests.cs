using System.Diagnostics;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class ComposeConfigurationTests
{
    [Fact]
    public async Task OpenSearchProfileDoesNotRequireSqlServerPasswordDuringComposeParsing()
    {
        if (!OperatingSystem.IsWindows() || !CommandExists("docker"))
            return;

        var root = Directory.GetParent(Directory.GetParent(WorkspacePathLocator.FindKnowledgeRoot())!.FullName)!.FullName;
        var startInfo = new ProcessStartInfo("docker", "compose config --services")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment.Remove("AIMENTOR_SQLSERVER_SA_PASSWORD");
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("opensearch", output, StringComparison.Ordinal);
        Assert.DoesNotContain("AIMENTOR_SQLSERVER_SA_PASSWORD", error, StringComparison.Ordinal);
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
