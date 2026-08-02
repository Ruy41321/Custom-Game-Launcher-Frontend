using GameLauncher.Core.Configuration;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Platform;
using GameLauncher.Infrastructure.Configuration;
using GameLauncher.Infrastructure.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncher.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every infrastructure implementation behind its Core interface. The UI layer
    /// calls this and then knows nothing about which concrete type it received.
    /// </summary>
    public static IServiceCollection AddLauncherInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IPathProvider>(_ => new PathProvider());
        services.AddSingleton<ILauncherConfigurationProvider, LauncherConfigurationProvider>();
        services.AddSingleton<IUserSettingsStore, JsonUserSettingsStore>();
        services.AddSingleton<ILocalizationService>(_ => new ResourceManagerLocalizationService());

        return services;
    }
}
