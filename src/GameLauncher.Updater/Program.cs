using GameLauncher.Core.Updates;

namespace GameLauncher.Updater;

/// <summary>
/// Standalone helper that replaces the launcher's own files.
///
/// It has to be a separate process: on Windows a running executable cannot overwrite its own
/// binaries (D7). The launcher downloads and verifies the new version, unpacks it, copies this
/// helper out of the directory it is about to replace, starts it and exits; this waits for that
/// exit, moves the old installation aside, puts the new one in place, starts it, and watches it
/// for about thirty seconds.
///
/// <b>The decision is a pure function of (exit code, elapsed time)</b> — see
/// <see cref="RelaunchWatch"/>. A non-zero exit inside the window puts the old installation
/// back and starts it again; still running at the end of the window, or an exit with zero, is a
/// success and the old installation goes. There is no marker file and no IPC, because the one
/// thing that must work when nothing else can be fixed is the thing least able to afford a
/// protocol.
///
/// The declared hole: a launcher that starts, survives thirty seconds and then crashes is not
/// rolled back. That is what the crash reports are for, and <c>--rollback</c> below is the
/// manual way out for as long as the old installation is still on disk.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
        }

        UpdateSwapRequest? request = UpdateSwapRequest.TryParse(args, out string? error);
        if (request is null)
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return ExitCodes.Usage;
        }

        SwapRunner runner = new(
            new SystemProcessStarter(),
            new SystemProcessWaiter(),
            TimeProvider.System,
            Console.Out);

        return runner.Run(request);
    }

    private static void PrintUsage() =>
        Console.WriteLine(
            """
            GameLauncher.Updater

            Usage:
              GameLauncher.Updater --source <dir> --target <dir> [--wait-for-pid <pid>] [--relaunch <exe>]
              GameLauncher.Updater --rollback --target <dir> [--wait-for-pid <pid>] [--relaunch <exe>]

            Options:
              --source        Directory holding the already downloaded, verified and unpacked
                              new version
              --target        Installation directory to replace in place. Its previous contents
                              are renamed to <target>.previous rather than deleted, and only go
                              away once the new launcher has started
              --wait-for-pid  Process id of the launcher to outlive before touching any file
              --relaunch      Executable to start once the new version is in place. It is then
                              watched for about thirty seconds: a non-zero exit inside that
                              window restores <target>.previous and starts it again
              --rollback      Put <target>.previous back and change nothing else. Run this
                              from a copy of the helper outside <target>: restoring deletes
                              <target> first, which cannot be done while an executable inside
                              it is running

            Exit codes:
              0  the new version is in place
              2  refused: the command line, or what it named. Nothing was changed
              4  the new launcher failed and the previous installation was restored
              5  the swap failed and the previous installation could not be put back
            """);
}
