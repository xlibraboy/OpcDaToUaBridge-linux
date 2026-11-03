using OpcBridge.Hmi.Core;
using OpcBridge.Mobile.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The viewer's access rule: an accepted bridge session opens the app, and the "bridge runs
/// without sign-in" state is still an anonymous session, not an exception. The read-only
/// offline view is the one separate grant (covered in MobileOfflineViewerTests).
/// </summary>
public sealed class MobileAccessTests
{
    [Fact]
    public void OnlyAnAuthenticatedSessionOpensTheViewer()
    {
        Assert.False(MobileAccess.IsGranted(null));

        using BridgeConnection connection = new(
            new SavedBridge("a1", "Line 1", "http://127.0.0.1:1"),
            new MultiBridgeTagCache());

        // No session read yet.
        Assert.False(MobileAccess.IsGranted(connection));

        // The bridge answered, but nobody is signed in — with or without enforced auth.
        connection.Session = new MobileSession(false, string.Empty, string.Empty, "Viewer", AuthEnabled: false);
        Assert.False(MobileAccess.IsGranted(connection));

        connection.Session = new MobileSession(true, "op", "Operator One", "Operator", AuthEnabled: false);
        Assert.True(MobileAccess.IsGranted(connection));
    }
}
