using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.Core.Tests.Authentication;

public sealed class AccountServiceTests
{
    private readonly IAccountApi _api = Substitute.For<IAccountApi>();
    private readonly IAuthenticationService _authentication =
        Substitute.For<IAuthenticationService>();

    public AccountServiceTests() => _authentication.IsAuthenticated.Returns(true);

    private AccountService CreateService() =>
        new(_api, _authentication, NullLogger<AccountService>.Instance);

    [Fact]
    public async Task SendsThePasswordAndSignsOutAfterwards()
    {
        AccountService service = CreateService();

        await service.DeleteAsync("hunter2", "moving on", TestContext.Current.CancellationToken);

        await _api.Received(1).DeleteAccountAsync(
            Arg.Is<DeleteAccountRequest>(request =>
                request!.Password == "hunter2" && request.Reason == "moving on"),
            Arg.Any<CancellationToken>());

        await _authentication.Received(1).SignOutAsync(Arg.Any<CancellationToken>());
    }

    // An empty box is absence on the wire, not a reason the operator has to read as blank.
    [Fact]
    public async Task AnEmptyReasonIsSentAsNoReasonAtAll()
    {
        AccountService service = CreateService();

        await service.DeleteAsync("hunter2", "   ", TestContext.Current.CancellationToken);

        await _api.Received(1).DeleteAccountAsync(
            Arg.Is<DeleteAccountRequest>(request => request!.Reason == null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The ordering rule this class exists for. Signing out is a local truth; an erasure is the
    /// server's answer, and forgetting the session after a refusal would leave somebody signed
    /// out of an account that still exists, unable to read the reason they were given.
    /// </summary>
    [Fact]
    public async Task ARefusedErasureLeavesTheSessionAlone()
    {
        _api.DeleteAccountAsync(Arg.Any<DeleteAccountRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Unauthenticated, "wrong password"));

        AccountService service = CreateService();

        ApiException failure = await Assert.ThrowsAsync<ApiException>(
            () => service.DeleteAsync("wrong", null, TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Unauthenticated, failure.Code);
        await _authentication.DidNotReceive().SignOutAsync(Arg.Any<CancellationToken>());
    }

    // The last operator who can manage users is refused with a conflict. It has to reach the
    // caller as itself: it names the thing they have to do first.
    [Fact]
    public async Task TheLastOperatorRefusalReachesTheCallerUnchanged()
    {
        _api.DeleteAccountAsync(Arg.Any<DeleteAccountRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Conflict, "grant another one first"));

        AccountService service = CreateService();

        ApiException failure = await Assert.ThrowsAsync<ApiException>(
            () => service.DeleteAsync("hunter2", null, TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Conflict, failure.Code);
        await _authentication.DidNotReceive().SignOutAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefusesWhenNobodyIsSignedIn()
    {
        _authentication.IsAuthenticated.Returns(false);
        AccountService service = CreateService();

        ApiException failure = await Assert.ThrowsAsync<ApiException>(
            () => service.DeleteAsync("hunter2", null, TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Unauthenticated, failure.Code);
        await _api.DidNotReceive().DeleteAccountAsync(
            Arg.Any<DeleteAccountRequest>(), Arg.Any<CancellationToken>());
    }
}
