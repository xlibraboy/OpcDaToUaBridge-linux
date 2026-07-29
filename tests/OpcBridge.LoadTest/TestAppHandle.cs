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
        Client = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:8080")
        };
    }

    public HttpClient Client { get; }

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

    private void AppendOutput(string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            output_.AppendLine(line);
        }
    }

    private async Task WaitForHealthyAsync()
    {
        for (int attempt = 0; attempt < 80; attempt++)
        {
            if (process_.HasExited)
            {
                throw new Xunit.Sdk.XunitException($"OpcBridge.App exited during startup with code {process_.ExitCode}.{Environment.NewLine}{output_}");
            }

            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(250));
                using HttpResponseMessage response = await Client.GetAsync("/health", timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
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
}
