using System.Text.Json;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The logic DTOs are the wire contract between the bridge, the dashboard and the mobile
/// app: they must round-trip camelCase JSON (the web/hub default) and the state/kind/op
/// constants must stay stable — the phone and the dashboard JS both switch on them.
/// </summary>
public sealed class LogicDtoSerializationTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Block_RoundTripsCamelCaseJson()
    {
        LogicBlockDto block = new()
        {
            Id = Guid.NewGuid(),
            Name = "Line 01 Start",
            Group = "Primary Arm",
            OutputTag = "PUMP_RUN",
            Description = "Start-up permissives for line 01",
            Kind = LogicBlockKinds.Sequence,
            Enabled = true,
            Order = 2,
            Tags = { "Line 1", "Safety" },
            Steps =
            {
                new LogicStepDto
                {
                    Id = Guid.NewGuid(),
                    Name = "Start permit",
                    NextStepText = "Turn the permit key",
                    CompletionSourceId = "sim",
                    CompletionItemId = "ns=2;s=Status/Line01.Permit",
                    Conditions =
                    {
                        new LogicConditionDto
                        {
                            Id = Guid.NewGuid(),
                            Text = "Line 01 start permit must be given",
                            SourceId = "sim",
                            ItemId = "ns=2;s=Status/Line01.Permit",
                            Op = LogicConditionOps.On,
                            NextStepText = "Turn the permit key to Permit",
                            Severity = LogicConditionSeverities.Block,
                            Group = "Start permissive",
                            HoldMs = 3000
                        }
                    },
                    Actions =
                    {
                        new LogicActionDto
                        {
                            Label = "Start",
                            SourceId = "sim",
                            ItemId = "ns=2;s=Cmd/Line01.Start",
                            Value = "true",
                            Confirm = true
                        }
                    }
                }
            },
            Actions = { new LogicActionDto { Label = "Reset", SourceId = "sim", ItemId = "ns=2;s=Cmd/Reset", Value = "1" } }
        };

        string json = JsonSerializer.Serialize(block, CamelCase);

        Assert.Contains("\"kind\":\"sequence\"", json, StringComparison.Ordinal);
        Assert.Contains("\"nextStepText\"", json, StringComparison.Ordinal);
        Assert.Contains("\"completionSourceId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"block\"", json, StringComparison.Ordinal);
        Assert.Contains("\"confirm\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"tags\":[\"Line 1\",\"Safety\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"group\":\"Primary Arm\"", json, StringComparison.Ordinal);
        Assert.Contains("\"outputTag\":\"PUMP_RUN\"", json, StringComparison.Ordinal);
        Assert.Contains("\"group\":\"Start permissive\"", json, StringComparison.Ordinal);
        Assert.Contains("\"holdMs\":3000", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Kind\"", json, StringComparison.Ordinal);

        LogicBlockDto? copy = JsonSerializer.Deserialize<LogicBlockDto>(json, CamelCase);

        Assert.NotNull(copy);
        Assert.Equal(block.Id, copy!.Id);
        Assert.Equal(LogicBlockKinds.Sequence, copy.Kind);
        Assert.Equal("Line 01 Start", copy.Name);
        Assert.Equal("PUMP_RUN", copy.OutputTag);
        Assert.Equal(new[] { "Line 1", "Safety" }, copy.Tags);
        Assert.Single(copy.Steps);
        Assert.Equal("Turn the permit key", copy.Steps[0].NextStepText);
        Assert.Equal("ns=2;s=Status/Line01.Permit", copy.Steps[0].Conditions[0].ItemId);
        Assert.Equal(LogicConditionOps.On, copy.Steps[0].Conditions[0].Op);
        Assert.Equal("true", copy.Steps[0].Actions[0].Value);
    }

    [Fact]
    public void StateSnapshot_RoundTripsCamelCaseJson()
    {
        LogicStateSnapshot snapshot = new()
        {
            Version = 7,
            EvaluatedUtc = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            Blocks =
            {
                new LogicBlockStateDto
                {
                    Id = Guid.NewGuid(),
                    State = LogicBlockStates.Blocked,
                    Reason = "Line 01 start permit must be given",
                    Output = new LogicBlockOutputStateDto
                    {
                        Tag = "PUMP_RUN",
                        State = LogicOutputStates.Mismatch,
                        Expected = true,
                        Actual = false,
                        ValueText = "Stopped",
                        Reason = "Output PUMP_RUN is not on."
                    },
                    Elements =
                    {
                        new LogicElementStateDto
                        {
                            Id = Guid.NewGuid(),
                            Kind = LogicElementKinds.Contact,
                            Depth = 1,
                            State = LogicConditionStates.False,
                            ValueText = "Blocked",
                            TimestampUtc = new DateTime(2026, 10, 8, 11, 59, 0, DateTimeKind.Utc),
                            Should = "1",
                            Matches = false
                        },
                        new LogicElementStateDto
                        {
                            Id = Guid.NewGuid(),
                            Kind = LogicElementKinds.Ton,
                            Depth = 0,
                            State = LogicConditionStates.False,
                            PtMs = 3000,
                            ElapsedMs = 1500
                        },
                        new LogicElementStateDto
                        {
                            Id = Guid.NewGuid(),
                            Kind = LogicElementKinds.Ctu,
                            Depth = 0,
                            State = LogicConditionStates.True,
                            Count = 2
                        }
                    },
                    Steps = { new LogicStepStateDto { Id = Guid.NewGuid(), State = LogicStepStates.Current, Reason = "waiting for Line 01 Start Permit" } }
                }
            }
        };

        string json = JsonSerializer.Serialize(snapshot, CamelCase);
        Assert.Contains("\"evaluatedUtc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"valueText\":\"Blocked\"", json, StringComparison.Ordinal);
        Assert.Contains("\"should\":\"1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"matches\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"ton\",\"depth\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"ptMs\":3000,\"elapsedMs\":1500", json, StringComparison.Ordinal);
        Assert.Contains("\"count\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"output\":{\"tag\":\"PUMP_RUN\",\"state\":\"mismatch\",\"expected\":true,\"actual\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"valueText\":\"Stopped\"", json, StringComparison.Ordinal);

        LogicStateSnapshot? copy = JsonSerializer.Deserialize<LogicStateSnapshot>(json, CamelCase);

        Assert.NotNull(copy);
        Assert.Equal(7, copy!.Version);
        Assert.Equal(LogicBlockStates.Blocked, copy.Blocks[0].State);
        Assert.Equal(LogicStepStates.Current, copy.Blocks[0].Steps[0].State);
        Assert.False(copy.Blocks[0].Elements[0].Matches);
        Assert.Equal(LogicElementKinds.Ton, copy.Blocks[0].Elements[1].Kind);
        Assert.Equal(1500, copy.Blocks[0].Elements[1].ElapsedMs);
        Assert.Equal(2, copy.Blocks[0].Elements[2].Count);
        Assert.NotNull(copy.Blocks[0].Output);
        Assert.Equal("PUMP_RUN", copy.Blocks[0].Output!.Tag);
        Assert.Equal(LogicOutputStates.Mismatch, copy.Blocks[0].Output.State);
        Assert.True(copy.Blocks[0].Output.Expected);
        Assert.False(copy.Blocks[0].Output.Actual);
        Assert.Equal("Output PUMP_RUN is not on.", copy.Blocks[0].Output.Reason);
    }

    [Fact]
    public void ConditionOps_ReportWhichNeedAValue()
    {
        Assert.True(LogicConditionOps.RequiresValue(LogicConditionOps.GreaterThan));
        Assert.True(LogicConditionOps.RequiresValue(LogicConditionOps.LessThan));
        Assert.True(LogicConditionOps.RequiresValue(LogicConditionOps.Equal));
        Assert.False(LogicConditionOps.RequiresValue(LogicConditionOps.On));
        Assert.False(LogicConditionOps.RequiresValue(LogicConditionOps.Off));
        Assert.True(LogicConditionOps.RequiresValue("GT"));
        Assert.False(LogicConditionOps.RequiresValue("bogus"));
    }

    [Fact]
    public void TagsSourceAndNamedReferences_RoundTripCamelCaseJson()
    {
        LogicAreaTagsDto source = new()
        {
            Name = "DRYEND",
            Tags =
            {
                new LogicTagDto
                {
                    Name = "START_PB",
                    SourceId = "sim",
                    ItemId = "ns=2;s=Status/Start",
                    Contact = LogicContactForms.NormallyClosed
                },
                new LogicTagDto { Name = "FAULT" }
            }
        };

        string json = JsonSerializer.Serialize(source, CamelCase);
        Assert.Contains("\"name\":\"DRYEND\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tags\":[", json, StringComparison.Ordinal);
        Assert.Contains("\"contact\":\"nc\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Name\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SourceId\"", json, StringComparison.Ordinal);

        LogicAreaTagsDto? copy = JsonSerializer.Deserialize<LogicAreaTagsDto>(json, CamelCase);
        Assert.NotNull(copy);
        Assert.Equal(new[] { "START_PB", "FAULT" }, copy!.Tags.Select(tag => tag.Name));
        Assert.Equal("ns=2;s=Status/Start", copy.Tags[0].ItemId);
        Assert.Equal(LogicContactForms.NormallyClosed, copy.Tags[0].Contact);
        Assert.Equal(string.Empty, copy.Tags[1].ItemId);
        Assert.Equal(string.Empty, copy.Tags[1].Contact);

        LogicBlockDto block = new()
        {
            Id = Guid.NewGuid(),
            Name = "Dry end start",
            Group = "DRYEND",
            Area = "DRY END",
            TagSource = "DRYEND",
            Kind = LogicBlockKinds.Sequence,
            Steps =
            {
                new LogicStepDto
                {
                    Name = "Start",
                    CompletionTag = "DONE_FB",
                    Conditions = { new LogicConditionDto { Text = "permit", Tag = "START_PB", Op = LogicConditionOps.On } }
                }
            },
            Actions = { new LogicActionDto { Label = "Start", Tag = "START_PB", Value = "true" } }
        };

        string blockJson = JsonSerializer.Serialize(block, CamelCase);
        Assert.Contains("\"tagSource\":\"DRYEND\"", blockJson, StringComparison.Ordinal);
        Assert.Contains("\"area\":\"DRY END\"", blockJson, StringComparison.Ordinal);
        Assert.Contains("\"completionTag\":\"DONE_FB\"", blockJson, StringComparison.Ordinal);
        Assert.Contains("\"tag\":\"START_PB\"", blockJson, StringComparison.Ordinal);

        LogicBlockDto? blockCopy = JsonSerializer.Deserialize<LogicBlockDto>(blockJson, CamelCase);
        Assert.NotNull(blockCopy);
        Assert.Equal("DRYEND", blockCopy!.TagSource);
        Assert.Equal("DRY END", blockCopy.Area);
        Assert.Equal("DONE_FB", blockCopy.Steps[0].CompletionTag);
        Assert.Equal("START_PB", blockCopy.Steps[0].Conditions[0].Tag);
        Assert.Equal("START_PB", blockCopy.Actions[0].Tag);
    }

    [Fact]
    public void Constants_AreStableStrings()
    {
        Assert.Equal("interlock", LogicBlockKinds.Interlock);
        Assert.Equal("permissive", LogicBlockKinds.Permissive);
        Assert.Equal("sequence", LogicBlockKinds.Sequence);
        Assert.Equal("on", LogicConditionOps.On);
        Assert.Equal("off", LogicConditionOps.Off);
        Assert.Equal("gt", LogicConditionOps.GreaterThan);
        Assert.Equal("lt", LogicConditionOps.LessThan);
        Assert.Equal("eq", LogicConditionOps.Equal);
        Assert.Equal("no", LogicContactForms.NormallyOpen);
        Assert.Equal("nc", LogicContactForms.NormallyClosed);
        Assert.True(LogicContactForms.IsValid("NO"));
        Assert.True(LogicContactForms.IsValid("nc"));
        Assert.False(LogicContactForms.IsValid(""));
        Assert.False(LogicContactForms.IsValid("bogus"));
        Assert.Equal("ready", LogicBlockStates.Ready);
        Assert.Equal("blocked", LogicBlockStates.Blocked);
        Assert.Equal("unknown", LogicBlockStates.Unknown);
        Assert.Equal("disabled", LogicBlockStates.Disabled);
        Assert.Equal("done", LogicStepStates.Done);
        Assert.Equal("current", LogicStepStates.Current);
        Assert.Equal("pending", LogicStepStates.Pending);
        Assert.True(LogicBlockKinds.IsValid("PERMISSIVE"));
        Assert.False(LogicBlockKinds.IsValid("ladder"));
    }
}
