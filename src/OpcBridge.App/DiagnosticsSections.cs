namespace OpcBridge.App;

/// <summary>
/// Guards individual /api/diagnostics sections so a failure building one section
/// (e.g. the UA stack being mid-initialization during early requests) degrades that
/// section to <c>null</c> instead of failing the entire endpoint with a 500.
/// </summary>
public static class DiagnosticsSections
{
    /// <summary>Runs <paramref name="build"/>, returning its value, or null when it throws.</summary>
    public static object? Safe(string name, Func<object?> build, Action<Exception> onError)
    {
        try
        {
            return build();
        }
        catch (Exception exception)
        {
            onError(exception);
            return null;
        }
    }
}
