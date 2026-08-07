using System.Text;
using GameLauncher.Core.Api;
using GameLauncher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Core.Updates;

/// <summary>
/// The five rules the server's <c>Documentation/launcher-releases.md</c> asks a client to hold,
/// in the order they are applied:
///
/// <list type="number">
/// <item>the signature is checked over the bytes <b>as they arrived</b>, before anything is
/// parsed — a document that is not the one that was published must never become the one that
/// gets installed;</item>
/// <item>anything not <b>strictly newer</b> than what is running is refused, which is the only
/// defence against a correctly signed <i>old</i> document being replayed;</item>
/// <item>the artifact is refused unless its bytes hash to the content address inside the signed
/// document — that half lives in <see cref="ILauncherUpdateDownloader"/>;</item>
/// <item>a failed check never stops the launcher from starting;</item>
/// <item>the key is the one compiled into the binary, and an absent one means no check happens
/// at all.</item>
/// </list>
///
/// There is deliberately <b>no minimum-version enforcement</b>: a server that can tell a
/// launcher it is too old to talk to is a remote kill switch, and one row would brick every
/// installation. Nothing here reads such a field and none is reserved.
/// </summary>
public sealed class UpdateChecker(
    ILauncherReleaseApi releases,
    IRuntimePlatform runtime,
    UpdateSettings settings,
    ILogger<UpdateChecker> logger) : IUpdateChecker
{
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!ReleaseSignature.IsUsableKey(settings.PublicKeyBase64))
        {
            // Not a failure and not worth a warning on every start: it is what a build that has
            // not been given a signing key is supposed to do.
            logger.LogDebug("No usable release signing key is built in; not checking for updates.");
            return UpdateCheckResult.NotConfigured;
        }

        if (!ReleaseVersion.TryParse(settings.CurrentVersion, out ReleaseVersion current))
        {
            // Nothing can be called newer than a version nobody can read, and guessing would
            // mean accepting whatever arrived.
            logger.LogWarning(
                "This launcher's own version '{Version}' cannot be compared; not checking for updates.",
                settings.CurrentVersion);
            return UpdateCheckResult.Undetermined;
        }

        try
        {
            return await CheckCoreAsync(current, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException exception) when (exception.Code == ApiErrorCode.NotFound)
        {
            // No key configured on that server, nothing published at all, or nothing for this
            // platform. From here they are one situation, and the server answers 404 to all
            // three on purpose so that a stranger cannot learn which platforms it builds for.
            logger.LogDebug("This server has no launcher release for {Platform}/{Arch}.",
                ReleaseTargets.NameOf(runtime.Platform), ReleaseTargets.NameOf(runtime.Architecture));
            return UpdateCheckResult.UpToDate;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Rule four, and the reason this catch is this wide: a launcher that will not open
            // because it could not reach the update route would be the worst possible outcome
            // of this feature. It is D50's reasoning about the crash uploader, unchanged.
            logger.LogWarning(exception, "Checking for a launcher update failed.");
            return UpdateCheckResult.Undetermined;
        }
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(
        ReleaseVersion current, CancellationToken cancellationToken)
    {
        string channel = settings.Channel;
        string platform = ReleaseTargets.NameOf(runtime.Platform);
        string arch = ReleaseTargets.NameOf(runtime.Architecture);

        LauncherReleaseResponse response = await releases
            .GetLatestAsync(channel, platform, arch, cancellationToken)
            .ConfigureAwait(false);

        // The bytes the signature covers, exactly as they arrived: the route serves the
        // document as an opaque string, so this is a decode and never a re-serialisation.
        byte[] document = Encoding.UTF8.GetBytes(response.Document);

        if (!ReleaseSignature.Verify(document, response.Signature, settings.PublicKeyBase64))
        {
            logger.LogWarning(
                "A launcher release was offered with a signature this launcher does not accept.");
            return UpdateCheckResult.Undetermined;
        }

        if (!ReleaseDocument.TryParse(document, out ReleaseDocument? release, out string problem))
        {
            // Signed by the right key and still unreadable: a server running a schema this
            // launcher predates, or a document nobody validated. Either way there is nothing
            // safe to install.
            logger.LogWarning("A signed launcher release could not be read: {Problem}", problem);
            return UpdateCheckResult.Undetermined;
        }

        if (!release.Describes(channel, platform, arch))
        {
            // The signature vouches for what the document *says*, so this is where that pays
            // off: a server holding real signed releases cannot hand a Windows launcher the
            // Linux one, or a stable launcher a beta.
            logger.LogWarning(
                "A launcher release for {Channel}/{Platform}/{Arch} was offered to {Wanted}.",
                release.Channel, release.Platform, release.Arch, $"{channel}/{platform}/{arch}");
            return UpdateCheckResult.Undetermined;
        }

        if (!release.Version.IsNewerThan(current))
        {
            return UpdateCheckResult.UpToDate;
        }

        if (!IsFetchable(response.Url))
        {
            logger.LogWarning(
                "Launcher release {Version} names an artifact URL this launcher will not fetch.",
                release.Version);
            return UpdateCheckResult.Undetermined;
        }

        logger.LogInformation(
            "Launcher version {Version} is available (this one is {Current}).",
            release.Version, current);

        return UpdateCheckResult.Available(release, response.Url);
    }

    /// <summary>
    /// http and https only, the same refusal <c>CachingImageLoader</c> applies to an artwork
    /// URL: the host is named by the server, so what schemes this client is willing to speak to
    /// it is this client's decision.
    /// </summary>
    private static bool IsFetchable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
}
