namespace OpcBridge.App.Presence;

/// <summary>A connected client, collapsed across the links it holds to the bridge.</summary>
internal sealed record ClientPresenceEntry(
    string ClientId,
    string Kind,
    string MachineName,
    string Address,
    string Username,
    string Platform,
    string UserAgent,
    string AppVersion,
    DateTime ConnectedAtUtc,
    DateTime LastSeenUtc,
    int ConnectionCount);

/// <summary>
/// The bridge's live clients: one entry per app instance and signed-in account, holding the
/// hub connections that instance currently has open. A reconnect reuses the same client id,
/// so the row does not flicker; the entry disappears when its last link closes.
/// </summary>
public sealed class ClientPresenceStore
{
    private sealed class Entry
    {
        public Entry(ClientPresenceConnection connection, DateTime connectedAtUtc)
        {
            ClientId = connection.ClientId;
            Kind = connection.Kind;
            MachineName = connection.MachineName;
            Address = connection.Address;
            Username = connection.Username;
            Platform = connection.Platform;
            UserAgent = connection.UserAgent;
            AppVersion = connection.AppVersion;
            ConnectedAtUtc = connectedAtUtc;
            LastSeenUtc = connectedAtUtc;
        }

        public string ClientId { get; }
        public string Kind { get; }
        public string MachineName { get; set; }
        public string Address { get; set; }
        public string Username { get; }
        public string Platform { get; set; }
        public string UserAgent { get; set; }
        public string AppVersion { get; set; }
        public DateTime ConnectedAtUtc { get; }
        public DateTime LastSeenUtc { get; set; }
        public HashSet<string> Connections { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, Entry> entries_ = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> connectionKeys_ = new(StringComparer.Ordinal);
    private readonly object sync_ = new();
    private readonly TimeProvider clock_;

    public ClientPresenceStore()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Test seam: the connected/last-seen stamps come from this clock.</summary>
    internal ClientPresenceStore(TimeProvider clock)
    {
        clock_ = clock;
    }

    internal int Count
    {
        get
        {
            lock (sync_)
            {
                return entries_.Count;
            }
        }
    }

    internal void Connect(ClientPresenceConnection connection)
    {
        DateTime now = clock_.GetUtcNow().UtcDateTime;
        lock (sync_)
        {
            if (!entries_.TryGetValue(connection.ClientKey, out Entry? entry))
            {
                entry = new Entry(connection, now);
                entries_[connection.ClientKey] = entry;
            }
            else
            {
                // A reconnect can carry a value the first link did not (a WebView page gains
                // its machine name from the host); newer non-empty claims win.
                if (connection.MachineName.Length > 0)
                {
                    entry.MachineName = connection.MachineName;
                }

                if (connection.Platform.Length > 0)
                {
                    entry.Platform = connection.Platform;
                }

                if (connection.AppVersion.Length > 0)
                {
                    entry.AppVersion = connection.AppVersion;
                }

                if (connection.Address.Length > 0)
                {
                    entry.Address = connection.Address;
                }

                if (connection.UserAgent.Length > 0)
                {
                    entry.UserAgent = connection.UserAgent;
                }
            }

            entry.Connections.Add(connection.ConnectionId);
            entry.LastSeenUtc = now;
            connectionKeys_[connection.ConnectionId] = connection.ClientKey;
        }
    }

    internal void Disconnect(string connectionId)
    {
        lock (sync_)
        {
            if (!connectionKeys_.Remove(connectionId, out string? key)
                || !entries_.TryGetValue(key, out Entry? entry))
            {
                return;
            }

            entry.Connections.Remove(connectionId);
            entry.LastSeenUtc = clock_.GetUtcNow().UtcDateTime;
            if (entry.Connections.Count == 0)
            {
                entries_.Remove(key);
            }
        }
    }

    internal IReadOnlyList<ClientPresenceEntry> Snapshot()
    {
        lock (sync_)
        {
            return entries_.Values
                .OrderBy(entry => KindRank(entry.Kind))
                .ThenBy(entry => entry.MachineName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Username, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new ClientPresenceEntry(
                    entry.ClientId,
                    entry.Kind,
                    entry.MachineName,
                    entry.Address,
                    entry.Username,
                    entry.Platform,
                    entry.UserAgent,
                    entry.AppVersion,
                    entry.ConnectedAtUtc,
                    entry.LastSeenUtc,
                    entry.Connections.Count))
                .ToArray();
        }
    }

    private static int KindRank(string kind) => kind switch
    {
        "hmi" => 0,
        "logic" => 1,
        "mobile" => 2,
        "browser" => 3,
        _ => 4
    };
}
