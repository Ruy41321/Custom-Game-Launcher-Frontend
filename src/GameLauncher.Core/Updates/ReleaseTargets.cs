using GameLauncher.Core.Models;

namespace GameLauncher.Core.Updates;

/// <summary>
/// The vocabulary the release route speaks: the channel, platform and architecture names, in
/// exactly the spellings the server parses.
/// </summary>
public static class ReleaseTargets
{
    /// <summary>The stream everybody is on unless a packager said otherwise.</summary>
    public const string StableChannel = "stable";

    public const string BetaChannel = "beta";

    /// <summary>
    /// Reads a configured channel, and reads anything it does not know as <c>stable</c>.
    ///
    /// This is the one place a launcher deliberately does not fail hard on a bad configuration
    /// value. <c>apiBaseUrl</c> is refused at start-up because a launcher pointed at nothing is
    /// useless anyway; a channel typo would instead turn a launcher that works perfectly well
    /// into one that will not open — and the server answers 422 to a channel it does not know,
    /// so sending the typo on would spend a request to be told no on every start.
    /// </summary>
    public static string Channel(string? configured) =>
        string.Equals(configured, BetaChannel, StringComparison.OrdinalIgnoreCase)
            ? BetaChannel
            : StableChannel;

    public static string NameOf(GamePlatform platform) => platform switch
    {
        GamePlatform.Linux => "linux",
        GamePlatform.MacOs => "macos",
        _ => "windows",
    };

    public static string NameOf(BuildArchitecture architecture) =>
        architecture == BuildArchitecture.Arm64 ? "arm64" : "x64";
}
