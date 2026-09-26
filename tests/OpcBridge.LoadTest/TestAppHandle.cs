using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpcBridge.App;
using OpcBridge.Core;
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
        string sourceDirectory = Path.GetDirectoryName(typeof(InterlinkStore).Assembly.Location)
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

        // The shipped appsettings.json enables dashboard authentication; test hosts run
        // without it unless the per-test configuration opts back in by writing its own
        // appsettings.json (see AuthApiTests). Applied before the callback so a test that
        // replaces the file wins.
        DisableAuthentication(appDirectory);

        configureAppDirectory(appDirectory);

        // Give this instance its own ports. Left at the defaults, several instances starting at
        // once all probed 8080, all found it free, and one died binding it ("Address already in
        // use") — failing whichever test happened to own that instance.
        WriteReservedPorts(appDirectory, ReservePort(), ReservePort());

        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = appDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(appDirectory, "OpcBridge.App.dll"));

        // Isolate the single-instance lock per test app so concurrently (or previously)
        // running bridge instances never block a test host from starting.
        startInfo.Environment["OPCBRIDGE_INSTANCE_LOCK"] = Path.Combine(appDirectory, "instance.lock");

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

        try
        {
            await handle.WaitForHealthyAsync();
            handle.UaPort = ReadBridgeIntSetting(
                Path.Combine(appDirectory, "appsettings.json"), "OpcUaPort") ?? 4840;
            return handle;
        }
        catch
        {
            // Startup failed (e.g. health timeout): never leak the app process.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
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

    private static void DisableAuthentication(string appDirectory)
    {
        string settingsPath = Path.Combine(appDirectory, "appsettings.json");
        try
        {
            if (!File.Exists(settingsPath))
            {
                return;
            }

            JsonNode? root = JsonNode.Parse(File.ReadAllText(settingsPath));
            if (root is not JsonObject settings)
            {
                return;
            }

            settings["Auth"] = new JsonObject { ["Enabled"] = false };
            File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A malformed settings file is the test's problem, not the harness's.
        }
    }

    private static int next_candidate_port_ = 18100;

    /// <summary>
    /// A free port that no other instance is being handed. The counter keeps concurrent test
    /// app instances apart; the availability check covers whatever else is on the machine.
    /// </summary>
    private static int ReservePort()
    {
        while (true)
        {
            int candidate = Interlocked.Increment(ref next_candidate_port_);
            if (candidate > 30000)
            {
                throw new InvalidOperationException("Ran out of ports to hand to test app instances.");
            }

            if (PortHelper.IsPortAvailable(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Pins this instance's ports in its appsettings.json. <c>Ua:EndpointUrl</c> is what drives
    /// the UA server's actual bind address, so it has to move with <c>Bridge:OpcUaPort</c> or the
    /// two disagree and the server binds the old port.
    /// </summary>
    private static void WriteReservedPorts(string appDirectory, int httpPort, int uaPort)
    {
        string settingsPath = Path.Combine(appDirectory, "appsettings.json");
        if (!File.Exists(settingsPath))
        {
            return;
        }

        if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject settings)
        {
            return;
        }

        if (settings["Bridge"] is not JsonObject bridge)
        {
            bridge = new JsonObject();
            settings["Bridge"] = bridge;
        }

        bridge["HttpPort"] = httpPort;
        bridge["OpcUaPort"] = uaPort;

        if (settings["Ua"] is JsonObject ua &&
            ua["EndpointUrl"] is JsonValue endpoint &&
            endpoint.TryGetValue(out string? url) &&
            Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            ua["EndpointUrl"] = new UriBuilder(parsed) { Port = uaPort }.Uri.ToString();
        }

        File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
