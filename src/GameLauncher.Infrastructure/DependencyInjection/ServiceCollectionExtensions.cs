using System.Net.Http.Headers;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Diagnostics;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Media;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Publishing;
using GameLauncher.Core.Updates;
using GameLauncher.Infrastructure.Api;
using GameLauncher.Infrastructure.Authentication;
using GameLauncher.Infrastructure.Configuration;
using GameLauncher.Infrastructure.Downloads;
using GameLauncher.Infrastructure.Installs;
using GameLauncher.Infrastructure.Launching;
using GameLauncher.Infrastructure.Logging;
using GameLauncher.Infrastructure.Media;
using GameLauncher.Infrastructure.Platform;
using GameLauncher.Infrastructure.Publishing;
using GameLauncher.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncher.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>How long a request may take before it is reported as a network failure.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the launcher waits to open a connection before calling the host unreachable.
    /// Far shorter than <see cref="RequestTimeout"/> on purpose: a server that will answer is
    /// reached in milliseconds, so everything past a few seconds is a machine that is not
    /// there, and the whole cost of finding that out is paid by somebody watching a window.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Registers every infrastructure implementation behind its Core interface. The UI layer
    /// calls this and then knows nothing about which concrete type it received.
    /// </summary>
    public static IServiceCollection AddLauncherInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IPathProvider>(_ => new PathProvider());
        services.AddSingleton<IRuntimePlatform>(_ => new RuntimePlatform());
        services.AddSingleton<IDiskSpaceProbe, DiskSpaceProbe>();
        services.AddSingleton<IFileBrowser, FileBrowser>();
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

        // One shared answer to "is the server there?", consulted before a request is sent and
        // written to by every request that completes. Registered before the clients, because
        // every one of them is handed it.
        services.AddSingleton<IServerReachability>(provider =>
            new ServerReachability(provider.GetRequiredService<TimeProvider>()));
        services.AddTransient<ReachabilityHandler>();

        services.AddSingleton<ITokenStore, FileTokenStore>();

        // What the account owns, as last answered, so an offline library is the library
        // rather than the subset of it that happens to be installed here.
        services.AddSingleton<ILibraryCache, FileLibraryCache>();
        services.AddSingleton<IInstallStore, SqliteInstallStore>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();

        // Erasure sits here rather than on IAuthenticationService because the account route
        // runs on the authenticated client, whose handler depends on IAuthenticationService:
        // the container would refuse a graph where the session service needed it back.
        // Every client this launcher owns, given a bounded connection attempt. The default is
        // the operating system's, and on Windows a host that is simply not there costs about
        // twenty seconds of SYN retries — which is twenty seconds of a launcher that looks
        // frozen, once per request. Reaching a server that exists takes milliseconds on a LAN
        // and well under this over a bad mobile link, so nothing that would have succeeded is
        // given up on. It bounds the *connection* only: a download still takes as long as it
        // takes.
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(
            () => new SocketsHttpHandler { ConnectTimeout = ConnectTimeout }));

        services.AddSingleton<IAccountService, AccountService>();
        services.AddTransient<BearerTokenHandler>();

        // The auth client deliberately carries no bearer token: refreshing has to work exactly
        // when the access token has expired, and a handler that fetched one first would call
        // back into this very client.
        services.AddHttpClient<IAuthApi, AuthApiClient>(ConfigureClient)
            .AddHttpMessageHandler<ReachabilityHandler>();

        // Also tokenless, and for a related reason: the limits document is what a launcher
        // reads before it knows whether it can sign in at all, and the route needs no token.
        services.AddHttpClient<ICapabilitiesApi, CapabilitiesApiClient>(ConfigureClient)
            .AddHttpMessageHandler<ReachabilityHandler>();

        // Tokenless for a third reason, and the sharpest of them: the crashes worth having are
        // often the ones that happen before anybody has signed in.
        services.AddHttpClient<ICrashReportApi, CrashReportApiClient>(ConfigureClient)
            .AddHttpMessageHandler<ReachabilityHandler>();

        // And tokenless for the sharpest reason of the four: the launcher that most needs an
        // update is the one that cannot sign in — pointed at a server it has never reached, or
        // carrying the very bug the update fixes.
        services.AddHttpClient<ILauncherReleaseApi, LauncherReleaseApiClient>(ConfigureClient)
            .AddHttpMessageHandler<ReachabilityHandler>();
        services.AddSingleton<IServerCapabilityProvider, CachedServerCapabilityProvider>();

        // Authenticated, unlike the auth client beside it: erasing an account is something a
        // signed-in account does to itself, and the password in the body is a second proof
        // rather than the first one.
        services.AddHttpClient<IAccountApi, AccountApiClient>(ConfigureClient)
            .AddHttpMessageHandler<ReachabilityHandler>()
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddHttpClient<ICatalogApi, CatalogApiClient>(ConfigureClient)
            .AddHttpMessageHandler<ReachabilityHandler>()
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddHttpClient<ILibraryApi, LibraryApiClient>(ConfigureClient)
            .AddHttpMessageHandler<ReachabilityHandler>()
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddHttpClient<IDownloadApi, DownloadApiClient>(ConfigureClient)
            .AddHttpMessageHandler<ReachabilityHandler>()
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddHttpClient<IPublishingApi, PublishingApiClient>(ConfigureClient)
            .AddHttpMessageHandler<ReachabilityHandler>()
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

        // A fifth, and the same shape as the file server's: a launcher archive is tens of
        // megabytes served from a public URL on a host the API named, so no token and no clock
        // that starts at the first byte.
        services.AddHttpClient<ILauncherUpdateDownloader, LauncherUpdateDownloader>(
            ConfigureFileServerClient);

        // The three things a check needs from outside its own code path. The version is this
        // assembly's; the channel is the packager's; the key is compiled in, because the file
        // an update overwrites must not be the file that authorizes it.
        services.AddSingleton(provider => new UpdateSettings
        {
            CurrentVersion = ThisAssemblyVersion(),
            Channel = ReleaseTargets.Channel(
                provider.GetRequiredService<LauncherConfiguration>().Updates.Channel),
            PublicKeyBase64 = LauncherReleaseKey.PublicKeyBase64,
        });

        services.AddSingleton<IUpdateChecker, UpdateChecker>();

        // Unpacking is the launcher's job rather than the updater's, so the rules that make it
        // safe (D24, through UpdateArchiveRules) stay in one place and the helper that runs when
        // nothing can be fixed any more stays small.
        services.AddSingleton<IUpdateInstaller, UpdateInstaller>();

        services.AddSingleton<ICrashReportUploader, CrashReportUploader>();
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
