using System.Globalization;

namespace GameLauncher.Core.Updates;

/// <summary>
/// The updater's command line, parsed and refused in one place, and built in that same place by
/// the launcher that calls it. Two sides of one contract with one definition, so a flag renamed
/// here cannot be spelled the old way there — the shape D19 keeps for the manifest, applied to
/// an argument list.
///
/// Nothing here touches the filesystem: Core does no I/O anywhere, and whether
/// <see cref="TargetDirectory"/> exists is a question for the process that is about to move it.
/// </summary>
public sealed record UpdateSwapRequest
{
    /// <summary>Directory holding the new version, already extracted and checked.</summary>
    public required string SourceDirectory { get; init; }

    /// <summary>The installation to replace.</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>The launcher to outlive before touching anything. Zero when there is none.</summary>
    public int WaitForProcessId { get; init; }

    /// <summary>What to start once the new version is in place. Null means start nothing.</summary>
    public string? RelaunchExecutable { get; init; }

    /// <summary>
    /// Put the previous installation back and change nothing else. The manual way out for as
    /// long as it is still on disk, and the reason it is renamed rather than deleted.
    /// </summary>
    public bool RollbackOnly { get; init; }

    /// <summary>
    /// The arguments that parse back into this request. Round-tripped by a test rather than
    /// trusted, because the one caller and the one parser are in different processes and only
    /// meet on a user's machine.
    /// </summary>
    public IReadOnlyList<string> ToArguments()
    {
        List<string> arguments = [];

        if (RollbackOnly)
        {
            arguments.Add("--rollback");
        }
        else
        {
            arguments.Add("--source");
            arguments.Add(SourceDirectory);
        }

        arguments.Add("--target");
        arguments.Add(TargetDirectory);

        if (WaitForProcessId != 0)
        {
            arguments.Add("--wait-for-pid");
            arguments.Add(WaitForProcessId.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(RelaunchExecutable))
        {
            arguments.Add("--relaunch");
            arguments.Add(RelaunchExecutable);
        }

        return arguments;
    }

    /// <summary>
    /// Null when <paramref name="arguments"/> do not describe a swap this helper will perform,
    /// with <paramref name="error"/> saying which part is missing. An incomplete command line is
    /// refused before a single file is looked at, let alone moved.
    /// </summary>
    public static UpdateSwapRequest? TryParse(IReadOnlyList<string> arguments, out string? error)
    {
        string? source = null;
        string? target = null;
        string? relaunch = null;
        int waitForPid = 0;
        bool rollback = false;

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case "--rollback":
                    rollback = true;
                    continue;

                case "--source":
                case "--target":
                case "--relaunch":
                case "--wait-for-pid":
                    if (index + 1 >= arguments.Count)
                    {
                        error = $"{argument} needs a value.";
                        return null;
                    }

                    string value = arguments[++index];
                    switch (argument)
                    {
                        case "--source":
                            source = value;
                            break;
                        case "--target":
                            target = value;
                            break;
                        case "--relaunch":
                            relaunch = value;
                            break;
                        default:
                            if (!int.TryParse(
                                    value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                    out waitForPid) || waitForPid < 0)
                            {
                                error = $"--wait-for-pid is not a process id: {value}";
                                return null;
                            }

                            break;
                    }

                    continue;

                default:
                    error = $"Unrecognised argument: {argument}";
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            error = "--target is required.";
            return null;
        }

        if (!rollback && string.IsNullOrWhiteSpace(source))
        {
            error = "--source is required unless --rollback is given.";
            return null;
        }

        if (rollback && !string.IsNullOrWhiteSpace(source))
        {
            // Two different jobs, and doing the wrong one is the expensive mistake here.
            error = "--rollback puts the previous installation back and takes no --source.";
            return null;
        }

        error = null;
        return new UpdateSwapRequest
        {
            SourceDirectory = source ?? string.Empty,
            TargetDirectory = target,
            WaitForProcessId = waitForPid,
            RelaunchExecutable = relaunch,
            RollbackOnly = rollback,
        };
    }
}
