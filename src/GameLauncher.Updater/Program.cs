namespace GameLauncher.Updater;

/// <summary>
/// Standalone helper that replaces the launcher's own files.
///
/// It has to be a separate process: on Windows a running executable cannot overwrite its
/// own binaries. The launcher downloads and verifies the new version, starts this helper,
/// and exits; the helper waits for that exit, swaps the files and starts the launcher again.
///
/// Milestone 8 implements the swap. This entry point exists now so the process boundary is
/// part of the architecture from the start rather than retrofitted later.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        Console.Error.WriteLine(
            "The self-update mechanism is not implemented yet (planned for milestone 8).");
        return ExitUsage;
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
            """);
}
