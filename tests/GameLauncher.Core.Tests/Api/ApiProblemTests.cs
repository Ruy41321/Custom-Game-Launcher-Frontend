using GameLauncher.Core.Api;

namespace GameLauncher.Core.Tests.Api;

public sealed class ApiProblemTests
{
    [Theory]
    [InlineData("invalid_input", ApiErrorCode.InvalidInput)]
    [InlineData("unauthenticated", ApiErrorCode.Unauthenticated)]
    [InlineData("forbidden", ApiErrorCode.Forbidden)]
    [InlineData("password_change_required", ApiErrorCode.PasswordChangeRequired)]
    [InlineData("not_found", ApiErrorCode.NotFound)]
    [InlineData("conflict", ApiErrorCode.Conflict)]
    [InlineData("quota_exceeded", ApiErrorCode.QuotaExceeded)]
    [InlineData("rate_limited", ApiErrorCode.RateLimited)]
    [InlineData("dependency_failure", ApiErrorCode.DependencyFailure)]
    [InlineData("internal", ApiErrorCode.Internal)]
    public void EveryCodeTheServerCanSendIsUnderstood(string code, ApiErrorCode expected)
    {
        Assert.Equal(expected, new ApiProblem { Code = code }.ToErrorCode(500));
    }

    // A 403 whose body names the category, told apart from a plain refusal by the code and
    // never by its prose: the shell sends somebody to the screen that ends it.
    [Fact]
    public void AForcedPasswordChangeIsNotAnOrdinaryRefusal()
    {
        ApiProblem problem = new() { Code = "password_change_required", Status = 403 };

        Assert.Equal(ApiErrorCode.PasswordChangeRequired, problem.ToErrorCode(403));
        Assert.NotEqual(ApiErrorCode.Forbidden, problem.ToErrorCode(403));
    }

    // A reverse proxy or a crashed worker answers without the envelope; the status is then
    // the only thing left to reason from.
    [Theory]
    [InlineData(401, ApiErrorCode.Unauthenticated)]
    [InlineData(404, ApiErrorCode.NotFound)]
    [InlineData(429, ApiErrorCode.RateLimited)]
    [InlineData(502, ApiErrorCode.Internal)]
    public void AResponseWithoutACodeFallsBackToTheStatus(int status, ApiErrorCode expected)
    {
        Assert.Equal(expected, new ApiProblem().ToErrorCode(status));
    }

    [Fact]
    public void AnUnrecognisedCodeDoesNotMasqueradeAsSomethingElse()
    {
        Assert.Equal(ApiErrorCode.Unknown, new ApiProblem { Code = "teapot" }.ToErrorCode(418));
    }

    [Theory]
    [InlineData(ApiErrorCode.Network)]
    [InlineData(ApiErrorCode.RateLimited)]
    [InlineData(ApiErrorCode.DependencyFailure)]
    [InlineData(ApiErrorCode.Internal)]
    public void RetryingCanHelpAfterAFailureNobodyCausedByTyping(ApiErrorCode code)
    {
        Assert.True(new ApiException(code, "boom").IsTransient);
    }

    [Theory]
    [InlineData(ApiErrorCode.InvalidInput)]
    [InlineData(ApiErrorCode.Unauthenticated)]
    [InlineData(ApiErrorCode.Forbidden)]
    [InlineData(ApiErrorCode.NotFound)]
    [InlineData(ApiErrorCode.Conflict)]
    public void RetryingTheSameCallCannotFixTheUsersMistake(ApiErrorCode code)
    {
        Assert.False(new ApiException(code, "boom").IsTransient);
    }
}
