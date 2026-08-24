using GameLauncher.App.Services;
using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class GameDetailViewModelTests
{
    private readonly ICatalogApi _catalog = Substitute.For<ICatalogApi>();
    private readonly ILibraryApi _library = Substitute.For<ILibraryApi>();
    private readonly IAuthenticationService _authentication =
        Substitute.For<IAuthenticationService>();
    private readonly ResourceManagerLocalizationService _localization =
        new("en");

    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly IInstallationService _installations =
        Substitute.For<IInstallationService>();

    private readonly IInstallStore _installs = Substitute.For<IInstallStore>();

    private readonly IGameLauncher _games = Substitute.For<IGameLauncher>();

    private readonly IImageProvider _images = Substitute.For<IImageProvider>();

    /// <summary>
    /// Playback is unavailable unless a test says otherwise, which is the honest default:
    /// a substitute has no native library behind it, and so does a machine without VLC.
    /// </summary>
    private readonly IVideoPlayback _playback = Substitute.For<IVideoPlayback>();

    private readonly IFileBrowser _files = Substitute.For<IFileBrowser>();

    private readonly IFolderPicker _folders = Substitute.For<IFolderPicker>();

    private readonly IUserSettingsStore _settings = Substitute.For<IUserSettingsStore>();

    /// <summary>
    /// The preferences and the devlog are read on paths every test walks through, and an
    /// unconfigured substitute answers a <c>Task&lt;T&gt;</c> with <c>default(T)</c> — a null
    /// <see cref="UserSettings"/> crashes the view model instead of failing an assertion.
    /// Arranged in the constructor, which runs before the test body, so a test that wants
    /// other settings arranges them over the top (NSubstitute's last stub wins).
    /// </summary>
    public GameDetailViewModelTests() =>
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new UserSettings());

    private GameDetailViewModel CreateViewModel(
        GamePlatform platform = GamePlatform.Windows,
        BuildArchitecture architecture = BuildArchitecture.X64)
    {
        var runtime = Substitute.For<IRuntimePlatform>();
        runtime.Platform.Returns(platform);
        runtime.Architecture.Returns(architecture);

        // Every load asks for the devlog. Arranged here rather than in each test, and
        // arranged first: a test that cares about the devlog overrides this afterwards, which
        // is the order NSubstitute resolves in.
        _catalog
            .GetPatchNotesAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<PatchNote>());

        return new GameDetailViewModel(
            _catalog,
            _library,
            new ApiErrorPresenter(_localization, NullLogger<ApiErrorPresenter>.Instance),
            _localization,
            runtime,
            _authentication,
            _installations,
            _installs,
            _games,
            _images,
            _playback,
            _files,
            _folders,
            _settings,
            new FakeTimeProvider(Now));
    }

    private static GameDetail DetailWith(bool inLibrary = false, params GameBuild[] builds) => new()
    {
        Game = new Game
        {
            Id = "g1",
            Slug = "orbital-drift",
            Title = "Orbital Drift",
            Summary = "A short one.",
            Description = "A long one.",
            ReleaseDate = new DateOnly(2026, 5, 4),
            Publisher = new Publisher { Id = "u1", DisplayName = "Luigi" },
        },
        InLibrary = inLibrary,
        Versions =
        [
            new GameVersion
            {
                Id = "v1",
                Semver = "0.2.0",
                Stage = BuildStage.Beta,
                ReleaseNotes = "Fixed the thing.",
                Published = true,
                PublishedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            },
        ],
        Builds = builds,
    };

    private void Returns(GameDetail detail) =>
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(detail);

    private static GameBuild WindowsBuild(string id = "win") => new()
    {
        Id = id,
        VersionId = "v1",
        Platform = GamePlatform.Windows,
        Architecture = BuildArchitecture.X64,
        Status = BuildStatus.Ready,
        Entrypoint = "Game.exe",
        TotalSizeBytes = 5_368_709_120,
    };

    private static InstalledGame InstalledAt(
        string buildId, InstallState state = InstallState.Installed) => new()
        {
            GameId = "g1",
            GameSlug = "orbital-drift",
            GameTitle = "Orbital Drift",
            BuildId = buildId,
            VersionId = "v1",
            VersionSemver = "0.2.0",
            Platform = GamePlatform.Windows,
            Architecture = BuildArchitecture.X64,
            InstallDirectory = "/games/orbital-drift",
            Entrypoint = "Game.exe",
            State = state,
            InstalledAt = Now,
            UpdatedAt = Now,
        };

    private void AlreadyInstalled(InstalledGame install) =>
        _installs.FindAsync("g1", Arg.Any<CancellationToken>()).Returns(install);

    private void CanDownload() =>
        _authentication.HasPermission(Permissions.GameDownload).Returns(true);

    [Fact]
    public async Task LoadingFillsThePage()
    {
        Returns(DetailWith(inLibrary: true));
        GameDetailViewModel model = CreateViewModel();

        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.Equal("Orbital Drift", model.Title);
        Assert.Equal("A short one.", model.Summary);
        Assert.Equal("Luigi", model.PublisherName);
        Assert.True(model.InLibrary);
        Assert.Single(model.Versions);
        Assert.Equal("Beta", model.Versions[0].Stage);
        Assert.True(model.Versions[0].ShowStage);
    }

    // The badge exists to say "this is not finished"; on a release it is noise.
    [Fact]
    public void AReleaseVersionCarriesNoStageBadge()
    {
        var card = new VersionCardViewModel(
            new GameVersion { Semver = "1.0.0", Stage = BuildStage.Release }, _localization);

        Assert.False(card.ShowStage);
    }

    [Fact]
    public async Task AGameWithNoAnnouncedDateSaysSoRatherThanShowingNothing()
    {
        Returns(DetailWith() with { Game = new Game { Title = "Orbital Drift" } });
        GameDetailViewModel model = CreateViewModel();

        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.Equal(_localization.Translate("Detail.Unreleased"), model.ReleaseDate);
    }

    [Fact]
    public async Task TheBuildOfferedIsTheOneForThisMachine()
    {
        Returns(DetailWith(
            false,
            new GameBuild { Id = "win", VersionId = "v1", Platform = GamePlatform.Windows, Status = BuildStatus.Ready },
            new GameBuild { Id = "lin", VersionId = "v1", Platform = GamePlatform.Linux, Status = BuildStatus.Ready }));

        GameDetailViewModel model = CreateViewModel(GamePlatform.Linux);
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.Equal("lin", model.InstallableBuild?.Id);
        Assert.True(model.HasInstallableBuild);
    }

    [Fact]
    public async Task NothingForThisPlatformIsSaidPlainly()
    {
        Returns(DetailWith(
            false,
            new GameBuild { Id = "win", VersionId = "v1", Platform = GamePlatform.Windows, Status = BuildStatus.Ready }));

        GameDetailViewModel model = CreateViewModel(GamePlatform.MacOs);
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.False(model.HasInstallableBuild);
        Assert.False(model.CanDownload);
    }

    // Hiding the button is a courtesy; the server checks the same permission on the request.
    [Fact]
    public async Task DownloadingIsOnlyOfferedToAnAccountThatMay()
    {
        Returns(DetailWith(
            false,
            new GameBuild
            {
                Id = "win",
                VersionId = "v1",
                Platform = GamePlatform.Windows,
                Status = BuildStatus.Ready,
                TotalSizeBytes = 3_221_225_472,
            }));

        _authentication.HasPermission(Permissions.GameDownload).Returns(false);
        GameDetailViewModel withoutPermission = CreateViewModel();
        await withoutPermission.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        Assert.False(withoutPermission.CanDownload);

        _authentication.HasPermission(Permissions.GameDownload).Returns(true);
        GameDetailViewModel withPermission = CreateViewModel();
        await withPermission.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        Assert.True(withPermission.CanDownload);
        Assert.Equal("3 GB", withPermission.DownloadSize);
    }

    [Fact]
    public async Task AddingAndRemovingFlipTheLibraryState()
    {
        Returns(DetailWith());
        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        await model.AddToLibraryCommand.ExecuteAsync(null);
        Assert.True(model.InLibrary);
        await _library.Received(1).AddAsync("g1", Arg.Any<CancellationToken>());

        await model.RemoveFromLibraryCommand.ExecuteAsync(null);
        Assert.False(model.InLibrary);
        await _library.Received(1).RemoveAsync("g1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedAddLeavesTheStateAloneAndSaysWhy()
    {
        Returns(DetailWith());
        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        _library.AddAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Forbidden, "no"));
        await model.AddToLibraryCommand.ExecuteAsync(null);

        Assert.False(model.InLibrary);
        Assert.Equal(_localization.Translate("Error.Forbidden"), model.ErrorMessage);
    }

    // A draft nobody may see is a 404 by design; the page must not imply it exists.
    [Fact]
    public async Task AGameTheAccountMayNotSeeIsReportedAsMissing()
    {
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.NotFound, "no such game"));
        GameDetailViewModel model = CreateViewModel();

        await model.LoadAsync("someones-draft", TestContext.Current.CancellationToken);

        Assert.Null(model.Detail);
        Assert.Equal(_localization.Translate("Error.NotFound"), model.ErrorMessage);
    }

    // The view model is reused across navigations, so it has to forget the previous game.
    [Fact]
    public async Task LoadingASecondGameLeavesNothingOfTheFirst()
    {
        Returns(DetailWith());
        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.NotFound, "gone"));
        await model.LoadAsync("deep-cut", TestContext.Current.CancellationToken);

        Assert.Null(model.Detail);
        Assert.Empty(model.Versions);
        Assert.Empty(model.Title);
    }

    [Fact]
    public void GoingBackIsAnnouncedRatherThanDecidedHere()
    {
        GameDetailViewModel model = CreateViewModel();
        bool asked = false;
        model.BackRequested += (_, _) => asked = true;

        model.BackCommand.Execute(null);

        Assert.True(asked);
    }

    [Fact]
    public async Task InstallingAsksForTheBuildThisMachineCanRun()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        InstallRequest? asked = null;
        _installations
            .InstallAsync(
                Arg.Do<InstallRequest>(request => asked = request),
                Arg.Any<IProgress<DownloadProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(new InstallResult { Install = InstalledAt("win") });

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.True(model.CanInstall);
        await model.InstallCommand.ExecuteAsync(null);

        Assert.Equal("win", asked?.Build.Id);
        Assert.Equal("g1", asked?.Game.Id);
        Assert.Equal("v1", asked?.Version.Id);

        Assert.True(model.IsInstalled);
        Assert.False(model.CanInstall);
        Assert.Equal("Ready to play.", model.StatusMessage);
        Assert.Null(model.Progress);
    }

    // An older build on disk is an update, not a second install, and it is the same request.
    [Fact]
    public async Task AnOlderBuildOnDiskOffersAnUpdateInsteadOfAnInstall()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild("win-2")));
        AlreadyInstalled(InstalledAt("win-1"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.True(model.IsInstalled);
        Assert.True(model.HasUpdate);
        Assert.True(model.CanUpdate);
        Assert.False(model.CanInstall);
        Assert.True(model.CanUninstall);
        Assert.True(model.CanVerify);
        Assert.Equal("Installed version: 0.2.0", model.InstalledVersion);

        // An update is not optional: playing the old build is what produces the failures that
        // arrive later and look like a broken game.
        Assert.False(model.CanPlay);
        Assert.True(model.MustUpdateBeforePlaying);
    }

    [Fact]
    public async Task ADamagedInstallOffersToRepairItselfAndCannotBeVerifiedAgain()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win", InstallState.Broken));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.True(model.IsBroken);
        Assert.True(model.CanUpdate);
        Assert.False(model.CanInstall);
        Assert.False(model.CanVerify);
    }

    [Fact]
    public async Task RunningOutOfSpaceSaysHowMuchIsMissingRatherThanThatSomethingWentWrong()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        _installations
            .InstallAsync(
                Arg.Any<InstallRequest>(),
                Arg.Any<IProgress<DownloadProgress>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InsufficientDiskSpaceException("/games", 5_368_709_120, 1_073_741_824));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.InstallCommand.ExecuteAsync(null);

        Assert.NotNull(model.ErrorMessage);
        Assert.Contains("5 GB", model.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("1 GB", model.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("/games", model.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(model.Progress);
    }

    [Fact]
    public async Task CancellingStopsTheInstallAndSaysSoRatherThanReportingAFailure()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        _installations
            .InstallAsync(
                Arg.Any<InstallRequest>(),
                Arg.Any<IProgress<DownloadProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return new InstallResult { Install = InstalledAt("win") };
            });

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Task installing = model.InstallCommand.ExecuteAsync(null);
        model.CancelInstallCommand.Execute(null);
        await installing;

        Assert.Equal("Cancelled.", model.ErrorMessage);
        Assert.Null(model.Progress);
        Assert.False(model.IsWorking);
    }

    [Fact]
    public async Task UninstallingSaysWhatItGaveBack()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win"));
        _installations.UninstallAsync("g1", Arg.Any<CancellationToken>())
            .Returns(new UninstallResult("g1", 5_368_709_120));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.UninstallCommand.ExecuteAsync(null);

        Assert.Equal("Uninstalled, freeing 5 GB.", model.StatusMessage);
        Assert.Null(model.Installed);
        Assert.False(model.IsInstalled);
        Assert.True(model.CanInstall);
    }

    [Fact]
    public async Task VerifyingAnIntactInstallSaysSo()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win"));
        _installations.VerifyAsync("g1", Arg.Any<CancellationToken>())
            .Returns(new IntegrityReport { BuildId = "win", Intact = true });

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.VerifyCommand.ExecuteAsync(null);

        Assert.Equal("Everything is where it should be.", model.StatusMessage);
    }

    [Fact]
    public async Task VerifyingADamagedInstallCountsWhatIsWrongWithIt()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win"));
        _installations.VerifyAsync("g1", Arg.Any<CancellationToken>())
            .Returns(new IntegrityReport
            {
                BuildId = "win",
                Intact = false,
                Missing = ["data/pak"],
                Corrupt = ["Game.exe", "lib/core.dll"],
            });

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.VerifyCommand.ExecuteAsync(null);

        Assert.Equal(
            "1 missing and 2 damaged. Install it again to repair it.", model.StatusMessage);
    }

    // The phases with no byte count of their own must not fill a bar: a bar moving during a
    // step that is transferring nothing is a bar that is lying.
    [Theory]
    [InlineData(InstallPhase.Planning, true)]
    [InlineData(InstallPhase.CheckingSpace, true)]
    [InlineData(InstallPhase.Verifying, true)]
    [InlineData(InstallPhase.Downloading, false)]
    [InlineData(InstallPhase.Applying, false)]
    public void OnlyTheStepsThatMoveBytesFillTheBar(InstallPhase phase, bool indeterminate)
    {
        GameDetailViewModel model = CreateViewModel();

        model.Progress = new DownloadProgress { Phase = phase, TotalBytes = 100 };

        Assert.Equal(indeterminate, model.IsProgressIndeterminate);
        Assert.True(model.IsWorking);
    }

    [Fact]
    public void ADownloadInFlightSaysHowFarAlongItIs()
    {
        GameDetailViewModel model = CreateViewModel();

        model.Progress = new DownloadProgress
        {
            Phase = InstallPhase.Downloading,
            TransferredBytes = 1_073_741_824,
            TotalBytes = 5_368_709_120,
        };

        Assert.Equal("Downloading", model.PhaseText);
        Assert.Equal(0.2, model.ProgressFraction, 3);

        // No speed yet, and therefore no estimate: one sample is not a rate.
        Assert.Equal("1 GB of 5 GB", model.ProgressDetail);
    }

    [Fact]
    public void AnEstimateOnlyAppearsOnceThereIsSomethingToBaseItOn()
    {
        var clock = new FakeTimeProvider(Now);
        var runtime = Substitute.For<IRuntimePlatform>();
        runtime.Platform.Returns(GamePlatform.Windows);
        runtime.Architecture.Returns(BuildArchitecture.X64);

        GameDetailViewModel model = new(
            _catalog,
            _library,
            new ApiErrorPresenter(_localization, NullLogger<ApiErrorPresenter>.Instance),
            _localization,
            runtime,
            _authentication,
            _installations,
            _installs,
            _games,
            _images,
            _playback,
            _files,
            _folders,
            _settings,
            clock);

        model.Progress = new DownloadProgress
        {
            Phase = InstallPhase.Downloading,
            TransferredBytes = 0,
            TotalBytes = 20_971_520,
        };
        Assert.DoesNotContain("left", model.ProgressDetail, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(1));
        model.Progress = new DownloadProgress
        {
            Phase = InstallPhase.Downloading,
            TransferredBytes = 10_485_760,
            TotalBytes = 20_971_520,
        };

        // 10 MB in one second, 10 MB to go.
        Assert.Contains("10 MB/s", model.ProgressDetail, StringComparison.Ordinal);
        Assert.Contains("1s left", model.ProgressDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchOptionsAreLoadedFromTheRowAndSavedBackToIt()
    {
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win") with { LaunchOptions = "-windowed" });

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.True(model.CanEditLaunchOptions);
        Assert.Equal("-windowed", model.LaunchOptions);
        Assert.False(model.LaunchOptionsChanged);

        model.LaunchOptions = "  -windowed --dev  ";
        Assert.True(model.LaunchOptionsChanged);

        await model.SaveLaunchOptionsCommand.ExecuteAsync(null);

        Assert.Equal("-windowed --dev", model.Installed?.LaunchOptions);
        await _installs.Received(1).SaveAsync(
            Arg.Any<InstalledGame>(), Arg.Any<CancellationToken>());
        Assert.False(model.LaunchOptionsChanged);
        Assert.Equal("Launch options saved.", model.StatusMessage);
    }

    // Shown so a player can see what their own arguments are being added to.
    [Fact]
    public async Task TheHintNamesTheArgumentsTheBuildAlreadyPasses()
    {
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win") with { LaunchArgs = "--fullscreen" });

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.Equal("--fullscreen", model.BuildLaunchArgs);
        Assert.Contains("--fullscreen", model.LaunchOptionsHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildWithNoArgumentsOfItsOwnSaysSoRatherThanShowingAnEmptyGap()
    {
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.Contains("passes none", model.LaunchOptionsHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGameThatIsNotInstalledHasNoLaunchOptionsToEdit()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.False(model.CanEditLaunchOptions);
        Assert.Empty(model.LaunchOptions);
    }

    [Fact]
    public async Task AnInstalledGameCanBePlayed()
    {
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win"));
        _games.LaunchAsync("g1", Arg.Any<CancellationToken>())
            .Returns(new RunningGame("g1", 4242, Now));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.True(model.CanPlay);
        await model.PlayCommand.ExecuteAsync(null);

        await _games.Received(1).LaunchAsync("g1", Arg.Any<CancellationToken>());
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task AGameThatIsNotInstalledIsNotOfferedAPlayButton()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.False(model.CanPlay);
        Assert.True(model.CanInstall);
    }

    // Uninstalling or verifying a game that is running would pull the files out from under it.
    [Fact]
    public async Task WhileAGameRunsItIsNotOfferedAgainAndCannotBeRemoved()
    {
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win"));
        _games.IsRunning("g1").Returns(true);

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.True(model.IsRunning);
        Assert.False(model.CanPlay);
        Assert.False(model.CanUninstall);
        Assert.False(model.CanVerify);
    }

    [Fact]
    public async Task ARefusedLaunchSaysWhyRatherThanDoingNothing()
    {
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win"));
        _games.LaunchAsync("g1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new GameLaunchException(
                LaunchFailure.EntrypointMissing, "Game.exe is missing from the installation."));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.PlayCommand.ExecuteAsync(null);

        Assert.Equal(
            "The game's executable is missing. Install it again to repair it.",
            model.ErrorMessage);
    }

    // The process exits on a thread that is not the UI's, and the page has to come back to
    // offering Play without being reloaded.
    [Fact]
    public async Task WhenTheGameExitsThePageOffersToPlayItAgain()
    {
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win"));
        _games.IsRunning("g1").Returns(true);

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        Assert.False(model.CanPlay);

        _games.IsRunning("g1").Returns(false);
        _games.GameExited += Raise.EventWith(new GameExitedEventArgs("g1", 0, TimeSpan.FromHours(1)));

        Assert.True(model.CanPlay);
        Assert.False(model.IsRunning);
    }

    // Installing a game is deciding to own it, and a library that did not list it afterwards
    // is the kind of gap people report as a bug.
    [Fact]
    public async Task InstallingAGameAddsItToTheLibrary()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        Installs(InstalledAt("win"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        Assert.False(model.InLibrary);

        await model.InstallCommand.ExecuteAsync(null);

        await _library.Received(1).AddAsync("g1", Arg.Any<CancellationToken>());
        Assert.True(model.InLibrary);
    }

    [Fact]
    public async Task AGameAlreadyInTheLibraryIsNotAddedToItAgain()
    {
        CanDownload();
        Returns(DetailWith(inLibrary: true, builds: WindowsBuild()));
        Installs(InstalledAt("win"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.InstallCommand.ExecuteAsync(null);

        await _library.DidNotReceive().AddAsync("g1", Arg.Any<CancellationToken>());
    }

    // The install really did happen, so it keeps saying so. The failure to record the
    // ownership is its own sentence rather than a replacement for that one.
    [Fact]
    public async Task AFailureToRecordOwnershipDoesNotUndoAFinishedInstall()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        Installs(InstalledAt("win"));
        _library
            .AddAsync("g1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Conflict, "no"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.InstallCommand.ExecuteAsync(null);

        Assert.True(model.IsInstalled);
        Assert.Equal("Ready to play.", model.StatusMessage);
        Assert.False(model.InLibrary);
        Assert.NotNull(model.ErrorMessage);
    }

    // Leaving the library while the files are here would leave an install the account no
    // longer owns, which cannot be updated and cannot be repaired.
    [Fact]
    public async Task AGameCannotLeaveTheLibraryWhileItIsInstalled()
    {
        Returns(DetailWith(inLibrary: true, builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.True(model.InLibrary);
        Assert.False(model.CanRemoveFromLibrary);
        Assert.True(model.CanUninstall);
    }

    [Fact]
    public async Task AGameThatIsNotInstalledCanLeaveTheLibrary()
    {
        Returns(DetailWith(inLibrary: true, builds: WindowsBuild()));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.True(model.CanRemoveFromLibrary);
    }

    [Fact]
    public async Task TheInstallFolderIsOnlyOfferedOnceThereIsOne()
    {
        Returns(DetailWith(builds: WindowsBuild()));
        _files.Reveal(Arg.Any<string>()).Returns(true);

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        Assert.False(model.CanOpenFolder);

        AlreadyInstalled(InstalledAt("win"));
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.True(model.CanOpenFolder);
        model.OpenFolderCommand.Execute(null);

        _files.Received(1).Reveal("/games/orbital-drift");
        Assert.Null(model.ErrorMessage);
    }

    // A desktop with nothing to open a folder with is a button that would otherwise do
    // nothing at all, with no way for anybody to tell whether it had been pressed.
    [Fact]
    public async Task AFolderThatWillNotOpenSaysSo()
    {
        Returns(DetailWith(builds: WindowsBuild()));
        AlreadyInstalled(InstalledAt("win"));
        _files.Reveal(Arg.Any<string>()).Returns(false);

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        model.OpenFolderCommand.Execute(null);

        Assert.Equal("The install folder could not be opened.", model.ErrorMessage);
    }

    // --- where a game is installed ----------------------------------------------------------

    [Fact]
    public async Task ByDefaultNobodyIsAskedWhereToInstall()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        Installs(InstalledAt("win"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.InstallCommand.ExecuteAsync(null);

        await _folders.DidNotReceive().PickAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(LastRequest);
        Assert.Null(LastRequest.InstallRoot);
    }

    // The picked directory is a root the launcher makes the game's own directory inside, never
    // the directory the build is unpacked into: uninstalling deletes that one recursively.
    [Fact]
    public async Task WithTheSettingOnTheChosenDirectoryIsPassedAsARoot()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        Installs(InstalledAt("win"));
        AsksWhereToInstall();
        _folders.PickAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("D:/Games");

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.InstallCommand.ExecuteAsync(null);

        await _folders.Received(1).PickAsync(
            "Where should Orbital Drift be installed?", Arg.Any<CancellationToken>());
        Assert.Equal("D:/Games", LastRequest?.InstallRoot);
        Assert.True(model.IsInstalled);
    }

    // Falling back to the default here would install the game somewhere the player has just
    // declined to confirm.
    [Fact]
    public async Task CancellingTheQuestionCancelsTheInstall()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild()));
        Installs(InstalledAt("win"));
        AsksWhereToInstall();
        _folders.PickAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.InstallCommand.ExecuteAsync(null);

        await _installations.DidNotReceive().InstallAsync(
            Arg.Any<InstallRequest>(),
            Arg.Any<IProgress<DownloadProgress>>(),
            Arg.Any<CancellationToken>());

        Assert.False(model.IsInstalled);
        Assert.Null(model.ErrorMessage);
        Assert.Null(model.Progress);
    }

    // An update goes where the game already lives (D33), so asking again would invite an
    // answer the first install is not in.
    [Fact]
    public async Task AnUpdateNeverAsksWhereTheGameShouldGo()
    {
        CanDownload();
        Returns(DetailWith(builds: WindowsBuild("win-2")));
        AlreadyInstalled(InstalledAt("win-1"));
        Installs(InstalledAt("win-2"));
        AsksWhereToInstall();

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);
        await model.InstallCommand.ExecuteAsync(null);

        await _folders.DidNotReceive().PickAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Null(LastRequest?.InstallRoot);
    }

    private void AsksWhereToInstall() => _settings
        .LoadAsync(Arg.Any<CancellationToken>())
        .Returns(new UserSettings { AskWhereToInstall = true });

    /// <summary>The request the install service was last handed, for asserting on.</summary>
    private InstallRequest? LastRequest { get; set; }

    private void Installs(InstalledGame result) => _installations
        .InstallAsync(
            Arg.Do<InstallRequest>(request => LastRequest = request),
            Arg.Any<IProgress<DownloadProgress>>(),
            Arg.Any<CancellationToken>())
        .Returns(new InstallResult { Install = result });
}
