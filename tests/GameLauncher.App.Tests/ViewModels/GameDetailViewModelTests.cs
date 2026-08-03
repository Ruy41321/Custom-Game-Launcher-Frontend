using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
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

    private GameDetailViewModel CreateViewModel(
        GamePlatform platform = GamePlatform.Windows,
        BuildArchitecture architecture = BuildArchitecture.X64)
    {
        var runtime = Substitute.For<IRuntimePlatform>();
        runtime.Platform.Returns(platform);
        runtime.Architecture.Returns(architecture);

        return new GameDetailViewModel(
            _catalog,
            _library,
            new ApiErrorPresenter(_localization),
            _localization,
            runtime,
            _authentication,
            _installations,
            _installs,
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
            new GameBuild { Id = "win", Platform = GamePlatform.Windows, Status = BuildStatus.Ready },
            new GameBuild { Id = "lin", Platform = GamePlatform.Linux, Status = BuildStatus.Ready }));

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
            new GameBuild { Id = "win", Platform = GamePlatform.Windows, Status = BuildStatus.Ready }));

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
        Assert.Equal("Installed: 0.2.0", model.InstalledVersion);
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
            new ApiErrorPresenter(_localization),
            _localization,
            runtime,
            _authentication,
            _installations,
            _installs,
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
}
