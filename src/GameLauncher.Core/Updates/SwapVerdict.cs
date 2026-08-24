namespace GameLauncher.Core.Updates;

/// <summary>What the updater concluded about the launcher it started.</summary>
public enum SwapVerdict
{
    /// <summary>Keep the new installation and discard the old one.</summary>
    Succeeded,

    /// <summary>Put the old installation back and start it again.</summary>
    Restore,
}
