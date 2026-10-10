using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpcBridge.Core;
using OpcBridge.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Starts the built OpcBridge.Logic app out-of-process (the same shape as
/// <see cref="TestAppHandle"/>): its output is copied to a temp directory, the bridge
/// address and its own port arrive as configuration environment variables, and the handle
/// is healthy once /health answers on the port the app reports it bound.
/// </summary>
public sealed class LogicAppHandle : IAsyncDisposable
{
    private readonly Process process_;
    private readonly string app_directory_;
    private readonly StringBuilder output_ = new();

    private LogicAppHandle(Process process, string appDirectory, HttpClient client)
    {
        process_ = process;
        app_directory_ = appDirectory;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<LogicAppHandle> StartAsync(string bridgeBaseUrl)
    {
        string sourceDirectory = Path.GetDirectoryName(typeof(LogicAppPage).Assembly.Location)
            ?? throw new InvalidOperationException("Could not locate OpcBridge.Logic output.");
        string appDirectory = Path.Combine(Path.GetTempPath(), "OpcBridge.LoadTest.Logic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDirectory);

        foreach (string file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(appDirectory, Path.GetFileName(file)), overwrite: true);
        }

        int port = ReservePort();
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = appDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(appDirectory, "OpcBridge.Logic.dll"));
        startInfo.Environment["Logic__HttpPort"] = port.ToString();
        startInfo.Environment["Bridge__BaseUrl"] = bridgeBaseUrl;

        Process process = new() { StartInfo = startInfo };
        HttpClient client = new();
        LogicAppHandle handle = new(process, appDirectory, client);
        process.OutputDataReceived += (_, args) => handle.AppendOutput(args.Data);
        process.ErrorDataReceived += (_, args) => handle.AppendOutput(args.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start OpcBridge.Logic test host.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await handle.WaitForHealthyAsync(port);
            return handle;
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
            client.Dispose();
            throw;
        }
    }

    public async Task<JsonDocument> GetJsonAsync(string path)
    {
        using HttpResponseMessage response = await Client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new Xunit.Sdk.XunitException(
                $"GET {path} => {(int)response.StatusCode} {response.StatusCode}.{Environment.NewLine}Body: {body}{Environment.NewLine}Process output:{Environment.NewLine}{output_}");
        }

        return JsonDocument.Parse(body);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        if (!process_.HasExited)
        {
            process_.Kill(entireProcessTree: true);
            await process_.WaitForExitAsync();
        }

        process_.Dispose();

        try
        {
            Directory.Delete(app_directory_, recursive: true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// The app logs the port it actually bound (it rolls when the reserved one was taken in
    /// the meantime), so the probe follows the app instead of the reservation.
    /// </summary>
    private async Task WaitForHealthyAsync(int reservedPort)
    {
        using HttpClient probe = new();
        for (int attempt = 0; attempt < 80; attempt++)
        {
            if (process_.HasExited)
            {
                throw new Xunit.Sdk.XunitException($"OpcBridge.Logic exited during startup with code {process_.ExitCode}.{Environment.NewLine}{output_}");
            }

            int port = ReadServingPort() ?? reservedPort;
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(250));
                using HttpResponseMessage response = await probe.GetAsync($"http://127.0.0.1:{port}/health", timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    Client.BaseAddress = new Uri($"http://127.0.0.1:{port}");
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for OpcBridge.Logic to become healthy.{Environment.NewLine}{output_}");
    }

    private int? ReadServingPort()
    {
        Match match = Regex.Match(output_.ToString(), @"serving on http://0\.0\.0\.0:(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int port) ? port : null;
    }

    private void AppendOutput(string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            output_.AppendLine(line);
        }
    }

    private static int next_candidate_port_ = 19400;

    private static int ReservePort()
    {
        while (true)
        {
            int candidate = Interlocked.Increment(ref next_candidate_port_);
            if (candidate > 19999)
            {
                throw new InvalidOperationException("Ran out of ports to hand to logic test hosts.");
            }

            if (PortHelper.IsPortAvailable(candidate))
            {
                return candidate;
            }
        }
    }
}
