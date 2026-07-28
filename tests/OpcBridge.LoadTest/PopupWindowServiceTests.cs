using OpcBridge.Hmi.Core;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class PopupWindowServiceTests
{
    [Fact]
    public void TagBindingKey_UsedAsPopupIdentity()
    {
        var a = TagBindingKey.Create("line1", "default", "Tank.Level");
        var b = TagBindingKey.Create("LINE1", "DEFAULT", "tank.level");
        Assert.True(TagBindingKeyComparer.Instance.Equals(a, b));
        Assert.Equal(
            TagBindingKeyComparer.Instance.GetHashCode(a),
            TagBindingKeyComparer.Instance.GetHashCode(b));
    }
}
