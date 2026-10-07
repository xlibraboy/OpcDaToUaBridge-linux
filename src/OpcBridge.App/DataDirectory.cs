namespace OpcBridge.App;

/// <summary>
/// Central resolver for the directory holding runtime-persisted files
/// (sources.json, mappings.json, links.json, mqtt.json, influx.json, pki/).
/// Defaults to the app base directory; override with the OPCBRIDGE_DATA
/// environment variable (e.g. a Docker volume mount).
/// </summary>
public static class DataDirectory
{
    private static readonly Lazy<string> Path_ = new(() =>
    {
        string? env = Environment.GetEnvironmentVariable("OPCBRIDGE_DATA");
        return string.IsNullOrWhiteSpace(env) ? AppContext.BaseDirectory : Path.TrimEndingDirectorySeparator(env);
    });

    public static string Value => Path_.Value;

    /// <summary>True when OPCBRIDGE_DATA supplies the data folder (e.g. a Docker volume); false when the app folder is used.</summary>
    public static bool IsEnvironmentOverride { get; } =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPCBRIDGE_DATA"));

    public static string Combine(string fileName) => Path.Combine(Value, fileName);
}
