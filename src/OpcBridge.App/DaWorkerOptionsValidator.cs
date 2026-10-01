using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Pure validation for per-source worker-isolation options, applied at the API boundary
/// before the request reaches <see cref="DaRuntimeSettings"/>. Normalization (unknown mode →
/// inProcess, trimming, password hygiene) lives in
/// <see cref="SourceConfigMigration.NormalizeWorkerOptions"/>; this type only rejects
/// combinations that cannot work.
/// </summary>
public static class DaWorkerOptionsValidator
{
    /// <summary>Returns an error message, or null when the options are acceptable.</summary>
    public static string? Validate(string? sourceType, DaWorkerOptions options)
    {
        if (!IsKnownMode(options.Mode))
        {
            return "Worker mode must be inProcess, group or own.";
        }

        string mode = DaWorkerModes.Normalize(options.Mode);
        bool isDa = string.Equals(sourceType, SourceTypes.OpcDa, StringComparison.OrdinalIgnoreCase);
        bool hasUser = !string.IsNullOrWhiteSpace(options.RunAsUser);

        if (!isDa && (mode != DaWorkerModes.InProcess || hasUser))
        {
            return "Worker isolation is only available for OPC DA sources.";
        }

        if (mode == DaWorkerModes.InProcess && hasUser)
        {
            return "A run-as account requires worker mode 'group' or 'own'.";
        }

        if (mode == DaWorkerModes.Group && !hasUser)
        {
            return "Worker mode 'group' requires a run-as account.";
        }

        if (!hasUser && !string.IsNullOrEmpty(options.RunAsPassword))
        {
            return "A run-as password requires a run-as account.";
        }

        if (hasUser && !IsValidAccount(options.RunAsUser!))
        {
            return "Run-as account must look like 'user', '.\\user' or 'DOMAIN\\user'.";
        }

        return null;
    }

    private static bool IsKnownMode(string? mode)
        => string.IsNullOrWhiteSpace(mode)
            || string.Equals(mode, DaWorkerModes.InProcess, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, DaWorkerModes.Group, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, DaWorkerModes.Own, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidAccount(string account)
    {
        string[] parts = account.Trim().Split('\\');
        if (parts.Length == 1)
        {
            return IsValidNamePart(parts[0]);
        }

        if (parts.Length == 2)
        {
            return (parts[0] == "." || IsValidNamePart(parts[0])) && IsValidNamePart(parts[1]);
        }

        return false;
    }

    private static bool IsValidNamePart(string value)
    {
        if (value.Length is < 1 or > 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
