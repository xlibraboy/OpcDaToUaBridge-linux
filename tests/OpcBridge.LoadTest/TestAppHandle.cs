using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class TestAppHandle : IAsyncDisposable
{
    private readonly Process process_;
    private readonly string app_directory_;
    private readonly StringBuilder output_ = new();

    private TestAppHandle(Process process, string appDirectory)
    {
        process_ = process;
        app_directory_ = appDirectory;
        Client = new HttpClient();
    }

    public HttpClient Client { get; }

    /// <summary>OPC UA port the app under test actually listens on (PortSetup auto-assigns when 4840 is taken).</summary>
    public int UaPort { get; private set; } = 4840;

    public static async Task<TestAppHandle> StartAsync(Action<string> configureAppDirectory)
    {
        string sourceDirectory = Path.GetDirectoryName(typeof(DaLinkStore).Assembly.Location)
            ?? throw new InvalidOperationException("Could not locate OpcBridge.App output.");
        string appDirectory = Path.Combine(Path.GetTempPath(), "OpcBridge.LoadTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDirectory);

        foreach (string file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(appDirectory, Path.GetFileName(file)), overwrite: true);
        }

        // RID-specific assets (e.g. System.IO.Ports native + unix impl) live under runtimes/.
        string runtimesSource = Path.Combine(sourceDirectory, "runtimes");
        if (Directory.Exists(runtimesSource))
        {
            CopyDirectory(runtimesSource, Path.Combine(appDirectory, "runtimes"));
        }

        configureAppDirectory(appDirectory);

        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = appDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(appDirectory, "OpcBridge.App.dll"));

        Process process = new() { StartInfo = startInfo };
        TestAppHandle handle = new(process, appDirectory);
        process.OutputDataReceived += (_, args) => handle.AppendOutput(args.Data);
        process.ErrorDataReceived += (_, args) => handle.AppendOutput(args.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start OpcBridge.App test host.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await handle.WaitForHealthyAsync();
        handle.UaPort = ReadBridgeIntSetting(
            Path.Combine(appDirectory, "appsettings.json"), "OpcUaPort") ?? 4840;
        return handle;
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

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    private void AppendOutput(string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            output_.AppendLine(line);
        }
    }

    /// <summary>
    /// PortSetup auto-assigns a free HTTP port when 8080 is taken and persists the choice to
    /// appsettings.json (Bridge:HttpPort) before the app starts listening. Re-read the port on
    /// every attempt so the health probe follows the app's actual port instead of whatever else
    /// occupies 8080.
    /// </summary>
    private async Task WaitForHealthyAsync()
    {
        string settingsPath = Path.Combine(app_directory_, "appsettings.json");
        using HttpClient probe = new();

        for (int attempt = 0; attempt < 80; attempt++)
        {
            if (process_.HasExited)
            {
                throw new Xunit.Sdk.XunitException($"OpcBridge.App exited during startup with code {process_.ExitCode}.{Environment.NewLine}{output_}");
            }

            int port = ReadBridgeIntSetting(settingsPath, "HttpPort") ?? 8080;
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

        throw new Xunit.Sdk.XunitException($"Timed out waiting for OpcBridge.App to become healthy.{Environment.NewLine}{output_}");
    }

    private static int? ReadBridgeIntSetting(string settingsPath, string key)
    {
        try
        {
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (settings.RootElement.TryGetProperty("Bridge", out JsonElement bridge) &&
                bridge.TryGetProperty(key, out JsonElement valueElement) &&
                valueElement.TryGetInt32(out int value) &&
                value is > 0 and < 65536)
            {
                return value;
            }
        }
        catch
        {
        }

        return null;
    }
}
