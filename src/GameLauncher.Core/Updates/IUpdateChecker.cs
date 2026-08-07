namespace GameLauncher.Core.Updates;

/// <summary>
/// Asks whether a newer launcher has been published, and answers without ever being able to
/// stop the launcher from starting.
/// </summary>
public interface IUpdateChecker
{
    /// <summary>
    /// Never throws for anything but cancellation. A server that is unreachable, a 404, a body
    /// that is not JSON, a signature that does not verify and a clock that is wrong all leave
    /// as <see cref="UpdateAvailability.Undetermined"/> with a line in the log.
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

public enum UpdateAvailability
{
    /// <summary>
    /// No usable key is compiled into this build, so nothing was asked. A fork that has not
    /// set up signing checks for no updates rather than trusting whoever answers.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// The server answered and there is nothing strictly newer than what is running. A 404 is
    /// read as this too: no key configured there, nothing published, or nothing for this
    /// platform are one situation from here.
    /// </summary>
    UpToDate,

    /// <summary>
    /// The check did not complete, or completed and was refused. Deliberately not an error the
    /// user is shown: a launcher that would not open because it could not reach the update
    /// route would be the worst possible outcome of this feature.
    /// </summary>
    Undetermined,

    Available,
}

public sealed record UpdateCheckResult
{
    public static UpdateCheckResult NotConfigured { get; } =
        new() { Availability = UpdateAvailability.NotConfigured };

    public static UpdateCheckResult UpToDate { get; } =
        new() { Availability = UpdateAvailability.UpToDate };

    public static UpdateCheckResult Undetermined { get; } =
        new() { Availability = UpdateAvailability.Undetermined };

    public UpdateAvailability Availability { get; init; }

    /// <summary>The verified document, present only when an update is available.</summary>
    public ReleaseDocument? Release { get; init; }

    /// <summary>Where the artifact is served. Empty unless an update is available.</summary>
    public string ArtifactUrl { get; init; } = string.Empty;

    public bool IsAvailable =>
        Availability == UpdateAvailability.Available && Release is not null;

    public static UpdateCheckResult Available(ReleaseDocument release, string artifactUrl) =>
        new()
        {
            Availability = UpdateAvailability.Available,
            Release = release,
            ArtifactUrl = artifactUrl,
        };
}
