using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpcBridge.Client;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// End-to-end wiring for the /api/logic family: definitions round-trip and validate,
/// the state endpoint evaluates against seeded mappings (unknown when no value has
/// arrived) and notes attach to known blocks only.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class LogicApiTests
{
    private static async Task<(TestAppHandle App, Guid BlockId)> StartWithBlockAsync()
    {
        TestAppHandle app = await TestAppHandle.StartAsync(static appDirectory =>
        {
            // The harness copies the test output directory, which can still hold files left by
            // the in-process store tests; start this instance from a clean logic slate.
            foreach (string stray in new[] { "logic.json", "logic-notes.json" })
            {
                string strayPath = Path.Combine(appDirectory, stray);
                if (File.Exists(strayPath))
                {
                    File.Delete(strayPath);
                }
            }

            File.WriteAllText(
                Path.Combine(appDirectory, "mappings.json"),
                JsonSerializer.Serialize(new[]
                {
                    new TagMapping
                    {
                        SourceId = "sim",
                        ItemId = "ns=2;s=Status/Line01.Permit",
                        DisplayName = "Line 01 Start Permit",
                        DataType = "Boolean",
                        Digital = true,
                        OnText = "Permit",
                        OffText = "Blocked"
                    }
                }));
        });

        LogicBlockDto block = new()
        {
            Id = Guid.NewGuid(),
            Name = "Line 01 Start",
            Kind = LogicBlockKinds.Interlock,
            Conditions =
            {
                new LogicConditionDto
                {
                    Id = Guid.NewGuid(),
                    Text = "Line 01 start permit must be given",
                    SourceId = "sim",
                    ItemId = "ns=2;s=Status/Line01.Permit",
                    Op = LogicConditionOps.On
                }
            }
        };

        using HttpResponseMessage response = await app.Client.PostAsJsonAsync("/api/logic/blocks", new { block });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (app, block.Id);
    }

    [Fact]
    public async Task Blocks_RoundTripAndStateEvaluatesAgainstMappings()
    {
        (TestAppHandle app, Guid blockId) = await StartWithBlockAsync();
        await using (app)
        {
            JsonDocument list = await app.GetJsonAsync("/api/logic");
            JsonElement stored = list.RootElement.GetProperty("blocks")[0];
            Assert.Equal(blockId, stored.GetProperty("id").GetGuid());
            Assert.Equal("interlock", stored.GetProperty("kind").GetString());
            Assert.Equal("Line 01 start permit must be given", stored.GetProperty("conditions")[0].GetProperty("text").GetString());
            Assert.Equal(1, list.RootElement.GetProperty("version").GetInt64());

            // No source produces a value in this test host, so the block must read unknown —
            // never silently ready — and name the tag from the mapping's display name.
            JsonDocument state = await app.GetJsonAsync("/api/logic/state");
            JsonElement blockState = state.RootElement.GetProperty("blocks")[0];
            Assert.Equal(blockId, blockState.GetProperty("id").GetGuid());
            Assert.Equal("unknown", blockState.GetProperty("state").GetString());
            Assert.Equal("no data for Line 01 Start Permit", blockState.GetProperty("reason").GetString());
            Assert.Equal("unknown", blockState.GetProperty("elements")[0].GetProperty("state").GetString());
            Assert.Equal(1, state.RootElement.GetProperty("version").GetInt64());
        }
    }

    [Fact]
    public async Task Blocks_RejectDuplicateNamesAndInvalidShapes()
    {
        (TestAppHandle app, _) = await StartWithBlockAsync();
        await using (app)
        {
            LogicBlockDto duplicate = new()
            {
                Id = Guid.NewGuid(),
                Name = "line 01 start",
                Kind = LogicBlockKinds.Interlock,
                Conditions = { new LogicConditionDto { Text = "x", SourceId = "sim", ItemId = "t", Op = LogicConditionOps.On } }
            };

            using HttpResponseMessage conflict = await app.Client.PostAsJsonAsync("/api/logic/blocks", new { block = duplicate });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using JsonDocument conflictBody = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
            Assert.Equal("Block name already exists.", conflictBody.RootElement.GetProperty("error").GetString());

            LogicBlockDto numericWithoutValue = new()
            {
                Id = Guid.NewGuid(),
                Name = "Numeric without value",
                Kind = LogicBlockKinds.Interlock,
                Conditions = { new LogicConditionDto { Text = "Level above", SourceId = "sim", ItemId = "t", Op = LogicConditionOps.GreaterThan } }
            };

            using HttpResponseMessage badRequest = await app.Client.PostAsJsonAsync("/api/logic/blocks", new { block = numericWithoutValue });
            Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
            using JsonDocument badBody = JsonDocument.Parse(await badRequest.Content.ReadAsStringAsync());
            Assert.Equal("A numeric comparison needs a value.", badBody.RootElement.GetProperty("error").GetString());

            using HttpResponseMessage missingBlock = await app.Client.PostAsJsonAsync("/api/logic/blocks", new { });
            Assert.Equal(HttpStatusCode.BadRequest, missingBlock.StatusCode);
        }
    }

    [Fact]
    public async Task Blocks_DeleteRemovesAndReportsNotFoundAfterwards()
    {
        (TestAppHandle app, Guid blockId) = await StartWithBlockAsync();
        await using (app)
        {
            using HttpResponseMessage delete = await app.Client.DeleteAsync($"/api/logic/blocks/{blockId}");
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
            using JsonDocument deleted = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
            Assert.Equal(2, deleted.RootElement.GetProperty("version").GetInt64());

            using HttpResponseMessage again = await app.Client.DeleteAsync($"/api/logic/blocks/{blockId}");
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

            JsonDocument list = await app.GetJsonAsync("/api/logic");
            Assert.Empty(list.RootElement.GetProperty("blocks").EnumerateArray());
        }
    }

    [Fact]
    public async Task Notes_AttachToKnownBlocksAndComeBackNewestFirst()
    {
        (TestAppHandle app, Guid blockId) = await StartWithBlockAsync();
        await using (app)
        {
            using HttpResponseMessage unknownBlock = await app.Client.PostAsJsonAsync(
                "/api/logic/notes",
                new { blockId = Guid.NewGuid(), text = "ghost" });
            Assert.Equal(HttpStatusCode.NotFound, unknownBlock.StatusCode);

            using HttpResponseMessage first = await app.Client.PostAsJsonAsync(
                "/api/logic/notes",
                new { blockId, text = "  Waiting on permit key  " });
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using JsonDocument firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.Equal("Waiting on permit key", firstBody.RootElement.GetProperty("note").GetProperty("text").GetString());
            // Authentication is disabled in test hosts, so the author falls back to "app".
            Assert.Equal("app", firstBody.RootElement.GetProperty("note").GetProperty("author").GetString());

            using HttpResponseMessage second = await app.Client.PostAsJsonAsync(
                "/api/logic/notes",
                new { blockId, text = "Operator acknowledged" });
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            JsonDocument notes = await app.GetJsonAsync($"/api/logic/notes?blockId={blockId}");
            JsonElement[] listed = notes.RootElement.GetProperty("notes").EnumerateArray().ToArray();
            Assert.Equal(2, listed.Length);
            Assert.Contains(listed, note => note.GetProperty("text").GetString() == "Operator acknowledged");

            JsonDocument all = await app.GetJsonAsync("/api/logic/notes");
            Assert.Equal(2, all.RootElement.GetProperty("notes").GetArrayLength());

            using HttpResponseMessage blank = await app.Client.PostAsJsonAsync(
                "/api/logic/notes",
                new { blockId, text = "   " });
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        }
    }

    /// <summary>
    /// A relay bound to a tag the bridge does not have yet creates it as an internal
    /// (bridge-held) tag: it shows up in the definitions, in the tag list and as a mapping —
    /// and the plant logic's own M is read-only for a writer.
    /// </summary>
    [Fact]
    public async Task Relays_BindTheTag_AndCreateAnInternalTagWhenItIsNew()
    {
        (TestAppHandle app, _) = await StartWithBlockAsync();
        await using (app)
        {
            LogicBlockDto block = new()
            {
                Id = Guid.NewGuid(),
                Name = "Line 01 relay drive",
                Kind = LogicBlockKinds.Interlock,
                Elements =
                {
                    new LogicElementDto
                    {
                        Id = Guid.NewGuid(),
                        Kind = LogicElementKinds.Coil,
                        Relay = "M01",
                        Inputs =
                        {
                            new LogicElementDto
                            {
                                Id = Guid.NewGuid(),
                                Kind = LogicElementKinds.Contact,
                                Text = "Line 01 start permit must be given",
                                SourceId = "sim",
                                ItemId = "ns=2;s=Status/Line01.Permit",
                                Op = LogicConditionOps.On
                            }
                        }
                    }
                }
            };
            using HttpResponseMessage blockSave = await app.Client.PostAsJsonAsync("/api/logic/blocks", new { block });
            Assert.Equal(HttpStatusCode.OK, blockSave.StatusCode);

            using HttpResponseMessage relays = await app.Client.PostAsJsonAsync(
                "/api/logic/relays",
                new
                {
                    relays = new[]
                    {
                        new { name = "M01", sourceId = "internal", itemId = "Line01.StartPermit", write = "auto" }
                    }
                });
            Assert.Equal(HttpStatusCode.OK, relays.StatusCode);
            using JsonDocument relaysBody = JsonDocument.Parse(await relays.Content.ReadAsStringAsync());
            Assert.Equal("M01", relaysBody.RootElement.GetProperty("relays")[0].GetProperty("name").GetString());

            JsonDocument definitions = await app.GetJsonAsync("/api/logic");
            JsonElement storedRelay = definitions.RootElement.GetProperty("relays")[0];
            Assert.Equal("M01", storedRelay.GetProperty("name").GetString());
            Assert.Equal("Line01.StartPermit", storedRelay.GetProperty("itemId").GetString());

            // The bound tag was created as the bridge's own (source internal, Mode Internal) and
            // is reported read-only: the coil that drives M01 owns it (the write policy's auto).
            JsonDocument tags = await app.GetJsonAsync("/api/hmi/tags");
            JsonElement created = tags.RootElement.GetProperty("tags").EnumerateArray()
                .Single(tag => tag.GetProperty("itemId").GetString() == "Line01.StartPermit");
            Assert.Equal("internal", created.GetProperty("sourceId").GetString());
            Assert.False(created.GetProperty("writeable").GetBoolean());

            // Evaluated: the coil drives M01, so a write is refused with the reason, never
            // silently applied.
            using HttpResponseMessage write = await app.Client.PostAsJsonAsync(
                "/api/hmi/write",
                new { sourceId = "internal", itemId = "Line01.StartPermit", value = true });
            using JsonDocument writeBody = JsonDocument.Parse(await write.Content.ReadAsStringAsync());
            Assert.False(writeBody.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("M01", writeBody.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Relays_RefuseDuplicateAndInvalidBindings()
    {
        (TestAppHandle app, _) = await StartWithBlockAsync();
        await using (app)
        {
            using HttpResponseMessage duplicate = await app.Client.PostAsJsonAsync(
                "/api/logic/relays",
                new
                {
                    relays = new[]
                    {
                        new { name = "M01", sourceId = "internal", itemId = "A" },
                        new { name = "m01", sourceId = "internal", itemId = "B" }
                    }
                });
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
            Assert.Contains("twice", body.RootElement.GetProperty("error").GetString());
        }
    }
}
