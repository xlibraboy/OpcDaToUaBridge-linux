using Microsoft.Extensions.Logging;
using OpcBridge.App.Auth;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// users.json round-trip, PBKDF2 verification and the "never lock everyone out"
/// guards (last enabled administrator cannot be demoted, disabled or deleted).
/// </summary>
public sealed class UserStoreTests : IDisposable
{
    private static readonly ILogger<UserStore> Log = LoggerFactory.Create(_ => { }).CreateLogger<UserStore>();

    private readonly string _dir;
    private readonly string _path;

    public UserStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "opcbridge-auth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "users.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private UserStore CreateStore() => new(Log, _path);

    [Fact]
    public void FreshStore_SeedsTheDefaultAdministrator()
    {
        UserStore store = CreateStore();

        Assert.True(store.SeededDefaultAdmin);
        Assert.Equal("admin", UserStore.DefaultAdminUsername);
        Assert.NotNull(store.Authenticate("admin", "admin"));
        Assert.Equal(UserRole.Admin, store.TryGet("admin")!.Role);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void UsersFile_StoresHashesOnly()
    {
        UserStore store = CreateStore();
        store.Create("op1", "hunter2", "Operator", "");
        string json = File.ReadAllText(_path);

        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.Contains("PasswordHash", json, StringComparison.Ordinal);
        Assert.Contains("PasswordSalt", json, StringComparison.Ordinal);

        UserAccount admin = store.TryGet("admin")!;
        Assert.NotEqual(UserStore.DefaultAdminPassword, admin.PasswordHash);
        Assert.Equal(32, Convert.FromBase64String(admin.PasswordHash).Length);
        Assert.Equal(16, Convert.FromBase64String(admin.PasswordSalt).Length);
    }

    [Fact]
    public void CreatedUser_SignsIn_AndWrongPasswordIsRejected()
    {
        UserStore store = CreateStore();
        Assert.True(store.Create("op1", "secret", "Operator", "Operator One").Ok);

        UserAccount? signedIn = store.Authenticate("op1", "secret");
        Assert.NotNull(signedIn);
        Assert.Equal(UserRole.Operator, signedIn!.Role);
        Assert.Equal("Operator One", signedIn.DisplayName);
        Assert.NotNull(signedIn.LastLoginUtc);
        Assert.Null(store.Authenticate("op1", "wrong"));
        Assert.Null(store.Authenticate("OP1", "nope"));
    }

    [Fact]
    public void UsernameLookup_IsCaseInsensitive()
    {
        UserStore store = CreateStore();
        store.Create("Tester", "secret", "Viewer", "");

        Assert.NotNull(store.Authenticate("tester", "secret"));
        Assert.False(store.Create("TESTER", "secret", "Viewer", "").Ok);
    }

    [Fact]
    public void Create_ValidatesInput()
    {
        UserStore store = CreateStore();

        Assert.False(store.Create("", "secret", "Viewer", "").Ok);
        Assert.False(store.Create("has space", "secret", "Viewer", "").Ok);
        Assert.False(store.Create("shortpw", "abc", "Viewer", "").Ok);          // below MinPasswordLength
        Assert.False(store.Create("badrole", "secret", "Superuser", "").Ok);
        Assert.True(store.Create("ok", "secret", "viewer", "").Ok);             // role text is case-insensitive
    }

    [Fact]
    public void SetPassword_RotatesTheCredential()
    {
        UserStore store = CreateStore();
        store.Create("op1", "secret", "Operator", "");

        Assert.True(store.SetPassword("op1", "newpass").Ok);
        Assert.Null(store.Authenticate("op1", "secret"));
        Assert.NotNull(store.Authenticate("op1", "newpass"));
    }

    [Fact]
    public void DisabledUser_CannotSignIn()
    {
        UserStore store = CreateStore();
        store.Create("op1", "secret", "Operator", "");
        Assert.True(store.SetEnabled("op1", false).Ok);

        Assert.Null(store.Authenticate("op1", "secret"));
        Assert.True(store.SetEnabled("op1", true).Ok);
        Assert.NotNull(store.Authenticate("op1", "secret"));
    }

    [Fact]
    public void LastEnabledAdministrator_CannotBeDemotedDisabledOrDeleted()
    {
        UserStore store = CreateStore(); // seeds exactly one admin

        Assert.False(store.SetRole("admin", "Engineer").Ok);
        Assert.False(store.SetEnabled("admin", false).Ok);
        Assert.False(store.Delete("admin").Ok);
        Assert.Equal(UserRole.Admin, store.TryGet("admin")!.Role);
    }

    [Fact]
    public void AdditionalAdministrator_AllowsTheOriginalToBeRemoved()
    {
        UserStore store = CreateStore();
        Assert.True(store.Create("admin2", "secret", "Admin", "").Ok);

        Assert.True(store.SetRole("admin", "Viewer").Ok);
        Assert.True(store.Delete("admin").Ok);
        Assert.Null(store.TryGet("admin"));
    }

    [Fact]
    public void Users_PersistAcrossStoreInstances()
    {
        UserStore first = CreateStore();
        first.Create("field", "secret", "Operator", "Field Ops");

        UserStore second = CreateStore();
        UserAccount? reloaded = second.TryGet("field");
        Assert.NotNull(reloaded);
        Assert.Equal("Field Ops", reloaded!.DisplayName);
        Assert.Equal(UserRole.Operator, reloaded.Role);
        Assert.NotNull(second.Authenticate("field", "secret"));
    }

    [Fact]
    public void Hashing_IsSaltedPerAccount()
    {
        (string hashA, string saltA, int iterationsA) = UserStore.HashPassword("same-password");
        (string hashB, string saltB, _) = UserStore.HashPassword("same-password");

        Assert.NotEqual(saltA, saltB);
        Assert.NotEqual(hashA, hashB);
        Assert.Equal(UserStore.DefaultIterations, iterationsA);
        Assert.True(UserStore.VerifyPassword("same-password", hashA, saltA, iterationsA));
        Assert.False(UserStore.VerifyPassword("other-password", hashA, saltA, iterationsA));
        Assert.False(UserStore.VerifyPassword("same-password", "not-base64", saltA, iterationsA));
    }

    [Fact]
    public void DisplayName_IsOptionalAndBounded()
    {
        UserStore store = CreateStore();
        store.Create("nodisplay", "secret", "Viewer", "   ");
        Assert.Equal("nodisplay", store.TryGet("nodisplay")!.DisplayName);

        Assert.False(store.SetDisplayName("nodisplay", new string('x', 200)).Ok);
        Assert.True(store.SetDisplayName("nodisplay", "Nice Name").Ok);
        Assert.Equal("Nice Name", store.TryGet("nodisplay")!.DisplayName);
    }

    [Fact]
    public void SessionStore_SlidesExpiryAndDropsRemovedUsers()
    {
        AuthSessionStore sessions = new(Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            Enabled = true,
            SessionHours = 1
        }));

        string token = sessions.Create("op1", UserRole.Operator);
        Assert.True(sessions.TryValidate(token, out string user, out UserRole role));
        Assert.Equal("op1", user);
        Assert.Equal(UserRole.Operator, role);
        Assert.False(sessions.TryValidate("not-a-token", out _, out _));

        sessions.RemoveUser("op1");
        Assert.False(sessions.TryValidate(token, out _, out _));

        string other = sessions.Create("op2", UserRole.Viewer);
        sessions.UpdateRole("op2", UserRole.Engineer);
        Assert.True(sessions.TryValidate(other, out _, out UserRole updated));
        Assert.Equal(UserRole.Engineer, updated);

        sessions.Remove(other);
        Assert.False(sessions.TryValidate(other, out _, out _));
    }
}
