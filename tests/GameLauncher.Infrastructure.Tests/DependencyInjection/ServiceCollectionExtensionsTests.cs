using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Media;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Publishing;
using GameLauncher.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncher.Infrastructure.Tests.DependencyInjection;

/// <summary>
/// The composition root is the one place a wiring mistake goes unnoticed until the app is
/// started by hand. Resolving every service here turns that into a failing test instead.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddLauncherInfrastructure();
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Theory]
    [InlineData(typeof(IPathProvider))]
    [InlineData(typeof(ILauncherConfigurationProvider))]
    [InlineData(typeof(LauncherConfiguration))]
    [InlineData(typeof(IUserSettingsStore))]
    [InlineData(typeof(ILocalizationService))]
    [InlineData(typeof(ITokenStore))]
    [InlineData(typeof(IAuthenticationService))]
    [InlineData(typeof(IAuthApi))]
    [InlineData(typeof(IAccountApi))]
    [InlineData(typeof(IAccountService))]
    [InlineData(typeof(ICatalogApi))]
    [InlineData(typeof(ILibraryApi))]
    [InlineData(typeof(IDownloadApi))]
    [InlineData(typeof(IInstallStore))]
    [InlineData(typeof(IBlobFetcher))]
    [InlineData(typeof(IDiskSpaceProbe))]
    [InlineData(typeof(IInstallationService))]
    [InlineData(typeof(IGameLauncher))]
    [InlineData(typeof(IPublishingApi))]
    [InlineData(typeof(IBuildPackager))]
    [InlineData(typeof(IBuildPublisher))]
    [InlineData(typeof(IImageLoader))]
    [InlineData(typeof(ICapabilitiesApi))]
    [InlineData(typeof(IServerCapabilityProvider))]
    public void EveryRegisteredServiceCanActuallyBeBuilt(Type serviceType)
    {
        using ServiceProvider provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService(serviceType));
    }

    // The auth client exists to break the cycle that would otherwise form: an authenticated
    // client needs a token, obtaining one is a call, and that call would need a token.
    [Fact]
    public void TheAuthenticatedClientsAndTheAuthClientCoexist()
    {
        using ServiceProvider provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IAuthApi>());
        Assert.NotNull(provider.GetRequiredService<ICatalogApi>());
    }

    /// <summary>
    /// The cycle erasure would have introduced if it had gone on <c>IAuthenticationService</c>:
    /// the account route runs on the authenticated client, whose handler needs the session
    /// service, so a session service that needed the account client back would make the graph
    /// unbuildable. Composing them in <c>AccountService</c> is what keeps this resolvable, and
    /// this asserts it rather than leaving it to a comment.
    /// </summary>
    [Fact]
    public void ErasureDoesNotMakeTheSessionServiceDependOnItsOwnTokenHandler()
    {
        using ServiceProvider provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IAccountService>());
        Assert.NotNull(provider.GetRequiredService<IAuthenticationService>());
    }

    [Fact]
    public void TheConfigurationIsReadOnceAndShared()
    {
        using ServiceProvider provider = BuildProvider();

        Assert.Same(
            provider.GetRequiredService<LauncherConfiguration>(),
            provider.GetRequiredService<LauncherConfiguration>());
    }
}
