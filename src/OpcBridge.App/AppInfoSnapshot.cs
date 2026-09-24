using System.Reflection;
using System.Runtime.InteropServices;

namespace OpcBridge.App;

public sealed record AppInfoSnapshot(
    string Name,
    string Version,
    string InformationalVersion,
    string Framework,
    string ProcessArchitecture,
    string OsDescription,
    string MachineName,
    string Creator,
    string Section)
{
    public static AppInfoSnapshot CreateCurrent(string creator = "", string section = "")
    {
        Assembly assembly = typeof(AppInfoSnapshot).Assembly;
        AssemblyName assemblyName = assembly.GetName();
        return new AppInfoSnapshot(
            assemblyName.Name ?? "OpcBridge.App",
            assemblyName.Version?.ToString() ?? "0.0.0.0",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.OSDescription,
            Environment.MachineName,
            creator,
            section);
    }
}
