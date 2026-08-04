using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class LibraryViewModelTests
{
    private readonly ILibraryApi _library = Substitute.For<ILibraryApi>();
    private readonly IInstallStore _installs = Substitute.For<IInstallStore>();
    private readonly IGameLauncher _games = Substitute.For<IGameLauncher>();
    private readonly ResourceManagerLocalizationService _localization =
        new("en");

    private LibraryViewModel CreateViewModel() =>
        new(_library, _installs, _games, new ApiErrorPresenter(_localization), _localization);

    private void Returns(params string[] titles) =>
        _library.GetLibraryAsync(Arg.Any<CancellationToken>()).Returns(
            [.. titles.Select(title => new Game { Id = title, Slug = title, Title = title })]);

    [Fact]
    public async Task LoadingFillsTheList()
    {
        Returns("Orbital Drift", "Deep Cut");
        LibraryViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Games.Count);
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void AnUnloadedLibraryIsNotAnEmptyOne()
    {
        Assert.False(CreateViewModel().IsEmpty);
    }

    [Fact]
    public async Task AnAccountWithNoGamesIsReportedAsEmpty()
    {
        Returns();
        LibraryViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsEmpty);
    }

    [Fact]
    public async Task ReloadingDoesNotDuplicateWhatIsAlreadyThere()
    {
        Returns("Orbital Drift");
        LibraryViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(model.Games);
    }

    [Fact]
    public async Task AFailedLoadShowsAMessageAndNotAnEmptyLibrary()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.DependencyFailure, "down"));
        LibraryViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_localization.Translate("Error.DependencyFailure"), model.ErrorMessage);
        Assert.False(model.IsEmpty);
    }

    // The server has confirmed the removal, so a second round trip would only make the list
    // flicker for information it already has.
    [Fact]
    public async Task RemovingTakesTheGameOutOfTheListWithoutReloading()
    {
        Returns("Orbital Drift", "Deep Cut");
        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        GameCardViewModel removed = model.Games[0];

        await model.RemoveCommand.ExecuteAsync(removed);

        await _library.Received(1).RemoveAsync(removed.Game.Id, Arg.Any<CancellationToken>());
        Assert.Single(model.Games);
        await _library.Received(1).GetLibraryAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedRemovalLeavesTheGameInPlace()
    {
        Returns("Orbital Drift");
        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        _library.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.NotFound, "gone"));
        await model.RemoveCommand.ExecuteAsync(model.Games[0]);

        Assert.Single(model.Games);
        Assert.Equal(_localization.Translate("Error.NotFound"), model.ErrorMessage);
    }

    [Fact]
    public async Task RemovingTheLastGameLeavesAnEmptyLibraryAndSaysSo()
    {
        Returns("Orbital Drift");
        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        await model.RemoveCommand.ExecuteAsync(model.Games[0]);

        Assert.True(model.IsEmpty);
    }

    private static InstalledGame InstalledGameNamed(
        string id, InstallState state = InstallState.Installed) => new()
        {
            GameId = id,
            GameSlug = id,
            GameTitle = id,
            BuildId = "b1",
            VersionSemver = "0.2.0",
            InstallDirectory = "/games/" + id,
            Entrypoint = "Game.exe",
            State = state,
        };

    private void Installed(params InstalledGame[] installs) =>
        _installs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(installs);

    [Fact]
    public async Task TheListSaysWhichGamesAreOnThisMachine()
    {
        Returns("Orbital Drift", "Deep Cut");
        Installed(InstalledGameNamed("Orbital Drift"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        GameCardViewModel installed = model.Games.Single(card => card.Title == "Orbital Drift");
        GameCardViewModel notInstalled = model.Games.Single(card => card.Title == "Deep Cut");

        Assert.True(installed.IsInstalled);
        Assert.True(installed.CanPlay);
        Assert.Equal("Installed: 0.2.0", installed.StatusText);

        Assert.False(notInstalled.IsInstalled);
        Assert.False(notInstalled.CanPlay);
        Assert.False(notInstalled.HasStatus);
    }

    // One query for the whole list rather than one per card: a lookup per game would make the
    // cost of drawing the library depend on how many of them are installed.
    [Fact]
    public async Task TheInstalledGamesAreReadOnceForTheWholeList()
    {
        Returns("Orbital Drift", "Deep Cut", "Zephyr");
        Installed(InstalledGameNamed("Orbital Drift"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        await _installs.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
        await _installs.DidNotReceive().FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADamagedInstallIsLabelledAndCannotBePlayed()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift", InstallState.Broken));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        GameCardViewModel card = model.Games[0];
        Assert.True(card.IsBroken);
        Assert.False(card.CanPlay);
        Assert.Equal("Damaged", card.StatusText);
    }

    [Fact]
    public async Task PlayingFromTheListStartsTheGame()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift"));
        _games.LaunchAsync("Orbital Drift", Arg.Any<CancellationToken>())
            .Returns(new RunningGame("Orbital Drift", 42, DateTimeOffset.UnixEpoch));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        await model.PlayCommand.ExecuteAsync(model.Games[0]);

        await _games.Received(1).LaunchAsync("Orbital Drift", Arg.Any<CancellationToken>());
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task ARunningGameSaysSoAndIsNotOfferedAgain()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift"));
        _games.IsRunning("Orbital Drift").Returns(true);

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.Games[0].IsRunning);
        Assert.False(model.Games[0].CanPlay);
        Assert.Equal("Running…", model.Games[0].StatusText);
    }

    // The exit arrives on a thread that is not the UI's, and only that game's card changes.
    [Fact]
    public async Task WhenAGameExitsItsCardOffersToPlayItAgain()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift"));
        _games.IsRunning("Orbital Drift").Returns(true);

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(model.Games[0].CanPlay);

        _games.IsRunning("Orbital Drift").Returns(false);
        _games.GameExited += Raise.EventWith(
            new GameExitedEventArgs("Orbital Drift", 0, TimeSpan.FromMinutes(5)));

        Assert.True(model.Games[0].CanPlay);
    }

    [Fact]
    public async Task ARefusedLaunchFromTheListSaysWhy()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift"));
        _games.LaunchAsync("Orbital Drift", Arg.Any<CancellationToken>())
            .ThrowsAsync(new GameLaunchException(LaunchFailure.AlreadyRunning, "already"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        await model.PlayCommand.ExecuteAsync(model.Games[0]);

        Assert.Equal("That game is already running.", model.ErrorMessage);
    }

    // Unreachable, not refused. What is installed is still installed and still playable, and a
    // launcher showing an error where the games should be would be useless on a train.
    [Fact]
    public async Task WithNoServerTheLibraryShowsWhatIsOnThisDisk()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "unreachable"));
        Installed(InstalledGameNamed("Orbital Drift"), InstalledGameNamed("Deep Cut"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsOffline);
        Assert.Null(model.ErrorMessage);
        Assert.Equal(2, model.Games.Count);
        Assert.All(model.Games, card => Assert.True(card.CanPlay));
        Assert.Equal("Orbital Drift", model.Games[0].Title);
    }

    [Fact]
    public async Task ComingBackOnlineReplacesTheOfflineListAndClearsTheBanner()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "unreachable"));
        Installed(InstalledGameNamed("Orbital Drift"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsOffline);

        Returns("Orbital Drift", "Deep Cut");
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(model.IsOffline);
        Assert.Equal(2, model.Games.Count);
    }

    // A refusal is not an outage: an expired session has to be said out loud, or the player
    // sees a short library and no reason for it.
    [Fact]
    public async Task AServerThatAnswersAndRefusesIsStillAnError()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Unauthenticated, "expired"));
        Installed(InstalledGameNamed("Orbital Drift"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(model.IsOffline);
        Assert.Equal(_localization.Translate("Error.Unauthenticated"), model.ErrorMessage);
        Assert.Empty(model.Games);
    }

    [Fact]
    public void OpeningAGameAsksForItByItsIdentifier()
    {
        LibraryViewModel model = CreateViewModel();
        string? requested = null;
        model.GameSelected += (_, idOrSlug) => requested = idOrSlug;

        model.OpenGameCommand.Execute(new GameCardViewModel(
            new Game { Id = "g1", Slug = "orbital-drift" }, null, _games, _localization));

        Assert.Equal("orbital-drift", requested);
    }
}
