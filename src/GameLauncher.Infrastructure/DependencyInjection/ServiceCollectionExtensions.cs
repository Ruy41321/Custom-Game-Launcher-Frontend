using System.Net.Http.Headers;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Platform;
using GameLauncher.Infrastructure.Api;
using GameLauncher.Infrastructure.Authentication;
using GameLauncher.Infrastructure.Configuration;
using GameLauncher.Infrastructure.Platform;
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
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddTransient<BearerTokenHandler>();

        // The auth client deliberately carries no bearer token: refreshing has to work exactly
        // when the access token has expired, and a handler that fetched one first would call
        // back into this very client.
        services.AddHttpClient<IAuthApi, AuthApiClient>(ConfigureClient);

        services.AddHttpClient<ICatalogApi, CatalogApiClient>(ConfigureClient)
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddHttpClient<ILibraryApi, LibraryApiClient>(ConfigureClient)
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddHttpClient<IDownloadApi, DownloadApiClient>(ConfigureClient)
            .AddHttpMessageHandler<BearerTokenHandler>();

        return services;
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
