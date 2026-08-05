using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class GameEditorViewModelTests
{
    private readonly IPublishingApi _publishing = Substitute.For<IPublishingApi>();
    private readonly ResourceManagerLocalizationService _localization = new("en");

    private GameEditorViewModel CreateViewModel() =>
        new(_publishing, new ApiErrorPresenter(_localization), _localization);

    private static Game Existing() => new()
    {
        Id = "g1",
        Slug = "orbital-drift",
        Title = "Orbital Drift",
        Summary = "A short one.",
        Description = "A longer one.",
        ReleaseDate = new DateOnly(2026, 5, 4),
        Visibility = GameVisibility.Draft,
    };

    private GameEditorViewModel Showing(Game game)
    {
        GameEditorViewModel model = CreateViewModel();
        model.Show(game);
        return model;
    }

    [Fact]
    public void TheFormIsFilledFromTheGame()
    {
        GameEditorViewModel model = Showing(Existing());

        Assert.True(model.HasGame);
        Assert.Equal("Orbital Drift", model.Title);
        Assert.Equal("A short one.", model.Summary);
        Assert.Equal("A longer one.", model.Description);
        Assert.Equal(new DateOnly(2026, 5, 4), DateOnly.FromDateTime(model.ReleaseDate!.Value.Date));
        Assert.Equal(GameVisibility.Draft, model.Visibility);
    }

    // A page with nothing to save must not offer to save: pressing it would send a PATCH that
    // rewrites every field with the value a text box happened to hold.
    [Fact]
    public void APageThatHasNotBeenEditedCannotBeSaved()
    {
        GameEditorViewModel model = Showing(Existing());

        Assert.False(model.HasChanges);
        Assert.False(model.CanSave);
    }

    [Fact]
    public void EditingAFieldEnablesTheSave()
    {
        GameEditorViewModel model = Showing(Existing());

        model.Summary = "A different one.";

        Assert.True(model.HasChanges);
        Assert.True(model.CanSave);
    }

    // The whole reason the changes are computed rather than sent wholesale: null is absence on
    // the wire, and an absent field is a field the server leaves alone.
    [Fact]
    public async Task OnlyTheEditedFieldsAreSent()
    {
        GameEditorViewModel model = Showing(Existing());
        _publishing.UpdateGameAsync(
                Arg.Any<string>(), Arg.Any<GameChanges>(), Arg.Any<CancellationToken>())
            .Returns(Existing());

        model.Summary = "A different one.";
        await model.SaveCommand.ExecuteAsync(null);

        await _publishing.Received(1).UpdateGameAsync(
            "g1",
            Arg.Is<GameChanges>(changes =>
                changes!.Summary == "A different one."
                && changes.Title == null
                && changes.Description == null
                && changes.ReleaseDate == null
                && changes.Visibility == null),
            Arg.Any<CancellationToken>());
    }

    // This is what closes the debt: a game created as a draft can be published from the client.
    [Fact]
    public async Task ADraftCanBeMadePublicFromHere()
    {
        GameEditorViewModel model = Showing(Existing());
        _publishing.UpdateGameAsync(
                Arg.Any<string>(), Arg.Any<GameChanges>(), Arg.Any<CancellationToken>())
            .Returns(Existing() with { Visibility = GameVisibility.Public });

        model.Visibility = GameVisibility.Public;
        await model.SaveCommand.ExecuteAsync(null);

        await _publishing.Received(1).UpdateGameAsync(
            "g1",
            Arg.Is<GameChanges>(changes => changes!.Visibility == GameVisibility.Public),
            Arg.Any<CancellationToken>());

        Assert.Equal(GameVisibility.Public, model.Visibility);
    }

    // Reseeding from the response is what makes a value the server normalised — a trimmed
    // title — the one the page then shows, instead of the raw thing that was typed.
    [Fact]
    public async Task TheFormIsReseededFromWhatTheServerAnswered()
    {
        GameEditorViewModel model = Showing(Existing());
        _publishing.UpdateGameAsync(
                Arg.Any<string>(), Arg.Any<GameChanges>(), Arg.Any<CancellationToken>())
            .Returns(Existing() with { Title = "Orbital Drift II" });

        model.Title = "  Orbital Drift II  ";
        await model.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Orbital Drift II", model.Title);
        Assert.False(model.HasChanges);
        Assert.Equal(_localization.Translate("Publish.GameUpdated"), model.StatusMessage);
    }

    [Fact]
    public async Task ASavedEditIsAnnouncedSoTheListAboveCanRefreshItsRow()
    {
        GameEditorViewModel model = Showing(Existing());
        _publishing.UpdateGameAsync(
                Arg.Any<string>(), Arg.Any<GameChanges>(), Arg.Any<CancellationToken>())
            .Returns(Existing() with { Title = "Orbital Drift II" });

        Game? announced = null;
        model.GameUpdated += (_, game) => announced = game;

        model.Title = "Orbital Drift II";
        await model.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Orbital Drift II", announced?.Title);
    }

    // A game the caller may not edit is a 404 server-side, and it is shown as unavailable —
    // never as a permissions problem, which would confirm the game exists.
    [Fact]
    public async Task ARefusalIsReportedAndTheFormKeepsWhatWasTyped()
    {
        GameEditorViewModel model = Showing(Existing());
        _publishing.UpdateGameAsync(
                Arg.Any<string>(), Arg.Any<GameChanges>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.NotFound, "no such game"));

        model.Title = "Orbital Drift II";
        await model.SaveCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Error.NotFound"), model.ErrorMessage);
        Assert.Equal("Orbital Drift II", model.Title);
    }

    [Fact]
    public void ShowingNothingEmptiesTheFormAndDisablesIt()
    {
        GameEditorViewModel model = Showing(Existing());

        model.Show(null);

        Assert.False(model.HasGame);
        Assert.False(model.CanSave);
        Assert.Equal(string.Empty, model.Title);
    }

    [Fact]
    public async Task AGameWithNoReleaseDateCanBeGivenOne()
    {
        GameEditorViewModel model = Showing(Existing() with { ReleaseDate = null });
        _publishing.UpdateGameAsync(
                Arg.Any<string>(), Arg.Any<GameChanges>(), Arg.Any<CancellationToken>())
            .Returns(Existing());

        Assert.Null(model.ReleaseDate);

        model.ReleaseDate = new DateTimeOffset(new DateTime(2026, 5, 4), TimeSpan.Zero);
        await model.SaveCommand.ExecuteAsync(null);

        await _publishing.Received(1).UpdateGameAsync(
            "g1",
            Arg.Is<GameChanges>(changes => changes!.ReleaseDate == new DateOnly(2026, 5, 4)),
            Arg.Any<CancellationToken>());
    }
}
