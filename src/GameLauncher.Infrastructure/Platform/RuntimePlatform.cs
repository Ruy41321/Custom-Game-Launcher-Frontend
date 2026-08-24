using System.Runtime.InteropServices;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;

namespace GameLauncher.Infrastructure.Platform;

/// <summary>
/// Reports the running platform in the terms the server's build table uses. Anything the
/// launcher does not have a name for is reported as x64, which is the architecture every
/// desktop target can execute — under emulation if need be.
/// </summary>
public sealed class RuntimePlatform : IRuntimePlatform
{
    public GamePlatform Platform { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? GamePlatform.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? GamePlatform.MacOs
        : GamePlatform.Linux;

    public BuildArchitecture Architecture { get; } =
        RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? BuildArchitecture.Arm64
            : BuildArchitecture.X64;
}
