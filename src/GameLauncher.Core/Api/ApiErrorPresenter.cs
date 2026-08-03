using System.Globalization;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;

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

public sealed class ApiErrorPresenter(ILocalizationService localization) : IApiErrorPresenter
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

        string message = localization.Translate(KeyFor(apiException, unauthenticatedKey));

        // The request id is what turns "it did not work" into something the server operator
        // can actually look up, so it is shown whenever the server sent one.
        return string.IsNullOrEmpty(apiException.RequestId)
            ? message
            : localization.Translate("Error.WithReference", message, apiException.RequestId);
    }

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
