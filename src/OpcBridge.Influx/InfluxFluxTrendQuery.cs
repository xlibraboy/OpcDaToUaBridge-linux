using System.Globalization;
using InfluxDB.Client;
using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.Influx;

/// <summary>
/// Queries historical tag samples from InfluxDB via Flux.
/// Options/token come from the bridge host only (never from HMI clients).
/// </summary>
public sealed class InfluxFluxTrendQuery : IInfluxTrendQuery
{
    private readonly Func<InfluxOptions> options_provider_;
    private readonly ILogger<InfluxFluxTrendQuery> logger_;

    public InfluxFluxTrendQuery(Func<InfluxOptions> optionsProvider, ILogger<InfluxFluxTrendQuery> logger)
    {
        options_provider_ = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        logger_ = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HmiTrendResponse> QueryAsync(
        string sourceId,
        string itemId,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints,
        CancellationToken ct)
    {
        InfluxOptions options = options_provider_();
        string url = options.Url?.Trim() ?? string.Empty;
        string org = options.Org?.Trim() ?? string.Empty;
        string bucket = options.Bucket?.Trim() ?? string.Empty;
        string? token = options.Token?.Trim();
        string measurement = string.IsNullOrWhiteSpace(options.Measurement) ? "opc_tags" : options.Measurement.Trim();

        DateTime from = EnsureUtc(fromUtc);
        DateTime to = EnsureUtc(toUtc);

        if (!options.Enabled
            || string.IsNullOrWhiteSpace(url)
            || string.IsNullOrWhiteSpace(org)
            || string.IsNullOrWhiteSpace(bucket)
            || string.IsNullOrWhiteSpace(token))
        {
            return Empty(sourceId, itemId, from, to, "Influx not available");
        }

        int limit = maxPoints;
        if (limit < 10) limit = 10;
        if (limit > 2000) limit = 2000;

        if (from > to)
        {
            return Empty(sourceId, itemId, from, to, "from must be less than or equal to to");
        }

        string flux = BuildFlux(bucket, measurement, sourceId, itemId, from, to, limit);

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

            using InfluxDBClient client = new(clientOptions);
            List<FluxTable> tables = await client.GetQueryApi()
                .QueryAsync(flux, org, ct)
                .ConfigureAwait(false);

            List<HmiTrendPoint> points = MapTables(tables);
            bool truncated = points.Count >= limit;

            return new HmiTrendResponse
            {
                SourceId = sourceId,
                ItemId = itemId,
                FromUtc = from,
                ToUtc = to,
                Points = points,
                Truncated = truncated,
                Error = null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger_.LogWarning(ex, "Influx trend query failed for {SourceId}/{ItemId}", sourceId, itemId);
            return Empty(sourceId, itemId, from, to, "Influx query failed");
        }
    }

    internal static string BuildFlux(
        string bucket,
        string measurement,
        string sourceId,
        string itemId,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints)
    {
        string b = EscapeFluxString(bucket);
        string m = EscapeFluxString(measurement);
        string s = EscapeFluxString(sourceId);
        string d = EscapeFluxString(itemId);
        string start = fromUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        string stop = toUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        // Most recent maxPoints samples, then chronological for charting.
        return $@"from(bucket: ""{b}"")
  |> range(start: time(v: ""{start}""), stop: time(v: ""{stop}""))
  |> filter(fn: (r) => r._measurement == ""{m}"")
  |> filter(fn: (r) => r.source_id == ""{s}"" and r.da_item_id == ""{d}"")
  |> filter(fn: (r) => r._field == ""value"" or r._field == ""quality"" or r._field == ""is_good"")
  |> pivot(rowKey: [""_time""], columnKey: [""_field""], valueColumn: ""_value"")
  |> sort(columns: [""_time""], desc: true)
  |> limit(n: {maxPoints.ToString(CultureInfo.InvariantCulture)})
  |> sort(columns: [""_time""])
";
    }

    internal static string EscapeFluxString(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }

    internal static List<HmiTrendPoint> MapTables(IReadOnlyList<FluxTable> tables)
    {
        List<HmiTrendPoint> points = new();
        if (tables is null || tables.Count == 0)
        {
            return points;
        }

        foreach (FluxTable table in tables)
        {
            foreach (FluxRecord record in table.Records)
            {
                if (!TryGetRecordTime(record, out DateTime t))
                {
                    continue;
                }

                object? value = GetRecordValue(record, "value");
                object? qualityObj = GetRecordValue(record, "quality");
                object? goodObj = GetRecordValue(record, "is_good");

                int? q = null;
                if (qualityObj is not null
                    && long.TryParse(
                        Convert.ToString(qualityObj, CultureInfo.InvariantCulture),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long ql))
                {
                    q = (int)ql;
                }

                bool? good = null;
                if (goodObj is bool gb)
                {
                    good = gb;
                }
                else if (goodObj is not null
                    && bool.TryParse(Convert.ToString(goodObj, CultureInfo.InvariantCulture), out bool gb2))
                {
                    good = gb2;
                }

                points.Add(new HmiTrendPoint
                {
                    T = t,
                    V = value,
                    Q = q,
                    Good = good
                });
            }
        }

        points.Sort((a, b) => a.T.CompareTo(b.T));
        return points;
    }

    private static bool TryGetRecordTime(FluxRecord record, out DateTime t)
    {
        t = default;
        try
        {
            object? timeObj = record.GetTime();
            if (timeObj is not null)
            {
                // Influx client returns NodaTime.Instant
                System.Reflection.MethodInfo? toUtc = timeObj.GetType().GetMethod("ToDateTimeUtc");
                if (toUtc is not null)
                {
                    object? converted = toUtc.Invoke(timeObj, null);
                    if (converted is DateTime dt)
                    {
                        t = EnsureUtc(dt);
                        return true;
                    }
                }
            }
        }
        catch
        {
            // fall through
        }

        object? raw = GetRecordValue(record, "_time");
        if (raw is DateTime dt2)
        {
            t = EnsureUtc(dt2);
            return true;
        }

        if (raw is not null
            && DateTime.TryParse(
                Convert.ToString(raw, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTime parsed))
        {
            t = EnsureUtc(parsed);
            return true;
        }

        return false;
    }

    private static object? GetRecordValue(FluxRecord record, string key)
    {
        try
        {
            return record.GetValueByKey(key);
        }
        catch
        {
            if (record.Values is not null && record.Values.TryGetValue(key, out object? v))
            {
                return v;
            }

            return null;
        }
    }

    private static HmiTrendResponse Empty(string sourceId, string itemId, DateTime from, DateTime to, string? error)
    {
        return new HmiTrendResponse
        {
            SourceId = sourceId,
            ItemId = itemId,
            FromUtc = from,
            ToUtc = to,
            Points = Array.Empty<HmiTrendPoint>(),
            Truncated = false,
            Error = error
        };
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
