using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.Core;

namespace OpcBridge.App;

public sealed class MqttRuntimeSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync_ = new();
    private readonly string persist_path_;
    private MqttBrokerOptions options_;
    private string state_ = "Disconnected";
    private string? last_error_;
    private long published_count_;
    private long received_count_;
    private DateTime rate_window_start_ = DateTime.UtcNow;
    private long rate_window_published_;
    private long rate_window_received_;
    private double published_rate_;
    private double received_rate_;

    public MqttRuntimeSettings(IOptions<MqttBrokerOptions> options)
    {
        persist_path_ = DataDirectory.Combine("mqtt.json");
        MqttBrokerOptions? loaded = LoadFromDisk();
        options_ = loaded ?? options.Value;
    }

    public MqttBrokerOptions GetOptions()
    {
        lock (sync_) { return options_; }
    }

    public MqttRuntimeSnapshot GetSnapshot()
    {
        lock (sync_)
        {
            DateTime now = DateTime.UtcNow;
            double elapsed = (now - rate_window_start_).TotalSeconds;
            if (elapsed >= 1.0)
            {
                published_rate_ = rate_window_published_ / elapsed;
                received_rate_ = rate_window_received_ / elapsed;
                rate_window_published_ = 0;
                rate_window_received_ = 0;
                rate_window_start_ = now;
            }
            return new MqttRuntimeSnapshot(state_, last_error_, published_count_, received_count_, published_rate_, received_rate_, options_);
        }
    }

    public void ResetCounters()
    {
        lock (sync_)
        {
            published_count_ = 0;
            received_count_ = 0;
            rate_window_published_ = 0;
            rate_window_received_ = 0;
            rate_window_start_ = DateTime.UtcNow;
            published_rate_ = 0;
            received_rate_ = 0;
        }
    }

    public void UpsertOptions(MqttBrokerOptions updated)
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

    public void IncrementPublished() { lock (sync_) { published_count_++; rate_window_published_++; } }
    public void IncrementReceived() { lock (sync_) { received_count_++; rate_window_received_++; } }

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

    private MqttBrokerOptions? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(persist_path_)) return null;
            return JsonSerializer.Deserialize<MqttBrokerOptions>(File.ReadAllText(persist_path_));
        }
        catch
        {
            return null;
        }
    }
}

public sealed record MqttRuntimeSnapshot(
    string State,
    string? LastError,
    long PublishedCount,
    long ReceivedCount,
    double PublishedRate,
    double ReceivedRate,
    MqttBrokerOptions Options);
