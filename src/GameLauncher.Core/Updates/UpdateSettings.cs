namespace GameLauncher.Core.Updates;

/// <summary>
/// The three things a check needs that come from outside the code path: what is running, which
/// stream this launcher is on, and the key it trusts.
///
/// They arrive together as one value rather than as three constructor parameters so that a
/// test states all three at once, and so that the composition root is the only place that
/// knows where each of them really comes from — the assembly version, the shipped
/// configuration, and <see cref="LauncherReleaseKey"/>.
/// </summary>
public sealed record UpdateSettings
{
    /// <summary>What this launcher is, as it would be written in a release document.</summary>
    public string CurrentVersion { get; init; } = "0.0.0";

    /// <summary>Already normalised by <see cref="ReleaseTargets.Channel"/>.</summary>
    public string Channel { get; init; } = ReleaseTargets.StableChannel;

    public string PublicKeyBase64 { get; init; } = string.Empty;
}
