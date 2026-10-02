using System.Reflection;

namespace OpcBridge.App;

/// <summary>
/// Re-launches this application in one of its child modes (<c>--da-probe</c>, <c>--da-worker</c>).
/// A published app runs its own executable; when hosted by another executable (dotnet, the
/// test host) the assembly path is passed to that host instead.
/// </summary>
internal static class DaChildProcess
{
    internal static (string FileName, List<string> Arguments) Resolve(string modeArgument)
    {
        string? processPath = Environment.ProcessPath;
        List<string> arguments = new();
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(Path.GetFileNameWithoutExtension(processPath), "OpcBridge.App", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(modeArgument);
            return (processPath!, arguments);
        }

        // Hosted by another executable (dotnet, test host): run this assembly with its host.
        arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.Add(modeArgument);
        return (string.IsNullOrWhiteSpace(processPath) ? "dotnet" : processPath!, arguments);
    }
}
