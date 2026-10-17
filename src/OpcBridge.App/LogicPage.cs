using System.Text;

namespace OpcBridge.App;

/// <summary>
/// The Logic page: plant-logic authoring as a plain web page, served at <c>/logic</c>. It edits
/// the same store the HMI's Logic page and the phone read, over the same endpoints
/// (<c>/api/logic</c> and the <c>/hmi</c> hub's <c>logic</c> message), which is why it is served
/// by the bridge rather than from the HMI: the page then shares the API's origin, so nothing
/// needs CORS configuration and the page works in any browser as well as in a WebView.
///
/// The assets are embedded at build time (see OpcBridge.App.csproj) and read once here, the way
/// the release notes are: one copy means the repository files, the served page and every
/// deployment (the service, the MSI, a self-contained publish, the test host) cannot drift
/// apart. Nothing is loaded from the network — a plant host has no internet.
/// </summary>
internal static class LogicPage
{
    public const string HtmlType = "text/html; charset=utf-8";
    public const string StyleSheetType = "text/css; charset=utf-8";
    public const string ScriptType = "text/javascript; charset=utf-8";

    private const string HtmlResource = "OpcBridge.App.logic.index.html";
    private const string StyleSheetResource = "OpcBridge.App.logic.app.css";
    private const string ScriptResource = "OpcBridge.App.logic.app.js";

    private static readonly Lazy<byte[]> Html_ = new(() => Bytes(Read(HtmlResource)));
    private static readonly Lazy<byte[]> StyleSheet_ = new(() => Bytes(Read(StyleSheetResource)));
    private static readonly Lazy<byte[]> Script_ = new(() => Bytes(Read(ScriptResource)));

    public static byte[] Html => Html_.Value;

    public static byte[] StyleSheet => StyleSheet_.Value;

    public static byte[] Script => Script_.Value;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Read(string resourceName)
    {
        using Stream? stream = typeof(LogicPage).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"The Logic page resource '{resourceName}' is missing from this build — check the "
                + "EmbeddedResource items for Assets/logic in OpcBridge.App.csproj.");
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
