using OpcBridge.Ua;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class OpcUaMonitoredItemReconcileTests
{
    [Fact]
    public void Diff_AddsAndRemoves()
    {
        var (add, remove) = MonitoredItemReconcile.Diff(
            desiredNodeIds: new[] { "ns=2;s=A", "ns=2;s=B" },
            activeNodeIds: new[] { "ns=2;s=A", "ns=2;s=C" });

        Assert.Equal(new[] { "ns=2;s=B" }, add);
        Assert.Equal(new[] { "ns=2;s=C" }, remove);
    }

    [Fact]
    public void Diff_Identical_IsEmpty()
    {
        string[] ids = ["ns=2;s=A", "ns=2;s=B"];
        var (add, remove) = MonitoredItemReconcile.Diff(ids, ids);
        Assert.Empty(add);
        Assert.Empty(remove);
    }

    [Fact]
    public void Diff_EmptyDesired_RemovesAll()
    {
        var (add, remove) = MonitoredItemReconcile.Diff(
            desiredNodeIds: Array.Empty<string>(),
            activeNodeIds: new[] { "ns=2;s=A", "ns=2;s=B" });

        Assert.Empty(add);
        Assert.Equal(new[] { "ns=2;s=A", "ns=2;s=B" }, remove);
    }

    [Fact]
    public void Diff_EmptyActive_AddsAll()
    {
        var (add, remove) = MonitoredItemReconcile.Diff(
            desiredNodeIds: new[] { "ns=2;s=B", "ns=2;s=A" },
            activeNodeIds: Array.Empty<string>());

        Assert.Equal(new[] { "ns=2;s=A", "ns=2;s=B" }, add);
        Assert.Empty(remove);
    }

    [Fact]
    public void Diff_IgnoresWhitespaceAndDuplicates()
    {
        var (add, remove) = MonitoredItemReconcile.Diff(
            desiredNodeIds: new[] { " ns=2;s=A ", "ns=2;s=A", "ns=2;s=B", "" },
            activeNodeIds: new[] { "ns=2;s=A", "ns=2;s=A", "  " });

        Assert.Equal(new[] { "ns=2;s=B" }, add);
        Assert.Empty(remove);
    }
}
