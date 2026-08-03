using GameLauncher.Core.Authentication;
using GameLauncher.Infrastructure.Authentication;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Infrastructure.Tests.Authentication;

public sealed class FileTokenStoreTests
{
    private static readonly AuthSession Session = new()
    {
        AccessToken = "access",
        RefreshToken = "refresh",
        AccessTokenExpiresAt = new DateTimeOffset(2026, 8, 3, 12, 15, 0, TimeSpan.Zero),
        User = new AuthenticatedUser
        {
            Id = "u1",
            Email = "luigi@example.com",
            DisplayName = "Luigi",
            EmailVerified = true,
            UploadQuotaBytes = 5368709120,
        },
        Permissions = ["library.read", "game.download"],
    };

    private static FileTokenStore StoreFor(string path) =>
        new(path, NullLogger<FileTokenStore>.Instance);

    [Fact]
    public async Task NothingStoredMeansNoSession()
    {
        using var directory = new TemporaryDirectory();
        using FileTokenStore store = StoreFor(directory.File("session.json"));

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASessionSurvivesARoundTrip()
    {
        using var directory = new TemporaryDirectory();
        using FileTokenStore store = StoreFor(directory.File("session.json"));

        await store.SaveAsync(Session, TestContext.Current.CancellationToken);
        AuthSession? read = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Field by field rather than with record equality: a record compares a collection
        // member by reference, so == would pass on two sessions with different permissions.
        Assert.NotNull(read);
        Assert.Equal(Session.AccessToken, read.AccessToken);
        Assert.Equal(Session.RefreshToken, read.RefreshToken);
        Assert.Equal(Session.AccessTokenExpiresAt, read.AccessTokenExpiresAt);
        Assert.Equal(Session.User, read.User);
        Assert.Equal(Session.Permissions, read.Permissions);
    }

    [Fact]
    public async Task ClearingRemovesTheStoredSession()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("session.json");
        using FileTokenStore store = StoreFor(path);
        await store.SaveAsync(Session, TestContext.Current.CancellationToken);

        await store.ClearAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(path));
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    // Signing out when there is nothing to sign out of is not an error.
    [Fact]
    public async Task ClearingWhatIsNotThereSucceeds()
    {
        using var directory = new TemporaryDirectory();
        using FileTokenStore store = StoreFor(directory.File("session.json"));

        await store.ClearAsync(TestContext.Current.CancellationToken);
    }

    // A corrupt file must send the user to the login view, never crash the launcher.
    [Fact]
    public async Task ACorruptFileReadsAsNoSession()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.WriteFile("session.json", "{ not json at all");
        using FileTokenStore store = StoreFor(path);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    // Without a refresh token there is nothing to restore, and reporting a session anyway
    // only moves the failure to the first request that needs one.
    [Fact]
    public async Task ASessionWithoutARefreshTokenIsNotASession()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.WriteFile(
            "session.json", """{ "accessToken": "access", "refreshToken": "" }""");
        using FileTokenStore store = StoreFor(path);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SavingCreatesTheDirectoryAndLeavesNoTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "nested", "session.json");
        using FileTokenStore store = StoreFor(path);

        await store.SaveAsync(Session, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SavingTwiceKeepsTheNewerSession()
    {
        using var directory = new TemporaryDirectory();
        using FileTokenStore store = StoreFor(directory.File("session.json"));

        await store.SaveAsync(Session, TestContext.Current.CancellationToken);
        await store.SaveAsync(
            Session with { RefreshToken = "rotated" }, TestContext.Current.CancellationToken);

        AuthSession? read = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("rotated", read?.RefreshToken);
    }

    // The token is a bearer credential. Windows relies on the per-user directory's ACL; the
    // Unix platforms get the mode set on the temporary file before anything is written to it,
    // so it is never briefly readable by anybody else.
    [Fact]
    public async Task OnUnixTheFileIsReadableOnlyByItsOwner()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "File modes are a Unix concept; Windows relies on the directory ACL.");

        using var directory = new TemporaryDirectory();
        string path = directory.File("session.json");
        using FileTokenStore store = StoreFor(path);

        await store.SaveAsync(Session, TestContext.Current.CancellationToken);

        // The skip above already decided this; the check is what tells the platform analyzer.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }
}
