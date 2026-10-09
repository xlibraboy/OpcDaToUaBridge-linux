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
        Assert.Contains("\"group\":\"Start permissive\"", json, StringComparison.Ordinal);
        Assert.Contains("\"holdMs\":3000", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Kind\"", json, StringComparison.Ordinal);

        LogicBlockDto? copy = JsonSerializer.Deserialize<LogicBlockDto>(json, CamelCase);

        Assert.NotNull(copy);
        Assert.Equal(block.Id, copy!.Id);
        Assert.Equal(LogicBlockKinds.Sequence, copy.Kind);
        Assert.Equal("Line 01 Start", copy.Name);
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

        LogicStateSnapshot? copy = JsonSerializer.Deserialize<LogicStateSnapshot>(json, CamelCase);

        Assert.NotNull(copy);
        Assert.Equal(7, copy!.Version);
        Assert.Equal(LogicBlockStates.Blocked, copy.Blocks[0].State);
        Assert.Equal(LogicStepStates.Current, copy.Blocks[0].Steps[0].State);
        Assert.False(copy.Blocks[0].Elements[0].Matches);
        Assert.Equal(LogicElementKinds.Ton, copy.Blocks[0].Elements[1].Kind);
        Assert.Equal(1500, copy.Blocks[0].Elements[1].ElapsedMs);
        Assert.Equal(2, copy.Blocks[0].Elements[2].Count);
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
