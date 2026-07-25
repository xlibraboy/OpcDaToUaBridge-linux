using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using Microsoft.Extensions.Logging;
using OpcBridge.Core;

namespace OpcBridge.Influx;

public sealed class InfluxWriter : IInfluxWriter
{
    private readonly ILogger<InfluxWriter> logger_;
    private readonly object sync_ = new();
    private InfluxDBClient? client_;
    private WriteApiAsync? writeApi_;
    private InfluxOptions? options_;
    private InfluxConnectionState state_ = InfluxConnectionState.Disconnected;

    public InfluxWriter(ILogger<InfluxWriter> logger)
    {
        logger_ = logger;
    }

    public InfluxConnectionState State
    {
        get { lock (sync_) { return state_; } }
    }

    public event Action<InfluxConnectionState>? StateChanged;

    public async Task ConnectAsync(InfluxOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        string url = options.Url?.Trim() ?? string.Empty;
        string org = options.Org?.Trim() ?? string.Empty;
        string bucket = options.Bucket?.Trim() ?? string.Empty;
        string? token = options.Token?.Trim();

        if (string.IsNullOrWhiteSpace(url)
            || string.IsNullOrWhiteSpace(org)
            || string.IsNullOrWhiteSpace(bucket)
            || string.IsNullOrWhiteSpace(token))
        {
            SetState(InfluxConnectionState.Faulted);
            logger_.LogWarning("Influx connect failed: Url, Org, Bucket, and Token are required");
            throw new InvalidOperationException("Influx Url, Org, Bucket, and Token are required.");
        }

        await DisconnectAsync(ct).ConfigureAwait(false);
        SetState(InfluxConnectionState.Connecting);

        try
        {
            InfluxDBClientOptions clientOptions = InfluxDBClientOptions.Builder
                .CreateNew()
                .Url(url)
                .AuthenticateToken(token!)
                .Org(org)
                .Bucket(bucket)
                .TimeOut(TimeSpan.FromMilliseconds(Math.Max(1, options.TimeoutMs)))
                .VerifySsl(options.VerifySsl)
                .Build();

            InfluxDBClient client = new InfluxDBClient(clientOptions);
            WriteApiAsync writeApi = client.GetWriteApiAsync();

            // Probe write API construction; network validation deferred to first write.
            _ = writeApi;

            lock (sync_)
            {
                client_ = client;
                writeApi_ = writeApi;
                options_ = CloneOptions(options, url, org, bucket, token!);
            }

            SetState(InfluxConnectionState.Connected);
            logger_.LogInformation("Influx connected to {Url} org={Org} bucket={Bucket}", url, org, bucket);
        }
        catch (Exception ex)
        {
            await SafeDisposeClientAsync().ConfigureAwait(false);
            SetState(InfluxConnectionState.Faulted);
            logger_.LogWarning(ex, "Influx connect failed for {Url}", url);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await SafeDisposeClientAsync().ConfigureAwait(false);
        SetState(InfluxConnectionState.Disconnected);
    }

    public async Task WritePointAsync(BridgeValue value, string? displayName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);

        InfluxDBClient? client;
        WriteApiAsync? writeApi;
        InfluxOptions? options;
        InfluxConnectionState state;
        lock (sync_)
        {
            state = state_;
            client = client_;
            writeApi = writeApi_;
            options = options_;
        }

        if (state != InfluxConnectionState.Connected || client is null || writeApi is null || options is null)
        {
            return;
        }

        InfluxPointModel model = InfluxPointBuilder.Build(options, value, displayName);
        PointData point = BuildPointData(model);

        try
        {
            await writeApi.WritePointAsync(point, options.Bucket, options.Org, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger_.LogWarning(
                ex,
                "Influx write failed for source={SourceId} item={DaItemId}",
                value.SourceId,
                value.DaItemId);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static PointData BuildPointData(InfluxPointModel model)
    {
        PointData point = PointData.Measurement(model.Measurement);

        foreach ((string key, string tagValue) in model.Tags)
        {
            point = point.Tag(key, tagValue);
        }

        point = model.ValueFieldKind switch
        {
            "bool" => point.Field("value", (bool)model.ValueField!),
            "long" => point.Field("value", (long)model.ValueField!),
            "double" => point.Field("value", Convert.ToDouble(model.ValueField!)),
            "string" => point.Field("value", model.ValueField?.ToString() ?? string.Empty),
            _ => point // null kind: omit value field
        };

        point = point
            .Field("quality", model.Quality)
            .Field("is_good", model.IsGood)
            .Timestamp(model.TimestampUtc, WritePrecision.Ns);

        return point;
    }

    private static InfluxOptions CloneOptions(InfluxOptions source, string url, string org, string bucket, string token)
    {
        return new InfluxOptions
        {
            Enabled = source.Enabled,
            Url = url,
            Org = org,
            Bucket = bucket,
            Token = token,
            Measurement = string.IsNullOrWhiteSpace(source.Measurement) ? "opc_tags" : source.Measurement.Trim(),
            TimeoutMs = source.TimeoutMs,
            VerifySsl = source.VerifySsl
        };
    }

    private async Task SafeDisposeClientAsync()
    {
        InfluxDBClient? client;
        lock (sync_)
        {
            client = client_;
            client_ = null;
            writeApi_ = null;
            options_ = null;
        }

        if (client is null)
        {
            return;
        }

        try
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            logger_.LogWarning(ex, "Influx client dispose failed");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void SetState(InfluxConnectionState state)
    {
        lock (sync_) { state_ = state; }
        StateChanged?.Invoke(state);
    }
}
