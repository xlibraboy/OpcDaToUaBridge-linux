using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The address-space enumeration loop: S_FALSE is a legal "this batch is the last" reply that
/// still carries items, so everything a call fetched must be consumed before the loop stops.
/// </summary>
public sealed class OpcTagBrowserEnumerationTests
{
    private const int SOk = 0;
    private const int SFalse = 1;
    private const int EFail = unchecked((int)0x80004005);

    private static Func<string[], int[], int> Sequence(params (int Hr, string[] Items)[] replies)
    {
        int index = 0;
        return (buffer, fetched) =>
        {
            if (index >= replies.Length)
            {
                throw new InvalidOperationException("The enumerator was called more times than the test sequence provides.");
            }

            (int hr, string[] items) = replies[index];
            index++;
            Array.Copy(items, buffer, items.Length);
            fetched[0] = items.Length;
            return hr;
        };
    }

    [Fact]
    public void ItemsReturnedWithSFalse_AreCollected()
    {
        List<string> output = new();
        List<string> warnings = new();

        OpcTagBrowser.DrainStrings(
            4,
            Sequence((SFalse, new[] { "Tag1" }), (SFalse, Array.Empty<string>())),
            output,
            warnings,
            "test");

        Assert.Equal(new[] { "Tag1" }, output);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ItemsSpanningBatches_AreFullyDrained()
    {
        List<string> output = new();
        List<string> warnings = new();

        OpcTagBrowser.DrainStrings(
            2,
            Sequence((SOk, new[] { "A", "B" }), (SFalse, new[] { "C" }), (SFalse, Array.Empty<string>())),
            output,
            warnings,
            "test");

        Assert.Equal(new[] { "A", "B", "C" }, output);
        Assert.Empty(warnings);
    }

    [Fact]
    public void HardFailure_KeepsFetchedItemsAndRecordsWarning()
    {
        List<string> output = new();
        List<string> warnings = new();

        OpcTagBrowser.DrainStrings(
            4,
            Sequence((SOk, new[] { "A" }), (EFail, new[] { "B" })),
            output,
            warnings,
            "test");

        Assert.Equal(new[] { "A", "B" }, output);
        Assert.Single(warnings);
        Assert.Contains("0x80004005", warnings[0]);
    }

    [Fact]
    public void EndOfSequence_StopsWithoutWarning()
    {
        List<string> output = new();
        List<string> warnings = new();

        OpcTagBrowser.DrainStrings(
            4,
            Sequence((SFalse, Array.Empty<string>())),
            output,
            warnings,
            "test");

        Assert.Empty(output);
        Assert.Empty(warnings);
    }

    [Fact]
    public void BlankNames_AreSkipped()
    {
        List<string> output = new();
        List<string> warnings = new();

        OpcTagBrowser.DrainStrings(
            4,
            Sequence((SOk, new[] { "", "B" }), (SFalse, Array.Empty<string>())),
            output,
            warnings,
            "test");

        Assert.Equal(new[] { "B" }, output);
    }
}
