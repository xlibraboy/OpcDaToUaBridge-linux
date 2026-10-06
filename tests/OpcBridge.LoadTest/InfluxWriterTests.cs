using System.Text.Json;
using InfluxDB.Client.Core.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.Core;
using OpcBridge.Influx;
using Xunit;

namespace OpcBridge.LoadTest;

// Joins InterlinkApiAppCollection because MappingStore_Preserves_InfluxEnabled deletes and
// recreates AppContext.BaseDirectory/mappings.json — the same shared file other
// MappingStore tests round-trip; parallel collections would race on it.
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class InfluxWriterTests
{
    [Fact]
    public void InfluxOptions_Defaults_AreSafe()
    {
        InfluxOptions options = new();
        Assert.False(options.Enabled);
        Assert.Equal("http://localhost:8086", options.Url);
        Assert.Equal(string.Empty, options.Org);
        Assert.Equal(string.Empty, options.Bucket);
        Assert.Null(options.Token);
        Assert.Equal("opc_tags", options.Measurement);
        Assert.Equal(5000, options.TimeoutMs);
        Assert.True(options.VerifySsl);
    }

    [Fact]
    public void TagMapping_InfluxEnabled_JsonRoundTrip()
    {
        TagMapping tag = new()
        {
            SourceId = "default",
            ItemId = "Random.Int1",
            InfluxEnabled = true
        };

        string json = JsonSerializer.Serialize(tag);
        TagMapping? roundTrip = JsonSerializer.Deserialize<TagMapping>(json);
        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.InfluxEnabled);
    }

    [Fact]
    public void MappingStore_Preserves_InfluxEnabled()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "mappings.json");
        if (File.Exists(path)) File.Delete(path);

        MappingStore store = new(Options.Create(new BridgeOptions()));
        store.Add(
        [
            new TagMapping
            {
                SourceId = "default",
                ItemId = "tag.a",
                DisplayName = "A",
                InfluxEnabled = true
            }
        ]);

        (IReadOnlyList<TagMapping> snapshot, _) = store.GetSnapshot();
        TagMapping mapping = Assert.Single(snapshot.Where(m => m.ItemId == "tag.a"));
        Assert.True(mapping.InfluxEnabled);
    }

    [Theory]
    [InlineData(true, "value_bool")]
    [InlineData((byte)7, "value_int")]
    [InlineData((sbyte)7, "value_int")]
    [InlineData((short)7, "value_int")]
    [InlineData((ushort)7, "value_int")]
    [InlineData(7, "value_int")]
    [InlineData(7U, "value_int")]
    [InlineData((long)7, "value_int")]
    [InlineData(7UL, "value_int")]
    [InlineData(3.5f, "value")]
    [InlineData(3.5, "value")]
    [InlineData("hi", "value_str")]
    [InlineData('x', "value_str")]
    public void InfluxPointBuilder_Types_MapToTypeStableValueField(object raw, string fieldName)
    {
        InfluxOptions options = new() { Measurement = "opc_tags" };
        BridgeValue value = new("src", "item.1", raw, DateTime.UtcNow, 192, true);
        InfluxPointModel point = InfluxPointBuilder.Build(options, value, "Name");
        Assert.Equal("opc_tags", point.Measurement);
        Assert.Equal("src", point.Tags["source_id"]);
        Assert.Equal("item.1", point.Tags["da_item_id"]);
        Assert.Equal("Name", point.Tags["display_name"]);
        Assert.Equal(fieldName, point.ValueFieldName);
        Assert.Equal(192, point.Quality);
        Assert.True(point.IsGood);
    }

    [Fact]
    public void InfluxPointBuilder_Decimal_StoresAsDouble()
    {
        InfluxOptions options = new();
        BridgeValue value = new("src", "item.1", 1.25m, DateTime.UtcNow, 192, true);
        InfluxPointModel point = InfluxPointBuilder.Build(options, value, null);
        Assert.Equal("value", point.ValueFieldName);
        Assert.Equal(1.25, Assert.IsType<double>(point.ValueField));
    }

    [Fact]
    public void InfluxPointBuilder_UlongBeyondInt64_FallsToTheDoubleField()
    {
        InfluxOptions options = new();
        BridgeValue value = new("src", "item.1", ulong.MaxValue, DateTime.UtcNow, 192, true);
        InfluxPointModel point = InfluxPointBuilder.Build(options, value, null);
        Assert.Equal("value", point.ValueFieldName);
        Assert.Equal((double)ulong.MaxValue, Assert.IsType<double>(point.ValueField));
    }

    [Fact]
    public void InfluxPointBuilder_ByteString_StoresBase64()
    {
        InfluxOptions options = new();
        BridgeValue value = new("src", "item.1", new byte[] { 1, 2, 3 }, DateTime.UtcNow, 192, true);
        InfluxPointModel point = InfluxPointBuilder.Build(options, value, null);
        Assert.Equal("value_str", point.ValueFieldName);
        Assert.Equal("AQID", Assert.IsType<string>(point.ValueField));
    }

    [Fact]
    public void InfluxPointBuilder_DateTime_StoresIsoString()
    {
        InfluxOptions options = new();
        DateTime utc = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        BridgeValue value = new("src", "item.1", utc, DateTime.UtcNow, 192, true);
        InfluxPointModel point = InfluxPointBuilder.Build(options, value, null);
        Assert.Equal("value_str", point.ValueFieldName);
        Assert.Equal("2026-01-02T03:04:05.0000000Z", Assert.IsType<string>(point.ValueField));
    }

    [Fact]
    public void InfluxPointBuilder_NoValue_OmitsTheValueField()
    {
        InfluxOptions options = new();
        BridgeValue value = new("src", "item.1", null, DateTime.UtcNow, 0, false);
        InfluxPointModel point = InfluxPointBuilder.Build(options, value, null);
        Assert.Equal(string.Empty, point.ValueFieldName);
        Assert.Null(point.ValueField);
    }

    [Fact]
    public void InfluxPointBuilder_Omits_EmptyDisplayName()
    {
        InfluxOptions options = new();
        BridgeValue value = new("src", "item.1", 1L, DateTime.UtcNow, 0, false);
        InfluxPointModel point = InfluxPointBuilder.Build(options, value, "  ");
        Assert.False(point.Tags.ContainsKey("display_name"));
    }

    [Fact]
    public async Task Connect_UnreachableServer_FaultsInsteadOfClaimingConnected()
    {
        await using InfluxWriter writer = new(NullLogger<InfluxWriter>.Instance);

        // Port 9 is the discard port: nothing listens, so the probe is refused immediately.
        InfluxOptions options = new()
        {
            Enabled = true,
            Url = "http://127.0.0.1:9",
            Org = "demo-org",
            Bucket = "demo-bucket",
            Token = "demo-token",
            TimeoutMs = 1000,
            VerifySsl = false
        };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.ConnectAsync(options, CancellationToken.None));

        // The message must name the host: it is what the dashboard shows as Last error.
        Assert.Contains("127.0.0.1:9", error.Message);
        Assert.Equal(InfluxConnectionState.Faulted, writer.State);
    }

    [Fact]
    public async Task Connect_UnreachableServer_NeverRaisesConnected()
    {
        await using InfluxWriter writer = new(NullLogger<InfluxWriter>.Instance);
        List<InfluxConnectionState> seen = new();
        writer.StateChanged += state => seen.Add(state);

        InfluxOptions options = new()
        {
            Enabled = true,
            Url = "http://127.0.0.1:9",
            Org = "demo-org",
            Bucket = "demo-bucket",
            Token = "demo-token",
            TimeoutMs = 1000,
            VerifySsl = false
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ConnectAsync(options, CancellationToken.None));

        // Connected must never be announced for a server that did not answer. (The run opens with
        // a Disconnected from the pre-connect teardown, so only the absence is asserted here.)
        Assert.Contains(InfluxConnectionState.Connecting, seen);
        Assert.DoesNotContain(InfluxConnectionState.Connected, seen);
        Assert.Equal(InfluxConnectionState.Faulted, seen[^1]);
    }

    [Fact]
    public async Task Connect_MissingCredentials_FaultsBeforeProbing()
    {
        await using InfluxWriter writer = new(NullLogger<InfluxWriter>.Instance);
        InfluxOptions options = new()
        {
            Enabled = true,
            Url = "http://127.0.0.1:9",
            Org = string.Empty,
            Bucket = string.Empty,
            Token = null
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ConnectAsync(options, CancellationToken.None));
        Assert.Equal(InfluxConnectionState.Faulted, writer.State);
    }

    [Fact]
    public async Task Write_Failure_FaultsTheWriterSoItStopsClaimingConnected()
    {
        // The probe lands (204), the next real point is rejected (401): the shape of a historian
        // that is up with a token it stopped accepting between connect and the next point.
        using ScriptedInfluxServer server = ScriptedInfluxServer.Start((204, null), (401, null));
        await using InfluxWriter writer = new(NullLogger<InfluxWriter>.Instance);
        InfluxOptions options = new()
        {
            Enabled = true,
            Url = server.Url,
            Org = "demo-org",
            Bucket = "demo-bucket",
            Token = "demo-token",
            TimeoutMs = 2000,
            VerifySsl = false
        };

        await writer.ConnectAsync(options, CancellationToken.None);
        Assert.Equal(InfluxConnectionState.Connected, writer.State);

        BridgeValue value = new("src", "item.1", 1.5, DateTime.UtcNow, 192, true);
        await Assert.ThrowsAnyAsync<Exception>(() => writer.WritePointAsync(value, "Tag", CancellationToken.None));

        // The point never landed, so the link is not usable and the state must not stay Connected:
        // BridgeWorker's reconnect loop is what brings it back.
        Assert.Equal(InfluxConnectionState.Faulted, writer.State);
    }

    [Fact]
    public async Task Connect_ProbeWrite_LandsEveryValueFieldInTheConfiguredMeasurement()
    {
        using ScriptedInfluxServer server = ScriptedInfluxServer.Start((204, null));
        await using InfluxWriter writer = new(NullLogger<InfluxWriter>.Instance);
        InfluxOptions options = new()
        {
            Enabled = true,
            Url = server.Url,
            Org = "demo-org",
            Bucket = "demo-bucket",
            Token = "demo-token",
            Measurement = "plant_tags",
            TimeoutMs = 2000,
            VerifySsl = false
        };

        await writer.ConnectAsync(options, CancellationToken.None);

        Assert.Equal(InfluxConnectionState.Connected, writer.State);
        string probe = Assert.Single(server.WriteBodies);
        Assert.StartsWith("plant_tags,", probe);
        Assert.Contains("source_id=__opcbridge__", probe);
        Assert.Contains("da_item_id=probe", probe);
        Assert.Contains("value=0", probe);
        Assert.Contains("value_int=0i", probe);
        Assert.Contains("value_bool=false", probe);
        Assert.Contains("value_str=\"probe\"", probe);
        Assert.Contains("quality=0i", probe);
        Assert.Contains("is_good=true", probe);
    }

    [Fact]
    public async Task Connect_ProbeRejectedWithFieldTypeConflict_FaultsWithTheServerMessage()
    {
        using ScriptedInfluxServer server = ScriptedInfluxServer.Start((400, ScriptedInfluxServer.FieldTypeConflictBody));
        await using InfluxWriter writer = new(NullLogger<InfluxWriter>.Instance);
        List<InfluxConnectionState> seen = new();
        writer.StateChanged += state => seen.Add(state);

        InfluxOptions options = new()
        {
            Enabled = true,
            Url = server.Url,
            Org = "demo-org",
            Bucket = "demo-bucket",
            Token = "demo-token",
            TimeoutMs = 2000,
            VerifySsl = false
        };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.ConnectAsync(options, CancellationToken.None));

        // The operator must read the HTTP status, the server's own message and what to do about it.
        Assert.Contains("HTTP 400", error.Message);
        Assert.Contains("field type conflict", error.Message);
        Assert.Contains("locks a field name to one type per measurement", error.Message);
        Assert.Equal(InfluxConnectionState.Faulted, writer.State);
        Assert.DoesNotContain(InfluxConnectionState.Connected, seen);
    }

    [Fact]
    public async Task Connect_ProbeRejectedWithMissingBucket_FaultsWithTheBucketHint()
    {
        using ScriptedInfluxServer server = ScriptedInfluxServer.Start((
            404,
            """
            {"code":"not found","message":"bucket \"demo-bucket\" not found"}
            """));
        await using InfluxWriter writer = new(NullLogger<InfluxWriter>.Instance);
        InfluxOptions options = new()
        {
            Enabled = true,
            Url = server.Url,
            Org = "demo-org",
            Bucket = "demo-bucket",
            Token = "demo-token",
            TimeoutMs = 2000,
            VerifySsl = false
        };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.ConnectAsync(options, CancellationToken.None));

        Assert.Contains("HTTP 404", error.Message);
        Assert.Contains("check the Bucket and Org names", error.Message);
    }

    [Fact]
    public void InfluxErrorDescriber_TransportFailure_KeepsTheMessage()
    {
        string text = InfluxErrorDescriber.Describe(new HttpException("connection refused", 0));
        Assert.Equal("No response from the server: connection refused", text);
    }

    [Fact]
    public void InfluxErrorDescriber_Unauthorized_AddsTheTokenHint()
    {
        string text = InfluxErrorDescriber.Describe(new HttpException("unauthorized access", 401));
        Assert.StartsWith("HTTP 401:", text);
        Assert.Contains("check the Token", text);
    }

    [Fact]
    public void InfluxErrorDescriber_PassesThroughNonInfluxErrors()
    {
        string text = InfluxErrorDescriber.Describe(new InvalidOperationException("Influx Url, Org, Bucket, and Token are required."));
        Assert.Equal("Influx Url, Org, Bucket, and Token are required.", text);
    }

    [Fact]
    public void Retry_Backoff_GrowsToTheCapAndStopsThere()
    {
        Assert.Equal(2000, BridgeWorker.InfluxRetryDelayMs(0));
        Assert.Equal(4000, BridgeWorker.InfluxRetryDelayMs(1));
        Assert.Equal(8000, BridgeWorker.InfluxRetryDelayMs(2));
        Assert.Equal(16000, BridgeWorker.InfluxRetryDelayMs(3));
        Assert.Equal(30000, BridgeWorker.InfluxRetryDelayMs(4));
        Assert.Equal(30000, BridgeWorker.InfluxRetryDelayMs(12));
    }

    [Fact]
    public void SetLastError_RecordsFailureWithoutChangingState()
    {
        InfluxRuntimeSettings settings = new(Options.Create(new InfluxOptions()));
        settings.SetState("Connected");

        settings.SetLastError("Write failed: unauthorized");

        InfluxRuntimeSnapshot snapshot = settings.GetSnapshot();
        Assert.Equal("Connected", snapshot.State);
        Assert.Equal("Write failed: unauthorized", snapshot.LastError);
    }

    [Fact]
    public void InfluxRuntimeSettings_Persists_ToDisk()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "influx.json");
        if (File.Exists(path)) File.Delete(path);

        InfluxRuntimeSettings settings = new(Options.Create(new InfluxOptions()));
        settings.UpsertOptions(new InfluxOptions
        {
            Enabled = true,
            Url = "http://127.0.0.1:8086",
            Org = "factory",
            Bucket = "tags",
            Token = "secret",
            Measurement = "opc_tags"
        });

        Assert.True(File.Exists(path));
        InfluxRuntimeSettings reloaded = new(Options.Create(new InfluxOptions()));
        InfluxOptions opts = reloaded.GetOptions();
        Assert.True(opts.Enabled);
        Assert.Equal("factory", opts.Org);
        Assert.Equal("tags", opts.Bucket);
        Assert.Equal("secret", opts.Token);
    }
}
