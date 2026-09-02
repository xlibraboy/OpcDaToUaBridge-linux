namespace OpcBridge.Ua;

/// <summary>
/// Central resolver for the directory holding runtime-persisted files
/// (ua-settings.json, pki/). Defaults to the app base directory; override
/// with the OPCBRIDGE_DATA environment variable (e.g. a Docker volume mount).
/// </summary>
public static class DataDirectory
{
    private static readonly Lazy<string> Path_ = new(() =>
    {
        string? env = Environment.GetEnvironmentVariable("OPCBRIDGE_DATA");
        return string.IsNullOrWhiteSpace(env) ? AppContext.BaseDirectory : Path.TrimEndingDirectorySeparator(env);
    });

    public static string Value => Path_.Value;

    public static string Combine(string fileName) => Path.Combine(Value, fileName);
}
