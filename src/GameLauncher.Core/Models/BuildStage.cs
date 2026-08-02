namespace GameLauncher.Core.Models;

/// <summary>
/// Publisher-set release stage. <see cref="Release"/> carries no badge in the UI; the others
/// are shown as Demo / Alpha / Beta next to the semantic version.
/// </summary>
public enum BuildStage
{
    Demo,
    Alpha,
    Beta,
    Release,
}

/// <summary>Platforms a build can target. Mirrors the server's <c>build_platform</c> enum.</summary>
public enum GamePlatform
{
    Windows,
    Linux,
    MacOs,
}

/// <summary>Mirrors the server's <c>build_architecture</c> enum.</summary>
public enum BuildArchitecture
{
    X64,
    Arm64,
}
