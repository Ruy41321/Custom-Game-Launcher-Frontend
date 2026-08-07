namespace GameLauncher.Updater;

/// <summary>
/// Standalone helper that replaces the launcher's own files.
///
/// It has to be a separate process: on Windows a running executable cannot overwrite its
/// own binaries. The launcher downloads and verifies the new version, starts this helper,
/// and exits; the helper waits for that exit, swaps the files and starts the launcher again.
///
/// The swap is <b>not implemented</b>. This entry point exists so the process boundary is part
/// of the architecture from the start rather than retrofitted later, and the command line below
/// is the one the launcher will call — but nothing here moves a file yet.
///
/// The other two thirds are done. The server publishes signed releases, and as of 2026-08-07
/// the launcher checks for them: it verifies the signature over the document as it arrived,
/// refuses anything that is not strictly newer, and downloads an artifact only if its bytes hash
/// to the content address inside that document. See <c>Documentation/self-update.md</c> in this
/// repository, and <c>Documentation/launcher-releases.md</c> in the backend's.
///
/// What is missing is the swap below. Until it exists, a verified archive waits under
/// <c>&lt;user data&gt;/updates/&lt;version&gt;/</c> and the launcher says so rather than
/// claiming it is about to install it.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitNotImplemented = 3;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        // Says what is true. Until 2026-08-07 this line claimed the swap was "planned for
        // milestone 8" — a milestone that had been closed for days with nothing implemented,
        // which is the kind of comment that costs somebody an afternoon before they believe the
        // code over it.
        Console.Error.WriteLine(
            "The self-update swap is not implemented: this helper does not move any files yet. "
            + "The launcher already checks for a signed release and downloads a verified "
            + "archive; replacing the installation with it is what is missing.");
        return ExitNotImplemented;
    }

    private static void PrintUsage() =>
        Console.WriteLine(
            """
            GameLauncher.Updater

            Usage:
              GameLauncher.Updater --source <dir> --target <dir> --wait-for-pid <pid> [--relaunch <exe>]

            Options:
              --source        Directory holding the already downloaded and verified new version
              --target        Installation directory to update in place
              --wait-for-pid  Process id of the launcher to wait for before swapping files
              --relaunch      Executable to start once the swap has completed

            Not implemented: this helper currently swaps nothing and exits with code 3.
            """);
}
