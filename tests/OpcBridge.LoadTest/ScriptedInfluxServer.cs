using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpcBridge.LoadTest;

/// <summary>
/// A minimal InfluxDB fake for the writer tests: /ping always answers 204, and the write endpoint
/// answers a scripted sequence of (status, body) pairs — the last pair repeats. Request bodies are
/// captured so a test can assert exactly what the writer sent (the connect-time probe point).
/// </summary>
internal sealed class ScriptedInfluxServer : IDisposable
{
    /// <summary>
    /// The body InfluxDB returns when a field name is already locked to another type — the
    /// "wrong type / already exists as type" rejection the writer must report usefully.
    /// </summary>
    public const string FieldTypeConflictBody = """
        {"code":"invalid","message":"partial write: field type conflict: input field \"value\" on measurement \"opc_tags\" is type double, already exists as type string dropped=1"}
        """;

    private readonly HttpListener listener_;
    private readonly List<(int Status, string? Body)> responses_;
    private readonly List<string> write_bodies_ = new();
    private readonly object sync_ = new();
    private int response_index_;

    private ScriptedInfluxServer(HttpListener listener, string url, IEnumerable<(int Status, string? Body)> responses)
    {
        listener_ = listener;
        Url = url;
        responses_ = responses.ToList();
    }

    public string Url { get; }

    public IReadOnlyList<string> WriteBodies
    {
        get { lock (sync_) { return write_bodies_.ToList(); } }
    }

    public static ScriptedInfluxServer Start(params (int Status, string? Body)[] writeResponses)
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        ScriptedInfluxServer server = new(listener, $"http://127.0.0.1:{port}", writeResponses);
        _ = server.ServeAsync();
        return server;
    }

    private async Task ServeAsync()
    {
        while (listener_.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener_.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // stopped
            }

            try
            {
                bool ping = context.Request.Url?.AbsolutePath == "/ping";
                if (ping)
                {
                    context.Response.StatusCode = 204;
                }
                else
                {
                    using StreamReader reader = new(context.Request.InputStream, context.Request.ContentEncoding);
                    string body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    (int status, string? responseBody) = NextResponse();
                    lock (sync_) { write_bodies_.Add(body); }

                    context.Response.StatusCode = status;
                    if (!string.IsNullOrEmpty(responseBody))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(responseBody);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    }
                }

                context.Response.Headers["X-Influxdb-Version"] = "2.7.0";
                context.Response.Close();
            }
            catch (Exception)
            {
                // client hung up
            }
        }
    }

    private (int Status, string? Body) NextResponse()
    {
        lock (sync_)
        {
            if (responses_.Count == 0)
            {
                return (204, null);
            }

            (int Status, string? Body) response = responses_[Math.Min(response_index_, responses_.Count - 1)];
            response_index_++;
            return response;
        }
    }

    public void Dispose()
    {
        try
        {
            listener_.Stop();
            listener_.Close();
        }
        catch (Exception)
        {
            // already down
        }
    }
}
