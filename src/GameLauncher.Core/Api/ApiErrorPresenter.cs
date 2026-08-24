using System.Globalization;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Core.Api;

/// <summary>
/// Turns a failure into a sentence a person can read. It exists so that no view model ever
/// builds an error string of its own: the mapping from a failure to what the user is told
/// lives here, once, and is translated like everything else.
/// </summary>
public interface IApiErrorPresenter
{
    /// <summary>
    /// <paramref name="unauthenticatedKey"/> lets a screen that authenticates say something
    /// truer than "your session expired" — on the sign-in form the same 401 means the
    /// password was wrong.
    /// </summary>
    string Describe(Exception exception, string? unauthenticatedKey = null);
}

public sealed class ApiErrorPresenter(
    ILocalizationService localization,
    ILogger<ApiErrorPresenter> logger) : IApiErrorPresenter
{
    public string Describe(Exception exception, string? unauthenticatedKey = null)
    {
        // Two failures that never involve the server at all, and that a user can act on —
        // which is exactly why they are not allowed to fall through to "something went wrong".
        if (exception is InsufficientDiskSpaceException space)
        {
            return localization.Translate(
                "Error.NotEnoughSpace",
                ByteSize.Format(space.RequiredBytes, CultureInfo.CurrentCulture),
                ByteSize.Format(space.AvailableBytes, CultureInfo.CurrentCulture),
                space.Path);
        }

        if (exception is GameLaunchException launch)
        {
            return localization.Translate("Launch." + launch.Reason);
        }

        if (exception is OperationCanceledException)
        {
            return localization.Translate("Error.Cancelled");
        }

        if (exception is not ApiException apiException)
        {
            return localization.Translate("Error.Generic");
        }

        // A validation failure is the one refusal the person reading can fix themselves, and
        // the only one where the server says which rule refused. It is also the one that leaves
        // nothing to correlate: the server did exactly what it should have, so it is not logged
        // below either.
        if (apiException.Code == ApiErrorCode.InvalidInput)
        {
            return DescribeValidation(apiException);
        }

        // The request id is what turns "it did not work" into something the server operator can
        // look up, and that need is real — so it goes to this launcher's log rather than into
        // the sentence somebody reads (D67). A UUID in a message is a string nobody can act on,
        // in front of the one person on the machine who cannot use it.
        if (!string.IsNullOrEmpty(apiException.RequestId))
        {
            logger.LogWarning(
                "The server refused a request: {Code}, status {Status}, reference {RequestId}.",
                apiException.Code,
                apiException.StatusCode,
                apiException.RequestId);
        }

        return localization.Translate(KeyFor(apiException, unauthenticatedKey));
    }

    private string DescribeValidation(ApiException exception)
    {
        // An unknown rule is a server newer than this launcher, and the generic sentence is
        // what it used to say for every one of them. Adding a rule server-side therefore never
        // breaks a client; it only stops improving the message for one it has not learned.
        if (ValidationKeyFor(exception.Rule) is not { } key)
        {
            return localization.Translate("Error.InvalidInput");
        }

        string sentence = exception.RuleArgs.Count == 0
            ? localization.Translate(key)
            : localization.Translate(key, [.. exception.RuleArgs]);

        // A rule that arrived without the limit its wording needs is a server that changed its
        // mind about that rule's arguments, and an unfilled placeholder on screen is a
        // launcher that looks broken. The generic sentence is worse advice and better manners.
        return sentence.Contains("{0}", StringComparison.Ordinal)
            ? localization.Translate("Error.InvalidInput")
            : sentence;
    }

    /// <summary>
    /// The rules this launcher can say something specific about. The names are the backend's
    /// <c>domain/ValidationRules.h</c>, which is a frozen contract precisely so that this
    /// mapping can exist; the alternative was matching on the server's English
    /// <c>detail</c>, which turns rewording a message into a broken client.
    /// </summary>
    private static string? ValidationKeyFor(string? rule) => rule switch
    {
        "email_required" => "Error.Validation.EmailRequired",
        "email_too_long" => "Error.Validation.EmailTooLong",
        "email_invalid" => "Error.Validation.EmailInvalid",
        "password_required" => "Error.Validation.PasswordRequired",
        "password_too_short" => "Error.Validation.PasswordTooShort",
        "password_too_long" => "Error.Validation.PasswordTooLong",
        "password_blank" => "Error.Validation.PasswordBlank",
        "password_unchanged" => "Error.Validation.PasswordUnchanged",
        "display_name_required" => "Error.Validation.DisplayNameRequired",
        "display_name_too_short" => "Error.Validation.DisplayNameTooShort",
        "display_name_too_long" => "Error.Validation.DisplayNameTooLong",
        "display_name_invalid" => "Error.Validation.DisplayNameInvalid",
        "erasure_reason_too_long" => "Error.Validation.ErasureReasonTooLong",
        "title_required" => "Error.Validation.TitleRequired",
        "title_too_long" => "Error.Validation.TitleTooLong",
        "summary_too_long" => "Error.Validation.SummaryTooLong",
        "description_too_long" => "Error.Validation.DescriptionTooLong",
        "slug_required" => "Error.Validation.SlugRequired",
        "slug_too_long" => "Error.Validation.SlugTooLong",
        "slug_invalid" => "Error.Validation.SlugInvalid",
        "release_date_invalid" => "Error.Validation.ReleaseDateInvalid",
        "version_required" => "Error.Validation.VersionRequired",
        "version_invalid" => "Error.Validation.VersionInvalid",
        "version_too_large" => "Error.Validation.VersionTooLarge",
        "release_notes_too_long" => "Error.Validation.ReleaseNotesTooLong",
        "build_name_too_long" => "Error.Validation.BuildNameTooLong",
        "patch_note_title_required" => "Error.Validation.PatchNoteTitleRequired",
        "patch_note_title_too_long" => "Error.Validation.PatchNoteTitleTooLong",
        "patch_note_body_too_long" => "Error.Validation.PatchNoteBodyTooLong",
        "alt_text_too_long" => "Error.Validation.AltTextTooLong",
        "alt_text_invalid" => "Error.Validation.AltTextInvalid",
        _ => null,
    };

    private static string KeyFor(ApiException exception, string? unauthenticatedKey) =>
        exception.Code switch
        {
            ApiErrorCode.Network => "Error.Network",
            ApiErrorCode.Integrity => "Error.Integrity",
            ApiErrorCode.LinkExpired => "Error.LinkExpired",
            ApiErrorCode.InvalidInput => "Error.InvalidInput",
            ApiErrorCode.Unauthenticated => unauthenticatedKey ?? "Error.Unauthenticated",
            ApiErrorCode.Forbidden => "Error.Forbidden",
            ApiErrorCode.NotFound => "Error.NotFound",
            ApiErrorCode.Conflict => "Error.Conflict",
            ApiErrorCode.QuotaExceeded => "Error.QuotaExceeded",
            ApiErrorCode.RateLimited => "Error.RateLimited",
            ApiErrorCode.DependencyFailure => "Error.DependencyFailure",
            _ => "Error.Generic",
        };
}
