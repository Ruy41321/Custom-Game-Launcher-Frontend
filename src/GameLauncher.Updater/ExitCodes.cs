namespace GameLauncher.Updater;

/// <summary>
/// What this helper tells whoever is watching. They are distinct because the three failures
/// mean three different things to somebody reading a log after the fact: nothing happened, the
/// old launcher is back, or the installation needs a hand.
/// </summary>
public static class ExitCodes
{
    /// <summary>The new version is in place and the old one is gone.</summary>
    public const int Ok = 0;

    /// <summary>The command line, or what it named, was refused. Nothing was touched.</summary>
    public const int Usage = 2;

    /// <summary>
    /// The new launcher failed inside the watch window, and the previous installation was put
    /// back and started again.
    /// </summary>
    public const int Restored = 4;

    /// <summary>
    /// The swap failed and so did putting the old installation back. This is the only outcome
    /// that leaves somebody with work to do, which is why it does not share a code with the one
    /// above.
    /// </summary>
    public const int Broken = 5;
}
