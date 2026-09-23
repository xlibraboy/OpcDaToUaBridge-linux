using System.Text.Json.Serialization;

namespace OpcBridge.App.Auth;

/// <summary>
/// Dashboard access roles, ordered least → most privileged: a role grants every
/// capability of the roles below it. Viewer reads, Operator also writes tag
/// values, Engineer also changes configuration, Admin also manages users.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserRole
{
    Viewer = 0,
    Operator = 1,
    Engineer = 2,
    Admin = 3,

    /// <summary>Unknown/unassigned; never granted. Kept last so it sorts above Admin.</summary>
    None = 99
}

public static class UserRoles
{
    /// <summary>Roles assignable to a user, most privileged first.</summary>
    public static readonly UserRole[] Assignable =
    {
        UserRole.Admin,
        UserRole.Engineer,
        UserRole.Operator,
        UserRole.Viewer
    };

    public static bool TryParse(string? text, out UserRole role)
    {
        role = UserRole.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (Enum.TryParse(text.Trim(), ignoreCase: true, out UserRole parsed)
            && parsed != UserRole.None)
        {
            role = parsed;
            return true;
        }

        return false;
    }

    /// <summary>Case-insensitive membership test for the assignable role set.</summary>
    public static bool IsAssignable(string? text) => TryParse(text, out _);

    public static string Format(UserRole role) => role == UserRole.None ? "None" : role.ToString();
}
