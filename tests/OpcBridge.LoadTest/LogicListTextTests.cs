using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The list's empty state must name the one thing that is true: no bridge picked, a filter
/// that matched nothing, a bridge that answered with no blocks, or a bridge that was never
/// reached — the last one must not read like an empty plant.
/// </summary>
public sealed class LogicListTextTests
{
    [Fact]
    public void NoBridge_NamesSettings()
    {
        Assert.Equal(
            "No bridge selected — add one under Settings ▸ Bridges.",
            LogicListText.EmptyList(
                hasBridge: false,
                connected: false,
                hasBlocks: false,
                signInRequired: false,
                bridgeStatus: "not connected",
                filterMissText: "unused"));
    }

    [Fact]
    public void FilterMiss_KeepsTheFilterWording()
    {
        Assert.Equal(
            "No blocks match the search or filter.",
            LogicListText.EmptyList(
                hasBridge: true,
                connected: true,
                hasBlocks: true,
                signInRequired: false,
                bridgeStatus: "connected · live",
                filterMissText: "No blocks match the search or filter."));
    }

    [Fact]
    public void ConnectedAndEmpty_PointsAtTheAuthoringSurface()
    {
        string text = LogicListText.EmptyList(
            hasBridge: true,
            connected: true,
            hasBlocks: false,
            signInRequired: false,
            bridgeStatus: "connected · live",
            filterMissText: "filter");

        Assert.StartsWith("No logic blocks on this bridge yet", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Add a bridge", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreachableAndEmpty_SaysNothingHasSynced()
    {
        Assert.Equal(
            "No data yet — no bridge answered on http://10.0.0.5:18080. Connect once to sync the blocks.",
            LogicListText.EmptyList(
                hasBridge: true,
                connected: false,
                hasBlocks: false,
                signInRequired: false,
                bridgeStatus: "no bridge answered on http://10.0.0.5:18080",
                filterMissText: "filter"));
    }

    [Fact]
    public void SignInGate_AsksForASignInInsteadOfAConnect()
    {
        string text = LogicListText.EmptyList(
            hasBridge: true,
            connected: false,
            hasBlocks: false,
            signInRequired: true,
            bridgeStatus: "the bridge requires sign-in",
            filterMissText: "filter");

        Assert.Contains("requires sign-in", text, StringComparison.Ordinal);
        Assert.Contains("Sign in", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Connect once", text, StringComparison.Ordinal);
    }
}
