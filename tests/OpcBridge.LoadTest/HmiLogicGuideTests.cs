using OpcBridge.Client;
using OpcBridge.Hmi.ViewModels;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Logic page's guide ships inside the runtime's assembly (its Guide… button) — the
/// vocabulary the editor offers must be documented there, so the two cannot drift apart.
/// </summary>
public sealed class HmiLogicGuideTests
{
    private static string Guide() =>
        ReleaseNotes.Load(typeof(MainViewModel).Assembly, "OpcBridge.Hmi.LogicGuide.md");

    [Fact]
    public void Guide_DescribesEveryTopicAndTheIecVocabulary()
    {
        string guide = Guide();

        Assert.Contains("# Logic (Interlocks, Permissives & Sequences)", guide);
        Assert.Contains("# Block kinds", guide);
        Assert.Contains("# Conditions (the simple form)", guide);
        Assert.Contains("# Network (IEC 61131-3)", guide);
        Assert.Contains("# Area and interlock group", guide);
        Assert.Contains("# Tags", guide);
        Assert.Contains("# Area tags (per area)", guide);
        Assert.Contains("# Actions", guide);
        Assert.Contains("# Live state", guide);
        Assert.Contains("# Result tab", guide);

        Assert.Contains("OR group", guide);
        Assert.Contains("warn only", guide);
        Assert.Contains("should 1 / actual 0", guide);
        Assert.Contains("AREA TAG", guide);
        Assert.Contains("not assigned", guide);
        Assert.Contains("Tags join the area as they are picked", guide);
        Assert.Contains("TON", guide);
        Assert.Contains("TOF", guide);
        Assert.Contains("TP", guide);
        Assert.Contains("CTU", guide);
        Assert.Contains("CTD", guide);
        Assert.Contains("SR", guide);
        Assert.Contains("RS", guide);
        Assert.Contains("R_TRIG", guide);
        Assert.Contains("F_TRIG", guide);
        Assert.Contains("XOR", guide);

        Assert.Contains("/api/logic/state", guide);
        Assert.Contains("logic", guide);
        Assert.Contains("View ▸ Logic", guide);
    }
}
