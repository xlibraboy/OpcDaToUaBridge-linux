using System.Text.RegularExpressions;
using OpcBridge.Logic;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The guide that moved here with the Logic feature: the section list the Guide dialog
/// renders and the wording operators rely on. The dashboard's HelpContentTests asserted the
/// same content while the feature lived in the bridge dashboard.
/// </summary>
public sealed class LogicAppHelpTests
{
    private static string[] Sections() =>
        Regex.Split(HelpContent.Markdown, @"\r?\n---\r?\n").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

    private static string SectionTitle(string section)
    {
        var match = Regex.Match(section, @"^#\s+(.+)", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    [Fact]
    public void Guide_DescribesPlantLogicAndItsLiveState()
    {
        Assert.Contains("# Logic (Interlocks, Permissives & Sequences)", HelpContent.Markdown);
        Assert.Contains("interlock / permissive", HelpContent.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", HelpContent.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/logic/state", HelpContent.Markdown);
        Assert.Contains("next-step", HelpContent.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("# Tags", HelpContent.Markdown);
        Assert.Contains("filters the list", HelpContent.Markdown);
    }

    [Fact]
    public void Guide_SectionsMapOntoTheDialogTopics()
    {
        string[] titles = Sections().Select(SectionTitle).ToArray();

        Assert.Contains("Logic (Interlocks, Permissives & Sequences)", titles);
        foreach (string expected in new[]
        {
            "Block kinds", "Conditions (the simple form)", "Network (IEC 61131-3)",
            "Interlock group", "Tags", "Actions", "Live state",
        })
        {
            Assert.Contains(expected, titles);
        }
    }

    [Fact]
    public void Guide_CoversTheOfflineBehaviourAndTheIecVocabulary()
    {
        string markdown = HelpContent.Markdown;
        foreach (string term in new[] { "TON", "TOF", "TP", "CTU", "CTD", "SR", "RS", "R_TRIG", "F_TRIG", "XOR" })
        {
            Assert.Contains(term, markdown);
        }

        Assert.Contains("unreachable", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disables editing", markdown, StringComparison.OrdinalIgnoreCase);
    }
}
