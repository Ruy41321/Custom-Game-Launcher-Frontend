using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.Core.Tests.Authentication;

public sealed class AuthenticationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly IAuthApi _api = Substitute.For<IAuthApi>();
    private readonly ITokenStore _store = Substitute.For<ITokenStore>();
    private readonly FakeTimeProvider _clock = new(Now);

    private static AuthSession SessionExpiring(
        DateTimeOffset expiresAt, string accessToken = "access", string refreshToken = "refresh") =>
        new()
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            AccessTokenExpiresAt = expiresAt,
            User = new AuthenticatedUser { Id = "u1", Email = "a@b.c" },
            Permissions = ["library.read"],
        };

    private AuthenticationService CreateService() =>
        new(_api, _store, NullLogger<AuthenticationService>.Instance, _clock);

    // --- restoring on startup -------------------------------------------------------------

    [Fact]
    public async Task NothingOnDiskMeansSignedOut()
    {
        _store.LoadAsync(Arg.Any<CancellationToken>()).Returns((AuthSession?)null);
        using AuthenticationService service = CreateService();

        Assert.False(await service.RestoreAsync(TestContext.Current.CancellationToken));
        Assert.False(service.IsAuthenticated);
    }

    [Fact]
    public async Task AStillValidSessionIsRestoredWithoutTalkingToTheServer()
    {
        _store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddMinutes(10)));
        using AuthenticationService service = CreateService();

        Assert.True(await service.RestoreAsync(TestContext.Current.CancellationToken));
        Assert.Equal("access", service.CurrentSession?.AccessToken);
        await _api.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnExpiredSessionIsRotatedOnStartup()
    {
        _store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddMinutes(-5), refreshToken: "old"));
        _api.RefreshAsync("old", Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddMinutes(15), "fresh", "rotated"));

        using AuthenticationService service = CreateService();

        Assert.True(await service.RestoreAsync(TestContext.Current.CancellationToken));
        Assert.Equal("fresh", service.CurrentSession?.AccessToken);
        await _store.Received(1).SaveAsync(
            Arg.Is<AuthSession>(session => session!.RefreshToken == "rotated"),
            Arg.Any<CancellationToken>());
    }

    // A refresh token that the server rejects is spent for good — most likely its family was
    // revoked because somebody replayed one. The only correct answer is the login view.
    [Fact]
    public async Task ARejectedRefreshTokenSignsTheUserOut()
    {
        _store.LoadAsync(Arg.Any<CancellationToken>()).Returns(SessionExpiring(Now.AddMinutes(-5)));
        _api.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Unauthenticated, "token revoked"));

        using AuthenticationService service = CreateService();

        Assert.False(await service.RestoreAsync(TestContext.Current.CancellationToken));
        Assert.False(service.IsAuthenticated);
        await _store.Received(1).ClearAsync(Arg.Any<CancellationToken>());
    }

    // Offline startup must not throw away a session that is probably still good — and must not
    // answer by demanding a password, which is no more checkable offline than the refresh was.
    [Fact]
    public async Task AnUnreachableServerKeepsTheStoredSessionAndStaysSignedIn()
    {
        _store.LoadAsync(Arg.Any<CancellationToken>()).Returns(SessionExpiring(Now.AddMinutes(-5)));
        _api.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "no route to host"));

        using AuthenticationService service = CreateService();

        Assert.True(await service.RestoreAsync(TestContext.Current.CancellationToken));
        Assert.True(service.IsAuthenticated);
        await _store.DidNotReceive().ClearAsync(Arg.Any<CancellationToken>());
    }

    // The session that survives is the stored one, untouched: nothing rotated it, so the next
    // call that reaches a server is the one that does.
    [Fact]
    public async Task TheSessionKeptOfflineIsTheStoredOne()
    {
        AuthSession stored = SessionExpiring(Now.AddMinutes(-5));
        _store.LoadAsync(Arg.Any<CancellationToken>()).Returns(stored);
        _api.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.DependencyFailure, "gateway down"));

        using AuthenticationService service = CreateService();
        await service.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(stored.RefreshToken, service.CurrentSession?.RefreshToken);
        await _store.DidNotReceive().SaveAsync(
            Arg.Any<AuthSession>(), Arg.Any<CancellationToken>());
    }

    // --- signing in and out --------------------------------------------------------------

    [Fact]
    public async Task SigningInPersistsTheSessionAndAnnouncesIt()
    {
        _api.LoginAsync("a@b.c", "correct horse", Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddMinutes(15)));

        using AuthenticationService service = CreateService();
        AuthSession? announced = null;
        service.SessionChanged += (_, args) => announced = args.Session;

        await service.SignInAsync("a@b.c", "correct horse", TestContext.Current.CancellationToken);

        Assert.True(service.IsAuthenticated);
        Assert.Equal("access", announced?.AccessToken);
        await _store.Received(1).SaveAsync(Arg.Any<AuthSession>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SigningOutRevokesTheSessionServerSide()
    {
        _api.LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddMinutes(15), refreshToken: "live"));
        using AuthenticationService service = CreateService();
        await service.SignInAsync("a@b.c", "pw", TestContext.Current.CancellationToken);

        await service.SignOutAsync(TestContext.Current.CancellationToken);

        Assert.False(service.IsAuthenticated);
        await _api.Received(1).LogoutAsync("live", Arg.Any<CancellationToken>());
        await _store.Received(1).ClearAsync(Arg.Any<CancellationToken>());
    }

    // Asking to sign out and staying signed in because the network is down would be worse
    // than the stale server-side session the local clear leaves behind.
    [Fact]
    public async Task SigningOutSucceedsEvenWhenTheServerCannotBeReached()
    {
        _api.LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddMinutes(15)));
        _api.LogoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "offline"));

        using AuthenticationService service = CreateService();
        await service.SignInAsync("a@b.c", "pw", TestContext.Current.CancellationToken);

        await service.SignOutAsync(TestContext.Current.CancellationToken);

        Assert.False(service.IsAuthenticated);
        await _store.Received(1).ClearAsync(Arg.Any<CancellationToken>());
    }

    // --- handing out access tokens -------------------------------------------------------

    [Fact]
    public async Task AskingForATokenWhileSignedOutIsAnAuthenticationFailure()
    {
        using AuthenticationService service = CreateService();

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => service.GetAccessTokenAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Unauthenticated, exception.Code);
    }

    [Fact]
    public async Task AFreshTokenIsHandedBackUnchanged()
    {
        _api.LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddMinutes(15)));
        using AuthenticationService service = CreateService();
        await service.SignInAsync("a@b.c", "pw", TestContext.Current.CancellationToken);

        Assert.Equal("access", await service.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        await _api.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The margin is the point: a token valid for another 30 seconds will have expired by the
    // time the request it is attached to reaches the server.
    [Fact]
    public async Task ATokenAboutToExpireIsRotatedBeforeItIsUsed()
    {
        _api.LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddSeconds(30)));
        _api.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddMinutes(15), "fresh"));

        using AuthenticationService service = CreateService();
        await service.SignInAsync("a@b.c", "pw", TestContext.Current.CancellationToken);

        Assert.Equal("fresh", await service.GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    // Two rotations in flight means the second replays a token the first already spent, and
    // the server answers a replay by revoking the whole family.
    [Fact]
    public async Task ConcurrentCallersRotateTheSessionExactlyOnce()
    {
        _api.LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddSeconds(-1)));
        _api.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Yield();
                return SessionExpiring(_clock.Now.AddMinutes(15), "fresh");
            });

        using AuthenticationService service = CreateService();
        await service.SignInAsync("a@b.c", "pw", TestContext.Current.CancellationToken);

        string[] tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            service.GetAccessTokenAsync(TestContext.Current.CancellationToken)));

        Assert.All(tokens, token => Assert.Equal("fresh", token));
        await _api.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARejectedRotationClearsTheSessionAndReportsTheFailure()
    {
        _api.LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddSeconds(-1)));
        _api.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Unauthenticated, "family revoked"));

        using AuthenticationService service = CreateService();
        await service.SignInAsync("a@b.c", "pw", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ApiException>(
            () => service.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.False(service.IsAuthenticated);
    }

    // --- permissions ---------------------------------------------------------------------

    [Fact]
    public async Task PermissionsComeFromTheSessionAndAreGoneWithIt()
    {
        _api.LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SessionExpiring(Now.AddMinutes(15)));
        using AuthenticationService service = CreateService();

        Assert.False(service.HasPermission(Permissions.LibraryRead));

        await service.SignInAsync("a@b.c", "pw", TestContext.Current.CancellationToken);

        Assert.True(service.HasPermission(Permissions.LibraryRead));
        Assert.False(service.HasPermission(Permissions.GamePublish));

        await service.SignOutAsync(TestContext.Current.CancellationToken);

        Assert.False(service.HasPermission(Permissions.LibraryRead));
    }
}

public sealed class AuthSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(0, true)]
    [InlineData(-5, true)]
    public void AMinuteOfMarginDecidesWhenATokenIsSpent(int minutesLeft, bool needsRefresh)
    {
        var session = new AuthSession { AccessTokenExpiresAt = Now.AddMinutes(minutesLeft) };

        Assert.Equal(needsRefresh, session.NeedsRefresh(Now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void PermissionsAreMatchedExactly()
    {
        var session = new AuthSession { Permissions = ["game.read"] };

        Assert.True(session.HasPermission("game.read"));
        Assert.False(session.HasPermission("game.readmore"));
        Assert.False(session.HasPermission("Game.Read"));
    }
}
