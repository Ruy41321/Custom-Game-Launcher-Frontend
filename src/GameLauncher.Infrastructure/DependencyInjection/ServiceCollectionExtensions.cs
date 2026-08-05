using System.Net.Http.Headers;
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
using GameLauncher.Infrastructure.Api;
using GameLauncher.Infrastructure.Authentication;
using GameLauncher.Infrastructure.Configuration;
using GameLauncher.Infrastructure.Downloads;
using GameLauncher.Infrastructure.Installs;
using GameLauncher.Infrastructure.Launching;
using GameLauncher.Infrastructure.Media;
using GameLauncher.Infrastructure.Platform;
using GameLauncher.Infrastructure.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncher.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>How long a request may take before it is reported as a network failure.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers every infrastructure implementation behind its Core interface. The UI layer
    /// calls this and then knows nothing about which concrete type it received.
    /// </summary>
    public static IServiceCollection AddLauncherInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IPathProvider>(_ => new PathProvider());
        services.AddSingleton<IRuntimePlatform>(_ => new RuntimePlatform());
        services.AddSingleton<IDiskSpaceProbe, DiskSpaceProbe>();
        services.AddSingleton<ILauncherConfigurationProvider, LauncherConfigurationProvider>();

        // Read once and cached for the process. The HTTP clients need the API endpoint before
        // any view exists, and the shell needs the same document to brand itself from.
        services.AddSingleton(provider => provider
            .GetRequiredService<ILauncherConfigurationProvider>()
            .LoadAsync().GetAwaiter().GetResult());

        services.AddSingleton<IUserSettingsStore, JsonUserSettingsStore>();
        services.AddSingleton<ILocalizationService>(_ => new ResourceManagerLocalizationService());
        services.AddSingleton<IApiErrorPresenter, ApiErrorPresenter>();
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<ITokenStore, FileTokenStore>();
        services.AddSingleton<IInstallStore, SqliteInstallStore>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();

        // Erasure sits here rather than on IAuthenticationService because the account route
        // runs on the authenticated client, whose handler depends on IAuthenticationService:
        // the container would refuse a graph where the session service needed it back.
        services.AddSingleton<IAccountService, AccountService>();
        services.AddTransient<BearerTokenHandler>();

        // The auth client deliberately carries no bearer token: refreshing has to work exactly
        // when the access token has expired, and a handler that fetched one first would call
        // back into this very client.
        services.AddHttpClient<IAuthApi, AuthApiClient>(ConfigureClient);

        // Also tokenless, and for a related reason: the limits document is what a launcher
        // reads before it knows whether it can sign in at all, and the route needs no token.
        services.AddHttpClient<ICapabilitiesApi, CapabilitiesApiClient>(ConfigureClient);
        services.AddSingleton<IServerCapabilityProvider, CachedServerCapabilityProvider>();

        // Authenticated, unlike the auth client beside it: erasing an account is something a
        // signed-in account does to itself, and the password in the body is a second proof
        // rather than the first one.
        services.AddHttpClient<IAccountApi, AccountApiClient>(ConfigureClient)
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddHttpClient<ICatalogApi, CatalogApiClient>(ConfigureClient)
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddHttpClient<ILibraryApi, LibraryApiClient>(ConfigureClient)
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddHttpClient<IDownloadApi, DownloadApiClient>(ConfigureClient)
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddHttpClient<IPublishingApi, PublishingApiClient>(ConfigureClient)
            .AddHttpMessageHandler<BearerTokenHandler>();

        // A third client, and deliberately not one of the two above. It talks to the file
        // server rather than the API, on absolute URLs that carry their own authorization, so
        // it has no base address and — above all — no bearer token to hand to whatever host
        // the API named. The timeout is infinite because a build is arbitrarily large and this
        // one covers the body as well as the headers: a stalled transfer is bounded by the
        // caller's cancellation, not by a clock that started at the first byte.
        services.AddHttpClient<IBlobFetcher, BlobFetcher>(ConfigureFileServerClient);

        // A fourth client, for artwork. Public, unsigned URLs on whatever host the API named,
        // so it carries no bearer token for the same reason the file-server client does not.
        // Unlike that one it keeps an ordinary timeout: a cover is small, and a picture that
        // is taking thirty seconds is a picture the page is better off without.
        services.AddHttpClient<IImageLoader, CachingImageLoader>(ConfigureMediaClient);

        services.AddSingleton<IInstallationService, InstallationService>();
        services.AddSingleton<IGameLauncher, ProcessGameLauncher>();
        services.AddSingleton<IBuildPackager, DirectoryBuildPackager>();
        services.AddSingleton<IBuildPublisher, BuildPublisher>();

        return services;
    }

    private static void ConfigureMediaClient(HttpClient client)
    {
        client.Timeout = RequestTimeout;
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("CustomGameLauncher", ThisAssemblyVersion()));
    }

    private static void ConfigureFileServerClient(HttpClient client)
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("CustomGameLauncher", ThisAssemblyVersion()));
    }

    private static void ConfigureClient(IServiceProvider provider, HttpClient client)
    {
        LauncherConfiguration configuration = provider.GetRequiredService<LauncherConfiguration>();

        client.BaseAddress = new Uri(EndingInSlash(configuration.ApiBaseUrl));
        client.Timeout = RequestTimeout;
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("CustomGameLauncher", ThisAssemblyVersion()));
    }

    /// <summary>
    /// A base address without a trailing slash silently drops its last segment when a relative
    /// path is resolved against it, which would turn <c>/api/v1/</c> into <c>/api/</c>.
    /// </summary>
    private static string EndingInSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";

    private static string ThisAssemblyVersion() =>
        typeof(ServiceCollectionExtensions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
