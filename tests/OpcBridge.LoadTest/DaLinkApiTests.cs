using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using OpcBridge.App;
using OpcBridge.Core;
using Xunit;
namespace OpcBridge.LoadTest;
[Collection(nameof(DaLinkApiAppCollection))]
public sealed class DaLinkApiTests
{
    [Fact]
    public void TryMigrateLegacyDaLinks_LogsWarningAndLeavesStoreUsableOnConflict()
    {
        DeleteIfExists(Path.Combine(AppContext.BaseDirectory, "links.json"));

        DaLinkStore store = new(ToOptions(new BridgeOptions()));
        DashboardLogStore logStore = new();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });

        TagMapping[] legacyMappings =
        {
            new()
            {
                SourceId = "consumerA",
                ItemId = "itemA",
                ProviderSourceId = "providerA",
                ProviderItemId = "itemP1"
            },
            new()
            {
                SourceId = "consumerA",
                ItemId = "itemA",
                ProviderSourceId = "providerB",
                ProviderItemId = "itemP2"
            }
        };

        bool migrated = DaLinkApiHelpers.TryMigrateLegacyDaLinks(
            store,
            legacyMappings,
            logStore,
            loggerFactory.CreateLogger("DaLinkApiTests"),
            out string? warning);

        Assert.False(migrated);
        Assert.Equal("Skipping legacy DA link migration from mappings.json because Consumer already has a provider.", warning);

        IReadOnlyList<DashboardLogEntry> entries = logStore.GetEntries(10, LogLevel.Warning);
        DashboardLogEntry entry = Assert.Single(entries);
        Assert.Contains("Consumer already has a provider.", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Consumer already has a provider.", entry.ExceptionText, StringComparison.Ordinal);

        (IReadOnlyList<DaLinkRule> rules, long version) = store.GetSnapshot();
        Assert.Empty(rules);
        Assert.Equal(0, version);
    }

    [Fact]
    public async Task PutMissingRule_ReturnsNotFoundBeforeMetadataValidation()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(static appDirectory =>
        {
            File.WriteAllText(Path.Combine(appDirectory, "mappings.json"), "[]");
            DeleteIfExists(Path.Combine(appDirectory, "links.json"));
        });

        Guid id = Guid.NewGuid();
        using HttpResponseMessage response = await app.Client.PutAsync(
            $"/api/da-links/{id}",
            CreateJsonContent(new UpdateDaLinkRequest(new DaLinkDto(
                Id: id,
                ProviderSourceId: "providerA",
                ProviderItemId: "itemP",
                ConsumerSourceId: "consumerA",
                ConsumerItemId: "itemC",
                Enabled: true,
                ProviderCanonicalType: 5,
                ConsumerCanonicalType: 5))));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Rule not found.", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostForgedMetadata_ReturnsProviderNotFound()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(static appDirectory =>
        {
            File.WriteAllText(Path.Combine(appDirectory, "mappings.json"), "[]");
            DeleteIfExists(Path.Combine(appDirectory, "links.json"));
        });

        using HttpResponseMessage response = await app.Client.PostAsync(
            "/api/da-links",
            CreateJsonContent(new CreateDaLinkRequest(new DaLinkDto(
                Id: Guid.NewGuid(),
                ProviderSourceId: "providerA",
                ProviderItemId: "itemP",
                ConsumerSourceId: "consumerA",
                ConsumerItemId: "itemC",
                Enabled: true,
                ProviderCanonicalType: 5,
                ConsumerCanonicalType: 5,
                ProviderAccessRights: 1,
                ConsumerAccessRights: 3))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Provider tag not found.", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RemoveSource_RemovesDaLinkRulesReferencingThatSource()
    {
        Guid providerRuleId = Guid.NewGuid();
        Guid consumerRuleId = Guid.NewGuid();
        Guid retainedRuleId = Guid.NewGuid();

        await using TestAppHandle app = await TestAppHandle.StartAsync(appDirectory =>
        {
            File.WriteAllText(Path.Combine(appDirectory, "mappings.json"), JsonSerializer.Serialize(new[]
            {
                new TagMapping
                {
                    SourceId = "providerA",
                    ItemId = "itemP",
                    AccessRights = TagAccessRights.Read,
                    Enabled = true
                },
                new TagMapping
                {
                    SourceId = "consumerA",
                    ItemId = "itemC",
                    AccessRights = TagAccessRights.Write,
                    Enabled = true
                },
                new TagMapping
                {
                    SourceId = "otherA",
                    ItemId = "itemO",
                    AccessRights = TagAccessRights.ReadWrite,
                    Enabled = true
                }
            }));

            File.WriteAllText(
                Path.Combine(appDirectory, "sources.json"),
                JsonSerializer.Serialize(new DaRuntimeSettingsSnapshot(
                    1000,
                    true,
                    new[]
                    {
                        DaRuntimeSettings.CreateDaSource("providerA", "Provider", string.Empty, "localhost", null, null, null, 1000),
                        DaRuntimeSettings.CreateDaSource("consumerA", "Consumer", string.Empty, "localhost", null, null, null, 1000),
                        DaRuntimeSettings.CreateDaSource("otherA", "Other", string.Empty, "localhost", null, null, null, 1000)
                    },
                    0)));

            File.WriteAllText(Path.Combine(appDirectory, "links.json"), JsonSerializer.Serialize(new[]
            {
                new DaLinkRule(providerRuleId, "providerA", "itemP", "consumerA", "itemC", true, 5, 5),
                new DaLinkRule(consumerRuleId, "otherA", "itemO", "providerA", "itemP", true, 5, 5),
                new DaLinkRule(retainedRuleId, "otherA", "itemO", "consumerA", "itemC", true, 5, 5)
            }));
        });

        using HttpResponseMessage response = await app.Client.PostAsync(
            "/api/da/sources/remove",
            CreateJsonContent(new DaSourceRemoveRequest("providerA")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument linksBody = await app.GetJsonAsync("/api/da-links");
        JsonElement.ArrayEnumerator links = linksBody.RootElement.GetProperty("links").EnumerateArray();
        List<Guid> remainingIds = new();
        foreach (JsonElement link in links)
        {
            remainingIds.Add(link.GetProperty("id").GetGuid());
        }

        Assert.DoesNotContain(providerRuleId, remainingIds);
        Assert.DoesNotContain(consumerRuleId, remainingIds);
        Assert.Contains(retainedRuleId, remainingIds);
    }

    [Fact]
    public async Task RemoveLastSource_ReturnsOk_AndSourcesBecomeEmpty()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(appDirectory =>
        {
            File.WriteAllText(
                Path.Combine(appDirectory, "sources.json"),
                JsonSerializer.Serialize(new DaRuntimeSettingsSnapshot(
                    1000,
                    true,
                    new[]
                    {
                        DaRuntimeSettings.CreateDaSource("onlyA", "Only", string.Empty, "localhost", null, null, null, 1000)
                    },
                    0)));
        });

        using HttpResponseMessage response = await app.Client.PostAsync(
            "/api/da/sources/remove",
            CreateJsonContent(new DaSourceRemoveRequest("onlyA")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument sourcesBody = await app.GetJsonAsync("/api/da/sources");
        Assert.Empty(sourcesBody.RootElement.GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public async Task EmptySourcesConfig_DoesNotReseedDefaultSource()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(appDirectory =>
        {
            File.WriteAllText(
                Path.Combine(appDirectory, "sources.json"),
                JsonSerializer.Serialize(new DaRuntimeSettingsSnapshot(
                    1000,
                    true,
                    Array.Empty<DaSourceRuntimeSettings>(),
                    0)));
        });

        using JsonDocument sourcesBody = await app.GetJsonAsync("/api/da/sources");
        Assert.Empty(sourcesBody.RootElement.GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public void ValidateLink_RejectsTypeMismatch()
    {
        DaLinkDto request = new(
            Id: Guid.NewGuid(),
            ProviderSourceId: "providerA",
            ProviderItemId: "itemP",
            ConsumerSourceId: "consumerA",
            ConsumerItemId: "itemC",
            Enabled: true,
            ProviderCanonicalType: 5,
            ConsumerCanonicalType: 3);

        string? error = DaLinkValidators.Validate(request, consumerHasProvider: false, providerReadable: true, consumerWritable: true);
        Assert.Equal("Provider and consumer must use the same native OPC DA type.", error);
    }


    private static Microsoft.Extensions.Options.IOptions<BridgeOptions> ToOptions(BridgeOptions options)
    {
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    private static StringContent CreateJsonContent<T>(T value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

}

[CollectionDefinition(nameof(DaLinkApiAppCollection), DisableParallelization = true)]
public sealed class DaLinkApiAppCollection;
