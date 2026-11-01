using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpcBridge.Mobile.Core;

public enum OutboxKind
{
    Note,
    Write
}

/// <summary>
/// A queued note or action. <see cref="Sending"/> only exists while a flush is in flight; if
/// the app stops while it is set, the next load turns it into <see cref="Uncertain"/> because
/// the bridge may or may not have received it.
/// </summary>
public enum OutboxItemState
{
    Pending,
    Sending,
    Uncertain,
    Failed
}

public sealed class OutboxItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public OutboxKind Kind { get; set; }

    public DateTime QueuedUtc { get; set; } = DateTime.UtcNow;

    public Guid BlockId { get; set; }

    public string BlockName { get; set; } = string.Empty;

    public Guid? ConditionId { get; set; }

    public string? NoteText { get; set; }

    public string? SourceId { get; set; }

    public string? ItemId { get; set; }

    public string? TagLabel { get; set; }

    public string? ActionLabel { get; set; }

    /// <summary>The action's literal; parsed with <see cref="OutboxValue.Parse"/> when flushed.</summary>
    public string? ValueText { get; set; }

    public OutboxItemState State { get; set; } = OutboxItemState.Pending;

    public string? Error { get; set; }

    public int Attempts { get; set; }

    [JsonIgnore]
    public string KindLabel => Kind == OutboxKind.Note ? "Note" : "Action";

    [JsonIgnore]
    public string TargetText => Kind == OutboxKind.Note
        ? (string.IsNullOrWhiteSpace(BlockName) ? "Block note" : BlockName)
        : (string.IsNullOrWhiteSpace(TagLabel) ? (ItemId ?? "Tag") : TagLabel);

    [JsonIgnore]
    public string QueuedText => QueuedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string StateLabel => State switch
    {
        OutboxItemState.Pending => "pending",
        OutboxItemState.Sending => "sending…",
        OutboxItemState.Uncertain => "not confirmed",
        _ => "failed"
    };

    [JsonIgnore]
    public string StateKey => State switch
    {
        OutboxItemState.Pending => "pending",
        OutboxItemState.Sending => "syncing",
        OutboxItemState.Uncertain => "error",
        _ => "error"
    };

    [JsonIgnore]
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    [JsonIgnore]
    public bool IsActionable => State != OutboxItemState.Sending;

    [JsonIgnore]
    public string Detail => Kind == OutboxKind.Note
        ? NoteText ?? string.Empty
        : (string.IsNullOrWhiteSpace(ActionLabel) ? string.Empty : ActionLabel);
}

/// <summary>
/// One bridge's pending notes and actions, persisted as JSON behind
/// <see cref="IBridgeCacheStore"/> under <see cref="MobileBridge.OutboxKey"/>. A queued write
/// is only sent when it was never attempted (the app knew it was offline); an item left in
/// <see cref="OutboxItemState.Sending"/> by a crash becomes <see cref="OutboxItemState.Uncertain"/>
/// and waits for the operator.
/// </summary>
public sealed class MobileOutbox
{
    public const int MaxItems = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IBridgeCacheStore store_;
    private readonly string key_;
    private readonly object sync_ = new();
    private List<OutboxItem> items_;

    public MobileOutbox(IBridgeCacheStore store, string key)
    {
        store_ = store;
        key_ = key;
        items_ = Load();

        bool changed = false;
        foreach (OutboxItem item in items_)
        {
            if (item.State == OutboxItemState.Sending)
            {
                item.State = OutboxItemState.Uncertain;
                item.Error = "the app stopped before the bridge answered";
                changed = true;
            }
        }

        if (changed)
        {
            Persist();
        }
    }

    public IReadOnlyList<OutboxItem> All
    {
        get
        {
            lock (sync_)
            {
                return items_.ToArray();
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (sync_)
            {
                return items_.Count(item => item.State is OutboxItemState.Pending or OutboxItemState.Sending);
            }
        }
    }

    public int FailedCount
    {
        get
        {
            lock (sync_)
            {
                return items_.Count(item => item.State is OutboxItemState.Failed or OutboxItemState.Uncertain);
            }
        }
    }

    public bool TryAdd(OutboxItem item, out string? error, OutboxItemState state = OutboxItemState.Pending)
    {
        lock (sync_)
        {
            if (items_.Count >= MaxItems)
            {
                error = "the sync queue is full — retry or discard items in Settings ▸ Sync";
                return false;
            }

            item.State = state;
            items_.Add(item);
            PersistLocked();
            error = null;
            return true;
        }
    }

    public void Update(OutboxItem item)
    {
        lock (sync_)
        {
            PersistLocked();
        }
    }

    public void Remove(Guid id)
    {
        lock (sync_)
        {
            items_.RemoveAll(item => item.Id == id);
            PersistLocked();
        }
    }

    public void Retry(Guid id)
    {
        lock (sync_)
        {
            OutboxItem? item = items_.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null)
            {
                return;
            }

            // A manual retry is a fresh decision: a write that aged out can be sent now.
            item.State = OutboxItemState.Pending;
            item.Error = null;
            item.QueuedUtc = DateTime.UtcNow;
            PersistLocked();
        }
    }

    public void Reset()
    {
        lock (sync_)
        {
            items_.Clear();
            store_.Delete(key_);
        }
    }

    private List<OutboxItem> Load()
    {
        string? json = store_.Read(key_);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<OutboxItem>();
        }

        try
        {
            Envelope? envelope = JsonSerializer.Deserialize<Envelope>(json, JsonOptions);
            if (envelope?.Items is null)
            {
                return new List<OutboxItem>();
            }

            return envelope.Items
                .Where(item => item.Id != Guid.Empty)
                .OrderBy(item => item.QueuedUtc)
                .ToList();
        }
        catch (JsonException)
        {
            return new List<OutboxItem>();
        }
    }

    private void Persist()
    {
        lock (sync_)
        {
            PersistLocked();
        }
    }

    private void PersistLocked()
    {
        try
        {
            store_.Write(key_, JsonSerializer.Serialize(new Envelope { Items = items_ }, JsonOptions));
        }
        catch
        {
            // The next mutation retries; a store that cannot write behaves as a lost queue.
        }
    }

    private sealed class Envelope
    {
        public int Version { get; set; } = 1;

        public List<OutboxItem> Items { get; set; } = new();
    }
}

/// <summary>What one submit call did: sent, queued, rejected, uncertain or waiting for sign-in.</summary>
public enum OutboxSubmitKind
{
    Sent,
    Queued,
    Rejected,
    Uncertain,
    SignInRequired
}

public readonly record struct OutboxSubmitResult(OutboxSubmitKind Kind, string? Error = null)
{
    public bool Ok => Kind == OutboxSubmitKind.Sent;
}

/// <summary>The action literal's bool / number / text conversion, shared by live and queued writes.</summary>
public static class OutboxValue
{
    public static object Parse(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        if (bool.TryParse(text, out bool flag))
        {
            return flag;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return number;
        }

        return text;
    }
}
