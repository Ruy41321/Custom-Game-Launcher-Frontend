using System.Security.Cryptography;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GameLauncher.Core.Tests.Discovery;

public sealed class EndpointResolverTests : IDisposable
{
    private const string Shipped = "http://shipped.example.com/api/v1/";
    private const string FromRegistry = "https://moved.example.com/api/v1/";

    private readonly IServiceRegistryApi _registry = Substitute.For<IServiceRegistryApi>();
    private readonly IEndpointCache _cache = Substitute.For<IEndpointCache>();
    private readonly ECDsa _key;
    private readonly string _publicKey;

    public EndpointResolverTests()
    {
        (_publicKey, _key) = RegistrySigningFixture.NewKey();
    }

    public void Dispose() => _key.Dispose();

    private EndpointResolver Resolver(string? publicKey = null) =>
        new(_registry, _cache, NullLogger<EndpointResolver>.Instance, publicKey ?? _publicKey);

    private static LauncherConfiguration Configured() => new()
    {
        ApiBaseUrl = Shipped,
        ServiceRegistry = new ServiceRegistryConfiguration
        {
            Url = "https://registry.example.com/",
            ServiceKey = "game-launcher-api",
            Environment = "production",
        },
    };

    /* ------------------------------------------------------------- disabled */

    [Fact]
    public async Task UsesTheShippedAddressWhenNoRegistryIsConfigured()
    {
        LauncherConfiguration configuration = new() { ApiBaseUrl = Shipped };

        ResolvedEndpoint endpoint = await Resolver()
            .ResolveAsync(configuration, TestContext.Current.CancellationToken);

        Assert.Equal(Shipped, endpoint.BaseUrl);
        Assert.Equal(EndpointSource.ShippedConfiguration, endpoint.Source);
        await _registry.DidNotReceiveWithAnyArgs().GetSignedEndpointAsync(
            Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A build with an address to ask and no key to check the answer asks nothing: believing
    /// an unverifiable answer is the one thing this feature must never do.
    /// </summary>
    [Fact]
    public async Task AsksNothingWhenThisBuildCarriesNoKey()
    {
        ResolvedEndpoint endpoint = await Resolver(publicKey: "")
            .ResolveAsync(Configured(), TestContext.Current.CancellationToken);

        Assert.Equal(Shipped, endpoint.BaseUrl);
        await _registry.DidNotReceiveWithAnyArgs().GetSignedEndpointAsync(
            Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /* ------------------------------------------------------------- resolving */

    [Fact]
    public async Task AsksTheRegistryWhenNothingIsCached()
    {
        _cache.ReadAsync("game-launcher-api", "production", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _registry.GetSignedEndpointAsync(
                Arg.Any<Uri>(), "game-launcher-api", "production", Arg.Any<CancellationToken>())
            .Returns(RegistrySigningFixture.SignedEndpoint(_key, FromRegistry));

        ResolvedEndpoint endpoint = await Resolver()
            .ResolveAsync(Configured(), TestContext.Current.CancellationToken);

        Assert.Equal(FromRegistry, endpoint.BaseUrl);
        Assert.Equal(EndpointSource.Registry, endpoint.Source);
    }

    /// <summary>
    /// The start-up path never waits on a network it does not need: a stored claim is used as
    /// it stands, and the refresh that might replace it happens behind the window.
    /// </summary>
    [Fact]
    public async Task UsesTheStoredClaimWithoutAskingTheRegistry()
    {
        _cache.ReadAsync("game-launcher-api", "production", Arg.Any<CancellationToken>())
            .Returns(RegistrySigningFixture.SignedEndpoint(_key, FromRegistry));

        ResolvedEndpoint endpoint = await Resolver()
            .ResolveAsync(Configured(), TestContext.Current.CancellationToken);

        Assert.Equal(FromRegistry, endpoint.BaseUrl);
        Assert.Equal(EndpointSource.Cache, endpoint.Source);
        await _registry.DidNotReceiveWithAnyArgs().GetSignedEndpointAsync(
            Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The cache file is writable by anything running as this user, so it is verified on the
    /// way in exactly as a response is. A tampered one is a cache miss, not a redirection.
    /// </summary>
    [Fact]
    public async Task IgnoresAStoredClaimThatDoesNotVerify()
    {
        (_, ECDsa attacker) = RegistrySigningFixture.NewKey();
        using (attacker)
        {
            _cache.ReadAsync("game-launcher-api", "production", Arg.Any<CancellationToken>())
                .Returns(RegistrySigningFixture.SignedEndpoint(attacker, "https://evil.example.com/"));
            _registry.GetSignedEndpointAsync(
                    Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);

            ResolvedEndpoint endpoint = await Resolver()
                .ResolveAsync(Configured(), TestContext.Current.CancellationToken);

            Assert.Equal(Shipped, endpoint.BaseUrl);
        }
    }

    [Fact]
    public async Task FallsBackToTheShippedAddressWhenTheRegistryIsUnreachable()
    {
        _cache.ReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _registry.GetSignedEndpointAsync(
                Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        ResolvedEndpoint endpoint = await Resolver()
            .ResolveAsync(Configured(), TestContext.Current.CancellationToken);

        Assert.Equal(Shipped, endpoint.BaseUrl);
        Assert.Equal(EndpointSource.ShippedConfiguration, endpoint.Source);
    }

    /// <summary>
    /// A registry that accepts the connection and then says nothing is the failure that
    /// actually happens (D78), and it must not be able to hold the window shut.
    /// </summary>
    [Fact]
    public async Task GivesUpOnARegistryThatNeverAnswers()
    {
        _cache.ReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _registry.GetSignedEndpointAsync(
                Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return (string?)null;
            });

        ResolvedEndpoint endpoint = await Resolver()
            .ResolveAsync(Configured(), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(Shipped, endpoint.BaseUrl);
    }

    [Fact]
    public async Task StoresAVerifiedAnswer()
    {
        string envelope = RegistrySigningFixture.SignedEndpoint(_key, FromRegistry);
        _cache.ReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _registry.GetSignedEndpointAsync(
                Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(envelope);

        await Resolver().ResolveAsync(Configured(), TestContext.Current.CancellationToken);

        await _cache.Received(1).WriteAsync(
            "game-launcher-api", "production", envelope, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoresNothingWhenTheAnswerDoesNotVerify()
    {
        (_, ECDsa attacker) = RegistrySigningFixture.NewKey();
        using (attacker)
        {
            _cache.ReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);
            _registry.GetSignedEndpointAsync(
                    Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(RegistrySigningFixture.SignedEndpoint(attacker, "https://evil.example.com/"));

            await Resolver().ResolveAsync(Configured(), TestContext.Current.CancellationToken);

            await _cache.DidNotReceiveWithAnyArgs().WriteAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
    }

    /* -------------------------------------------------------------- refresh */

    [Fact]
    public async Task RefreshAlwaysAsksEvenWithSomethingStored()
    {
        _cache.ReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RegistrySigningFixture.SignedEndpoint(_key, Shipped, issuedAt: "2026-08-19T10:00:00Z"));
        _registry.GetSignedEndpointAsync(
                Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RegistrySigningFixture.SignedEndpoint(_key, FromRegistry, issuedAt: "2026-08-19T12:00:00Z"));

        EndpointClaim? claim = await Resolver()
            .RefreshAsync(Configured(), TestContext.Current.CancellationToken);

        Assert.NotNull(claim);
        Assert.Equal(FromRegistry, claim.BaseUrl);
        await _cache.Received(1).WriteAsync(
            "game-launcher-api", "production", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A genuine, correctly signed answer from before the address moved is a replay. Keeping
    /// it would move every launcher that receives it back to the old backend.
    /// </summary>
    [Fact]
    public async Task DoesNotReplaceAStoredClaimWithAnOlderOne()
    {
        _cache.ReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RegistrySigningFixture.SignedEndpoint(_key, FromRegistry, issuedAt: "2026-08-19T12:00:00Z"));
        _registry.GetSignedEndpointAsync(
                Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RegistrySigningFixture.SignedEndpoint(_key, Shipped, issuedAt: "2026-08-19T10:00:00Z"));

        await Resolver().RefreshAsync(Configured(), TestContext.Current.CancellationToken);

        await _cache.DidNotReceiveWithAnyArgs().WriteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshDoesNothingWithoutARegistry()
    {
        LauncherConfiguration configuration = new() { ApiBaseUrl = Shipped };

        Assert.Null(await Resolver().RefreshAsync(configuration, TestContext.Current.CancellationToken));
        await _registry.DidNotReceiveWithAnyArgs().GetSignedEndpointAsync(
            Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
