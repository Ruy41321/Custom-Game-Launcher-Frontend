namespace GameLauncher.Core.Downloads;

/// <summary>
/// Where an installation has got to. The phases are what the user is told, so they are the
/// steps that take visibly different amounts of time — not the internal ones.
/// </summary>
public enum InstallPhase
{
    /// <summary>Asking the server what has to happen.</summary>
    Planning,

    /// <summary>Comparing what it will cost against the free space, before anything is written.</summary>
    CheckingSpace,

    Downloading,

    /// <summary>Moving files into the install directory and deleting what the build dropped.</summary>
    Applying,

    /// <summary>Checking the result against the manifest.</summary>
    Verifying,

    Done,
}

/// <summary>
/// One progress report. Bytes, not percentages: the caller decides how to present it, and a
/// speed or an estimate needs a clock this has no business owning.
/// </summary>
public sealed record DownloadProgress
{
    public InstallPhase Phase { get; init; }

    /// <summary>Bytes accounted for, including those a resumed transfer found already on disk.</summary>
    public long TransferredBytes { get; init; }

    /// <summary>What the plan said the transfer would cost. Zero when nothing has to travel.</summary>
    public long TotalBytes { get; init; }

    public int FilesApplied { get; init; }

    public int TotalFiles { get; init; }

    /// <summary>
    /// Between 0 and 1, and clamped: a resumed transfer that the server then answers in full
    /// can report more than it promised, and a bar that overshoots looks broken.
    /// </summary>
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)TransferredBytes / TotalBytes, 0, 1)
        : Phase == InstallPhase.Done ? 1 : 0;
}
