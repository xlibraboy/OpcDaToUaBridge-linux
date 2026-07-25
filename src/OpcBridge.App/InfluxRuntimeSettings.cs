using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.Core;

namespace OpcBridge.App;

public sealed class InfluxRuntimeSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync_ = new();
    private readonly string persist_path_;
    private InfluxOptions options_;
    private string state_ = "Disconnected";
    private string? last_error_;
    private long written_count_;
    private DateTime rate_window_start_ = DateTime.UtcNow;
    private long rate_window_written_;
    private double written_rate_;

    public InfluxRuntimeSettings(IOptions<InfluxOptions> options)
    {
        persist_path_ = Path.Combine(AppContext.BaseDirectory, "influx.json");
        InfluxOptions? loaded = LoadFromDisk();
        options_ = loaded ?? options.Value;
    }

    public InfluxOptions GetOptions()
    {
        lock (sync_) { return options_; }
    }

    public InfluxRuntimeSnapshot GetSnapshot()
    {
        lock (sync_)
        {
            DateTime now = DateTime.UtcNow;
            double elapsed = (now - rate_window_start_).TotalSeconds;
            if (elapsed >= 1.0)
            {
                written_rate_ = rate_window_written_ / elapsed;
                rate_window_written_ = 0;
                rate_window_start_ = now;
            }
            return new InfluxRuntimeSnapshot(state_, last_error_, written_count_, written_rate_, options_);
        }
    }

    public void ResetCounters()
    {
        lock (sync_)
        {
            written_count_ = 0;
            rate_window_written_ = 0;
            rate_window_start_ = DateTime.UtcNow;
            written_rate_ = 0;
        }
    }

    public void UpsertOptions(InfluxOptions updated)
    {
        lock (sync_)
        {
            options_ = updated;
            Persist();
        }
    }

    public void SetState(string state, string? lastError = null)
    {
        lock (sync_)
        {
            state_ = state;
            last_error_ = lastError;
        }
    }

    public void IncrementWritten() { lock (sync_) { written_count_++; rate_window_written_++; } }

    private void Persist()
    {
        try
        {
            lock (sync_)
            {
                string json = JsonSerializer.Serialize(options_, JsonOptions);
                File.WriteAllText(persist_path_, json);
            }
        }
        catch
        {
        }
    }

    private InfluxOptions? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(persist_path_)) return null;
            return JsonSerializer.Deserialize<InfluxOptions>(File.ReadAllText(persist_path_));
        }
        catch
        {
            return null;
        }
    }
}

public sealed record InfluxRuntimeSnapshot(
    string State,
    string? LastError,
    long WrittenCount,
    double WrittenRate,
    InfluxOptions Options);
