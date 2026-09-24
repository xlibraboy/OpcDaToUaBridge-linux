namespace OpcBridge.App;

/// <summary>
/// Identity of the personal profile this install is registered to, bound from the
/// <c>Profile</c> section of appsettings.json. Company profiles leave both values
/// empty, which is what keeps the Personal Profile box off the About view.
/// </summary>
public sealed class ProfileOptions
{
    /// <summary>Person the personal profile is registered to.</summary>
    public string Creator { get; set; } = string.Empty;

    /// <summary>Section the personal profile belongs to.</summary>
    public string Section { get; set; } = string.Empty;
}
