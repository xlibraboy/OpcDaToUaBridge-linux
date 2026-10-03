using System.Diagnostics;
using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DaWorkerHostTests
{
    [Fact]
    public void IsWorkerInvocation_MatchesOnlyTheExactSwitch()
    {
        Assert.True(DaWorkerHost.IsWorkerInvocation(new[] { "--da-worker" }));
        Assert.False(DaWorkerHost.IsWorkerInvocation(Array.Empty<string>()));
        Assert.False(DaWorkerHost.IsWorkerInvocation(new[] { "--da-worker", "--extra" }));
        Assert.False(DaWorkerHost.IsWorkerInvocation(new[] { "--other" }));
        Assert.False(DaWorkerHost.IsWorkerInvocation(new[] { "--DA-WORKER" }));
    }

    [Fact]
    public void Run_OnNonWindows_ReturnsBootstrapError()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(DaWorkerHost.ExitBootstrapError, DaWorkerHost.Run());
    }

    [Fact]
    public async Task SpawnedWorker_OnNonWindows_ExitsBeforeStartingTheBridge()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (string fileName, List<string> arguments) = DaChildProcess.Resolve(DaWorkerHost.ModeArgument);
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();

        string standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(DaWorkerHost.ExitBootstrapError, process.ExitCode);
        Assert.Contains("Windows", standardError, StringComparison.OrdinalIgnoreCase);
    }
}
