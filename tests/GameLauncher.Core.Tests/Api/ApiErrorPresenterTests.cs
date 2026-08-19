using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Core.Tests.Api;

/// <summary>
/// The presenter is where a failure becomes a sentence somebody reads, so these assert on the
/// sentence rather than on a code — the wording is the half that actually helps a person.
/// </summary>
public sealed class ApiErrorPresenterTests
{
    /// <summary>Every rule name the backend's <c>domain/ValidationRules.h</c> defines.</summary>
    public static TheoryData<string> EveryKnownRule() =>
    [
        "email_required", "email_too_long", "email_invalid",
        "password_required", "password_too_short", "password_too_long", "password_blank",
        "display_name_required", "display_name_too_short", "display_name_too_long",
        "display_name_invalid",
        "erasure_reason_too_long",
        "title_required", "title_too_long", "summary_too_long", "description_too_long",
        "slug_required", "slug_too_long", "slug_invalid", "release_date_invalid",
        "version_required", "version_invalid", "version_too_large", "release_notes_too_long",
        "build_name_too_long",
        "patch_note_title_required", "patch_note_title_too_long", "patch_note_body_too_long",
        "alt_text_too_long", "alt_text_invalid",
    ];

    private static ApiException Validation(
        string? rule, IReadOnlyList<string>? args = null, string? requestId = "01HZY") =>
        new(ApiErrorCode.InvalidInput, "server prose nobody should read", 422, requestId)
        {
            Rule = rule,
            RuleArgs = args ?? [],
        };

    private static ApiErrorPresenter PresenterIn(
        string culture, ILogger<ApiErrorPresenter>? logger = null)
    {
        var localization = new ResourceManagerLocalizationService(culture);
        return new ApiErrorPresenter(localization, logger ?? NullLogger<ApiErrorPresenter>.Instance);
    }

    // The complaint this whole mechanism answers: registering with a short password used to
    // read "Some of what you entered was not accepted (reference 01HZY)".
    [Fact]
    public void AWeakPasswordIsNamedAndTheMinimumIsSaidOutLoud()
    {
        string message = PresenterIn("en").Describe(Validation("password_too_short", ["8"]));

        Assert.Equal("The password must be at least 8 characters.", message);
    }

    // The reference is for an operator to find a request in a log, and the person reading the
    // message is never that operator. No sentence carries it any more (D67) — including the
    // validation one, which never did, and which leaves nothing in a log worth finding.
    [Fact]
    public void AValidationFailureNeverShowsTheRequestId()
    {
        string message = PresenterIn("en").Describe(Validation("password_too_short", ["8"]));

        Assert.DoesNotContain("01HZY", message);
    }

    [Fact]
    public void NoOtherFailureShowsTheRequestIdEither()
    {
        string message = PresenterIn("en").Describe(
            new ApiException(ApiErrorCode.Internal, "boom", 500, "01HZY"));

        Assert.DoesNotContain("01HZY", message);
        Assert.Equal("Something went wrong. Please try again.", message);
    }

    // Dropped from the sentence, kept where it is useful: an operator asked for a launcher log
    // can still find the request. Deleting it would have cost the only thing it was good for.
    [Fact]
    public void TheRequestIdGoesToTheLogInstead()
    {
        var logger = new RecordingLogger();

        PresenterIn("en", logger).Describe(new ApiException(ApiErrorCode.Internal, "boom", 500, "01HZY"));

        Assert.Contains(logger.Lines, line => line.Contains("01HZY", StringComparison.Ordinal));
    }

    // A validation failure is the server behaving correctly, so it does not become a warning in
    // somebody's log file every time they mistype an email address.
    [Fact]
    public void AValidationFailureIsNotLogged()
    {
        var logger = new RecordingLogger();

        PresenterIn("en", logger).Describe(Validation("password_too_short", ["8"]));

        Assert.Empty(logger.Lines);
    }

    // A server newer than this launcher sends rules it has never heard of, and the answer is
    // the sentence it used to give for all of them — never the server's English prose.
    [Fact]
    public void AnUnknownRuleFallsBackToTheGenericSentence()
    {
        var presenter = PresenterIn("en");

        string unknown = presenter.Describe(Validation("some_future_rule", ["7"]));
        string none = presenter.Describe(Validation(rule: null));

        Assert.Equal(none, unknown);
        Assert.Equal("Some of what you entered was not accepted.", unknown);
        Assert.DoesNotContain("server prose", unknown);
    }

    [Theory]
    [MemberData(nameof(EveryKnownRule))]
    public void EveryKnownRuleHasASentenceInEveryLanguage(string rule)
    {
        foreach (string culture in new[] { "en", "it", "fr" })
        {
            var presenter = PresenterIn(culture);
            string generic = presenter.Describe(Validation(rule: null));
            string specific = presenter.Describe(Validation(rule, ["10"]));

            Assert.DoesNotContain("!Error.", specific);
            Assert.DoesNotContain("{0}", specific);
            Assert.NotEqual(generic, specific);
        }
    }

    // The Italian is the case the maintainer met, so it is asserted rather than assumed.
    [Fact]
    public void TheItalianSaysTheSameThingWithTheSameNumber()
    {
        string message = PresenterIn("it").Describe(Validation("password_too_short", ["8"]));

        Assert.Equal("La password deve contenere almeno 8 caratteri.", message);
    }

    // A rule whose sentence takes no argument is unharmed by a server that sends one.
    [Fact]
    public void ArgumentsAreIgnoredBySentencesThatDoNotWantThem()
    {
        Assert.Equal(
            "That does not look like an email address.",
            PresenterIn("en").Describe(Validation("email_invalid", ["99"])));
    }

    // And a sentence that needs a number, arriving without one, falls back rather than putting
    // a raw placeholder in front of somebody.
    [Fact]
    public void ARuleMissingTheArgumentItsSentenceNeedsFallsBack()
    {
        var presenter = PresenterIn("en");

        Assert.Equal(
            presenter.Describe(Validation(rule: null)),
            presenter.Describe(Validation("password_too_short")));
    }

    /// <summary>Keeps the formatted lines, which is the whole of what these tests ask of a log.</summary>
    private sealed class RecordingLogger : ILogger<ApiErrorPresenter>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
