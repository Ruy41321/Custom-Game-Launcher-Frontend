using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
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
            _authentication);
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

    // Powers of 1024, because a user comparing against free disk space compares against what
    // their file manager shows.
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_048_576, "1 MB")]
    [InlineData(5_368_709_120, "5 GB")]
    public void SizesAreFormattedTheWayAFileManagerWould(long bytes, string expected)
    {
        // Pinned to the invariant culture: the separator deliberately follows the user's,
        // and this test is about the unit and the rounding, not about where the comma goes.
        Assert.Equal(
            expected,
            GameDetailViewModel.FormatBytes(bytes, System.Globalization.CultureInfo.InvariantCulture));
    }
}
