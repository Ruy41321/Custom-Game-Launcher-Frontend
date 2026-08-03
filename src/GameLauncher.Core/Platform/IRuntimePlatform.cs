using GameLauncher.Core.Models;

namespace GameLauncher.Core.Platform;

/// <summary>
/// What this machine is, in the server's vocabulary. Kept behind an interface because a test
/// has to be able to say "pretend this is an arm64 Mac" without running on one.
/// </summary>
public interface IRuntimePlatform
{
    GamePlatform Platform { get; }

    BuildArchitecture Architecture { get; }
}
