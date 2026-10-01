using System.Text.Json;

namespace OpcBridge.Client.Workers;

/// <summary>Wire contract for the parent ↔ DA worker named pipe.</summary>
public static class WorkerProtocol
{
    /// <summary>Protocol revision; the worker refuses a bootstrap it does not understand.</summary>
    public const string Version = "1";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>Frame type discriminators. Requests are answered with the same <c>Seq</c>.</summary>
public static class WorkerFrameTypes
{
    // worker -> parent
    public const string Ready = "ready";
    public const string State = "state";
    public const string Values = "values";
    public const string ReadResult = "readResult";
    public const string WriteResult = "writeResult";
    public const string MetadataResult = "metadataResult";
    public const string MetadataDump = "metadataDump";
    public const string Notice = "notice";
    public const string Heartbeat = "heartbeat";
    public const string Pong = "pong";
    public const string Fatal = "fatal";

    // parent -> worker
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string UpsertSource = "upsertSource";
    public const string RemoveSource = "removeSource";
    public const string Read = "read";
    public const string Write = "write";
    public const string Metadata = "metadata";
    public const string Ping = "ping";
    public const string Shutdown = "shutdown";
}

/// <summary>
/// One protocol frame. <see cref="Seq"/> correlates a response with its request; worker-
/// initiated pushes (values, state, heartbeat) carry the worker's own increasing sequence.
/// </summary>
public sealed record WorkerFrame(string Type, long Seq, JsonElement Payload)
{
    public static WorkerFrame Create<T>(string type, long seq, T payload)
        => new(type, seq, JsonSerializer.SerializeToElement(payload, WorkerProtocol.JsonOptions));

    public T? PayloadAs<T>() where T : class
        => Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : Payload.Deserialize<T>(WorkerProtocol.JsonOptions);
}

// --- bootstrap: one JSON line on stdin at worker start, then stdin closes ---

public sealed record WorkerBootstrap(
    string ProtocolVersion,
    string WorkerId,
    string PipeName,
    int ParentPid,
    IReadOnlyList<WorkerSourceConfig> Sources);

public sealed record WorkerSourceConfig(
    string SourceId,
    string ProgId,
    string Host,
    string? RemoteUsername,
    string? RemotePassword,
    string? RemoteDomain,
    string IoMode,
    int UpdateRateMs,
    bool UseSubscriptions,
    IReadOnlyList<WorkerGroupIoMode> GroupIoModes,
    int WatchdogTimeoutMs);

public sealed record WorkerGroupIoMode(string Name, int Rate, string IoMode);

// --- worker -> parent payloads ---

public sealed record WorkerReady(string ProtocolVersion, int Pid, string Account);

/// <summary>A source connection-state change; <paramref name="Transient"/> tells the parent
/// whether to retry (SourceConnectionLostException) or fault the source.</summary>
public sealed record WorkerStateChange(string SourceId, string State, string? Error, bool Transient);

public sealed record WorkerValues(IReadOnlyList<WireValue> Values);

public sealed record WorkerReadResult(IReadOnlyList<WireValue> Values, string? Error);

public sealed record WorkerWriteResult(bool Ok, string? Error);

public sealed record WorkerMetadataEntry(string ItemId, short? CanonicalDataType, int? AccessRights);

public sealed record WorkerMetadataResult(bool Found, short? CanonicalDataType, int? AccessRights);

public sealed record WorkerMetadataDump(IReadOnlyList<WorkerMetadataEntry> Entries);

public sealed record WorkerNotice(string Level, string Message);

public sealed record WorkerHeartbeat(int SourceCount, long UptimeMs, long PushSeq);

public sealed record WorkerFatal(string Message, string? Detail);

public sealed record WorkerPong(long WorkerSeq);

// --- parent -> worker payloads ---

public sealed record WorkerConnectRequest(string SourceId);

public sealed record WorkerDisconnectRequest(string SourceId);

public sealed record WorkerUpsertSource(WorkerSourceConfig Config);

public sealed record WorkerRemoveSource(string SourceId);

public sealed record WorkerTagRef(string ItemId);

public sealed record WorkerReadRequest(IReadOnlyList<WorkerTagRef> Tags);

public sealed record WorkerWriteRequest(string ItemId, string Type, JsonElement Value);

public sealed record WorkerMetadataRequest(string ItemId);

public sealed record WorkerShutdownRequest(int GraceMs);
