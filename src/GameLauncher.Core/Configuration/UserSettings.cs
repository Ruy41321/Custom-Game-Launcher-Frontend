namespace GameLauncher.Core.Configuration;

/// <summary>
/// Preferences the user can change, persisted under the platform's app-data directory.
/// Kept apart from <see cref="LauncherConfiguration"/> so replacing the shipped config
/// during a self-update never overwrites them.
/// </summary>
public sealed record UserSettings
{
    /// <summary>Null follows <see cref="LauncherConfiguration"/>, then the OS language.</summary>
    public string? Language { get; init; }

    /// <summary>Null follows the theme from <see cref="LauncherConfiguration"/>.</summary>
    public string? ThemeVariant { get; init; }

    /// <summary>Null uses the platform default install location.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>Opt-in: crash reports are never uploaded unless this is explicitly true.</summary>
    public bool SendCrashReports { get; init; }

    public bool LaunchMinimized { get; init; }
}

public interface IUserSettingsStore
{
    Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);
}

public interface ILauncherConfigurationProvider
{
    /// <summary>
    /// Reads <c>launcher.config.json</c>. A missing file yields the defaults; a malformed or
    /// invalid one throws, because running with half-applied branding is worse than a clear
    /// startup failure.
    /// </summary>
    Task<LauncherConfiguration> LoadAsync(CancellationToken cancellationToken = default);
}
