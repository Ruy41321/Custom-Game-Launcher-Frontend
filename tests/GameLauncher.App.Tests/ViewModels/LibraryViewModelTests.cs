using GameLauncher.App.Services;
using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class LibraryViewModelTests
{
    private readonly ILibraryApi _library = Substitute.For<ILibraryApi>();
    private readonly ILibraryCache _cache = Substitute.For<ILibraryCache>();
    private readonly IAuthenticationService _authentication =
        Substitute.For<IAuthenticationService>();

    private readonly ServerReachability _reachability = new(TimeProvider.System);
    private readonly ICatalogApi _catalog = Substitute.For<ICatalogApi>();
    private readonly IRuntimePlatform _platform = Substitute.For<IRuntimePlatform>();
    private readonly IInstallStore _installs = Substitute.For<IInstallStore>();
    private readonly IGameLauncher _games = Substitute.For<IGameLauncher>();
    private readonly ResourceManagerLocalizationService _localization =
        new("en");

    private readonly IImageProvider _images = Substitute.For<IImageProvider>();

    /// <summary>
    /// The update check every load makes. Arranged here rather than in a factory, and in the
    /// constructor rather than in the tests, because it runs on every load and an unconfigured
    /// substitute answers a `Task&lt;GameDetail&gt;` with null — see the two rows about this in
    /// §7. The default answer is "the installed build is the newest one", which is what every
    /// test written before the check existed assumed.
    /// </summary>
    public LibraryViewModelTests()
    {
        _platform.Platform.Returns(GamePlatform.Windows);
        _platform.Architecture.Returns(BuildArchitecture.X64);
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(DetailWhoseNewestBuildIs("b1"));

        // Same reason as the line above: the offline path reads the cache on every failure,
        // and an unconfigured substitute answers a Task<IReadOnlyList<Game>> with null.
        _cache.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        // Signed in, which is what every test here but the offline-visit ones assumes: with
        // nobody signed in the page deliberately never asks the server at all.
        _authentication.IsAuthenticated.Returns(true);
        _authentication.CurrentSession.Returns(new AuthSession
        {
            User = new AuthenticatedUser { Id = "account-1" },
        });
    }

    private static GameDetail DetailWhoseNewestBuildIs(string buildId) => new()
    {
        // The version has to be there and published, or the build is not a candidate at all (D71).
        Versions = [new GameVersion { Id = "v1", Semver = "1.0.0", Published = true }],
        Builds =
        [
            new GameBuild
            {
                Id = buildId,
                VersionId = "v1",
                Platform = GamePlatform.Windows,
                Architecture = BuildArchitecture.X64,
                Status = BuildStatus.Ready,
                ReadyAt = DateTimeOffset.UnixEpoch,
            },
        ],
    };

    private LibraryViewModel CreateViewModel() =>
        new(
            _library,
            _cache,
            _authentication,
            _reachability,
            _catalog,
            _platform,
            _installs,
            _games,
            new ApiErrorPresenter(_localization, NullLogger<ApiErrorPresenter>.Instance),
            _localization,
            _images);

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
    public async Task EveryCardIsAskedForItsCover()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>()).Returns(
            [new Game { Id = "g1", Title = "Orbital Drift", CoverUrl = "https://f/1.png" }]);

        await CreateViewModel().LoadAsync(TestContext.Current.CancellationToken);

        await _images.Received(1).GetAsync("https://f/1.png", Arg.Any<CancellationToken>());
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
        string id, InstallState state = InstallState.Installed, string coverUrl = "") => new()
        {
            GameId = id,
            GameSlug = id,
            GameTitle = id,
            CoverUrl = coverUrl,
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
        Assert.Equal("Installed version: 0.2.0", installed.StatusText);

        Assert.False(notInstalled.IsInstalled);
        Assert.False(notInstalled.CanPlay);
        Assert.False(notInstalled.HasStatus);

        // Leaving the library while the files are here would leave an install the account no
        // longer owns, which can be neither updated nor repaired. Uninstall first.
        Assert.False(installed.CanRemove);
        Assert.True(notInstalled.CanRemove);
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

    // The artwork cache is indexed by URL and needs no server, so the only thing that was
    // missing offline was somebody who remembered the URL. The row does now. A test cannot
    // assert on a decoded picture (D37), so it asserts on which URL was asked for.
    [Fact]
    public async Task WithNoServerACardStillAsksForTheCoverTheRowRemembers()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "unreachable"));
        Installed(InstalledGameNamed(
            "Orbital Drift", coverUrl: "https://files.example/media/86e1.png"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsOffline);
        Assert.Equal("https://files.example/media/86e1.png", model.Games[0].Game.CoverUrl);
        await _images.Received(1).GetAsync(
            "https://files.example/media/86e1.png", Arg.Any<CancellationToken>());
    }

    // The library is what the account owns, and what is installed is a corner of it. Falling
    // back to the install rows alone showed nothing at all to somebody who had not downloaded
    // a game yet, and hid every title owned and not installed on this machine.
    [Fact]
    public async Task WithNoServerTheLibraryIsTheOneTheServerLastGave()
    {
        _cache.ReadAsync("account-1", Arg.Any<CancellationToken>()).Returns(
        [
            new Game { Id = "g1", Slug = "orbital", Title = "Orbital Drift" },
            new Game { Id = "g2", Slug = "deep-cut", Title = "Deep Cut" },
        ]);

        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "unreachable"));
        Installed(InstalledGameNamed("g1"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsOffline);
        Assert.Null(model.ErrorMessage);
        Assert.Equal(["Orbital Drift", "Deep Cut"], model.Games.Select(card => card.Title));

        // The join still happens offline: one of the two is on this disk and only that one
        // can be played.
        Assert.True(model.Games[0].CanPlay);
        Assert.False(model.Games[1].IsInstalled);
    }

    // A game installed since the last successful load plays perfectly well, and a library that
    // hid it would be hiding the one thing this page is for.
    [Fact]
    public async Task AnInstallTheStoredListDoesNotMentionIsShownAsWell()
    {
        _cache.ReadAsync("account-1", Arg.Any<CancellationToken>()).Returns(
            [new Game { Id = "g1", Slug = "orbital", Title = "Orbital Drift" }]);

        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "unreachable"));
        Installed(InstalledGameNamed("g1"), InstalledGameNamed("g9"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Games.Count);
        Assert.Equal("g9", model.Games[1].Game.Id);
    }

    [Fact]
    public async Task AServerThatAnsweredIsWrittenDownForTheNextStartWithoutOne()
    {
        Returns("Orbital Drift", "Deep Cut");

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        await _cache.Received(1).WriteAsync(
            "account-1",
            Arg.Is<IReadOnlyList<Game>>(games => games!.Count == 2),
            Arg.Any<CancellationToken>());
    }

    // The circuit reopens on its own after a short window; somebody who pressed a button has
    // said they think the network is back and is owed an attempt rather than a wait.
    [Fact]
    public async Task RetryingAsksAgainAtOnce()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "unreachable"));
        Installed(InstalledGameNamed("g1"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        _reachability.ReportUnreachable();

        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Returns([new Game { Id = "g1", Slug = "orbital", Title = "Orbital Drift" }]);
        await model.RetryCommand.ExecuteAsync(null);

        Assert.True(_reachability.AllowsRequests);
        Assert.False(model.IsOffline);
        Assert.Single(model.Games);
    }

    // A launcher that recovers on its own, rather than one that has to be restarted.
    [Fact]
    public async Task TheServerComingBackReloadsAnOfflineLibraryByItself()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "unreachable"));
        Installed(InstalledGameNamed("g1"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsOffline);

        _reachability.ReportUnreachable();
        Returns("Orbital Drift", "Deep Cut");
        _reachability.ReportReachable();
        await Task.Yield();

        Assert.False(model.IsOffline);
        Assert.Equal(2, model.Games.Count);
    }

    // The offline visit: no session at all, so there is no account to ask about and no request
    // worth making. What is on this disk was paid for already and plays.
    [Fact]
    public async Task WithNobodySignedInTheDiskIsTheLibraryAndNothingIsAsked()
    {
        _authentication.IsAuthenticated.Returns(false);
        _authentication.CurrentSession.Returns((AuthSession?)null);
        Installed(InstalledGameNamed("g1"), InstalledGameNamed("g2"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsOffline);
        Assert.True(model.IsOfflineWithoutAccount);
        Assert.False(model.IsOfflineWithAccount);
        Assert.Null(model.ErrorMessage);
        Assert.Equal(2, model.Games.Count);
        Assert.All(model.Games, card => Assert.True(card.CanPlay));

        await _library.DidNotReceive().GetLibraryAsync(Arg.Any<CancellationToken>());
    }

    // The stored library belongs to an account. Handing the last person's list to whoever
    // opens the launcher next is not something an unreachable server excuses.
    [Fact]
    public async Task AnOfflineVisitNeverShowsTheLastAccountsList()
    {
        _authentication.IsAuthenticated.Returns(false);
        _authentication.CurrentSession.Returns((AuthSession?)null);
        _cache.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            [new Game { Id = "g7", Slug = "secret", Title = "Somebody Else's Game" }]);
        Installed(InstalledGameNamed("g1"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("g1", Assert.Single(model.Games).Game.Id);
    }

    // The game page is built from the catalog, so offline it can only produce an error — and
    // with nobody signed in it is the wrong error, since no session ever expired. The button
    // goes, and the command refuses as well.
    [Fact]
    public async Task OfflineACardOffersNoWayToAPageThatNeedsAServer()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "unreachable"));
        Installed(InstalledGameNamed("g1"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        GameCardViewModel card = Assert.Single(model.Games);
        Assert.False(card.CanOpenDetails);

        bool opened = false;
        model.GameSelected += (_, _) => opened = true;
        model.OpenGameCommand.Execute(card);

        Assert.False(opened);
    }

    [Fact]
    public async Task OnlineTheCardStillOpensTheGamePage()
    {
        Returns("Orbital Drift");

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        GameCardViewModel card = Assert.Single(model.Games);
        Assert.True(card.CanOpenDetails);

        string? opened = null;
        model.GameSelected += (_, slug) => opened = slug;
        model.OpenGameCommand.Execute(card);

        Assert.Equal("Orbital Drift", opened);
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

    // --- the update check (D69) ------------------------------------------------------------

    // The card used to know nothing about updates, which left Play on a game that must not be
    // started until it is updated — the game page hid it and the library offered it.
    [Fact]
    public async Task APendingUpdateTakesPlayOffTheCardAndSaysWhy()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift"));
        _catalog.GetGameAsync("Orbital Drift", Arg.Any<CancellationToken>())
            .Returns(DetailWhoseNewestBuildIs("b2"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        GameCardViewModel card = model.Games[0];
        Assert.True(card.HasUpdate);
        Assert.False(card.CanPlay);
        Assert.True(card.MustUpdateBeforePlaying);
    }

    // D71, found by looking at the window: the publisher of a game is served their own
    // unpublished versions, so the newest build in the document was one nobody may download —
    // and their card lost Play for good over it.
    [Fact]
    public async Task ABuildOfAnUnpublishedVersionIsNotAnUpdate()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift"));
        _catalog.GetGameAsync("Orbital Drift", Arg.Any<CancellationToken>()).Returns(
            new GameDetail
            {
                Versions =
                [
                    new GameVersion { Id = "v1", Semver = "1.0.0", Published = true },
                    new GameVersion { Id = "v2", Semver = "2.0.0", Published = false },
                ],
                Builds =
                [
                    new GameBuild
                    {
                        Id = "b1",
                        VersionId = "v1",
                        Platform = GamePlatform.Windows,
                        Architecture = BuildArchitecture.X64,
                        Status = BuildStatus.Ready,
                        ReadyAt = DateTimeOffset.UnixEpoch,
                    },
                    new GameBuild
                    {
                        Id = "b2",
                        VersionId = "v2",
                        Platform = GamePlatform.Windows,
                        Architecture = BuildArchitecture.X64,
                        Status = BuildStatus.Ready,
                        ReadyAt = DateTimeOffset.UnixEpoch.AddDays(30),
                    },
                ],
            });

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        GameCardViewModel card = model.Games[0];
        Assert.False(card.HasUpdate);
        Assert.True(card.CanPlay);
    }

    // Pressed on its way to being hidden: the check can land between the press and the click.
    [Fact]
    public async Task PressingPlayOnAGameThatNeedsUpdatingStartsNothing()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift"));
        _catalog.GetGameAsync("Orbital Drift", Arg.Any<CancellationToken>())
            .Returns(DetailWhoseNewestBuildIs("b2"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        await model.PlayCommand.ExecuteAsync(model.Games[0]);

        await _games.DidNotReceive().LaunchAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The cost of this feature, asserted so it cannot grow quietly: a library is everything an
    // account was ever given, and only what is on this disk has a Play button to take away.
    [Fact]
    public async Task OnlyInstalledGamesCostARequest()
    {
        Returns("Orbital Drift", "Deep Cut", "Nightjar");
        Installed(InstalledGameNamed("Orbital Drift"));

        await CreateViewModel().LoadAsync(TestContext.Current.CancellationToken);

        await _catalog.Received(1).GetGameAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _catalog.Received(1).GetGameAsync(
            "Orbital Drift", Arg.Any<CancellationToken>());
    }

    // A game whose files are here but whose install never finished has nothing to compare.
    [Fact]
    public async Task AnUnfinishedInstallIsNotAskedAbout()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift", InstallState.Broken));

        await CreateViewModel().LoadAsync(TestContext.Current.CancellationToken);

        await _catalog.DidNotReceive().GetGameAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Nothing shipped for this machine since is not an update: what is installed stays playable.
    [Fact]
    public async Task NoBuildForThisMachineIsNotAnUpdate()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift"));
        _catalog.GetGameAsync("Orbital Drift", Arg.Any<CancellationToken>())
            .Returns(new GameDetail());

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(model.Games[0].HasUpdate);
        Assert.True(model.Games[0].CanPlay);
    }

    // Offline the question cannot be asked, and refusing to start a game already on this disk
    // over an unanswered question is exactly what the offline library exists to avoid.
    [Fact]
    public async Task WithNoServerNothingIsAskedAndPlayStays()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "unreachable"));
        Installed(InstalledGameNamed("Orbital Drift"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        await _catalog.DidNotReceive().GetGameAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.True(model.Games[0].CanPlay);
    }

    // One card that could not be checked is not a library that failed to load.
    [Fact]
    public async Task ACheckThatIsRefusedLeavesTheCardAloneAndSaysNothing()
    {
        Returns("Orbital Drift");
        Installed(InstalledGameNamed("Orbital Drift"));
        _catalog.GetGameAsync("Orbital Drift", Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.NotFound, "gone"));

        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(model.ErrorMessage);
        Assert.False(model.Games[0].HasUpdate);
        Assert.True(model.Games[0].CanPlay);
    }

    // --- what a page keeps across accounts (D70) ---------------------------------------------

    [Fact]
    public async Task TheListDoesNotSurviveAChangeOfAccount()
    {
        Returns("Orbital Drift", "Deep Cut");
        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, model.Games.Count);

        model.ResetForAccountChange();

        Assert.Empty(model.Games);
        Assert.False(model.IsEmpty);
        Assert.Null(model.ErrorMessage);
    }
}
