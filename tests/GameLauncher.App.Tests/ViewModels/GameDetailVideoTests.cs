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

namespace GameLauncher.App.Tests.ViewModels;

/// <summary>
/// Playing a trailer on the game page. Everything here is the state machine around playback —
/// whether a picture actually appears is the one thing no test in this repository can see, so
/// it is checked by hand in the window and what is pinned here is everything else:
///
/// <list type="bullet">
/// <item>nothing is asked of a machine that cannot play;</item>
/// <item>a refusal produces a sentence rather than silence;</item>
/// <item>and every way of leaving the page stops the sound.</item>
/// </list>
/// </summary>
public sealed class GameDetailVideoTests
{
    private readonly ICatalogApi _catalog = Substitute.For<ICatalogApi>();
    private readonly IImageProvider _images = Substitute.For<IImageProvider>();
    private readonly IVideoPlayback _playback = Substitute.For<IVideoPlayback>();
    private readonly ResourceManagerLocalizationService _localization = new("en");

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every load asks for the devlog and for the settings, and both are arranged here rather
    /// than in the factory: an unconfigured Task-returning substitute member hands back null,
    /// which dies inside a command instead of failing an assertion.
    /// </summary>
    public GameDetailVideoTests()
    {
        _catalog
            .GetPatchNotesAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<PatchNote>());

        // The ordinary machine for these tests is one that can play.
        _playback.IsAvailable.Returns(true);
        _playback.Play(Arg.Any<string>()).Returns(true);
    }

    private static GameMedia Video(string id, int sortOrder = 0) => new()
    {
        Id = id,
        GameId = "g1",
        Kind = MediaKind.Video,
        ContentType = "video/mp4",
        Url = "https://files.example/media/" + id + ".mp4",
        AltText = id,
        SortOrder = sortOrder,
        CreatedAt = Now,
    };

    private static GameMedia Shot(string id) => new()
    {
        Id = id,
        GameId = "g1",
        Kind = MediaKind.Screenshot,
        Url = "https://files.example/media/" + id + ".png",
        ContentType = "image/png",
        CreatedAt = Now,
    };

    private static IUserSettingsStore SettingsStore()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new UserSettings());
        return store;
    }

    private GameDetailViewModel CreateViewModel()
    {
        var runtime = Substitute.For<IRuntimePlatform>();
        runtime.Platform.Returns(GamePlatform.Windows);
        runtime.Architecture.Returns(BuildArchitecture.X64);

        return new GameDetailViewModel(
            _catalog,
            Substitute.For<ILibraryApi>(),
            new ApiErrorPresenter(_localization, NullLogger<ApiErrorPresenter>.Instance),
            _localization,
            runtime,
            Substitute.For<IAuthenticationService>(),
            Substitute.For<IInstallationService>(),
            Substitute.For<IInstallStore>(),
            Substitute.For<IGameLauncher>(),
            _images,
            _playback,
            Substitute.For<IFileBrowser>(),
            Substitute.For<IFolderPicker>(),
            SettingsStore(),
            new FakeTimeProvider(Now));
    }

    private async Task<GameDetailViewModel> ShowingAsync(params GameMedia[] media)
    {
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GameDetail
            {
                Game = new Game { Id = "g1", Slug = "orbital-drift", Title = "Orbital Drift" },
                Media = media,
            });

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);
        return model;
    }

    // --- what the page shows -------------------------------------------------------------------

    [Fact]
    public async Task TheVideosAreTheirOwnStripAndNoneOfThemIsFetchedAsAPicture()
    {
        GameDetailViewModel model = await ShowingAsync(Video("clip"), Shot("shot"));

        Assert.Equal(["clip"], model.Videos.Select(card => card.Id));
        Assert.Equal(["shot"], model.Screenshots.Select(card => card.Id));
        Assert.True(model.HasVideos);

        await _images.DidNotReceive().GetAsync(
            "https://files.example/media/clip.mp4", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AGameWithNoVideosSaysSo() =>
        Assert.False((await ShowingAsync(Shot("shot"))).HasVideos);

    // --- playing -------------------------------------------------------------------------------

    [Fact]
    public async Task PressingPlayStartsThatVideosUrl()
    {
        GameDetailViewModel model = await ShowingAsync(Video("clip"));

        model.PlayVideoCommand.Execute(model.Videos[0]);

        _playback.Received(1).Play("https://files.example/media/clip.mp4");
        Assert.True(model.IsPlayingVideo);
        Assert.Null(model.VideoError);
    }

    /// <summary>
    /// One player, one video. Pressing a second card replaces the first rather than stacking
    /// two — which is what <see cref="IVideoPlayback"/> having no notion of "which" enforces.
    /// </summary>
    [Fact]
    public async Task ASecondPressReplacesWhatWasPlaying()
    {
        GameDetailViewModel model = await ShowingAsync(Video("first"), Video("second", 1));

        model.PlayVideoCommand.Execute(model.Videos[0]);
        model.PlayVideoCommand.Execute(model.Videos[1]);

        Assert.Equal("second", model.PlayingVideo?.Id);
        _playback.Received(1).Play("https://files.example/media/second.mp4");
    }

    /// <summary>
    /// A machine with no libvlc — every Linux box without VLC installed, and any Windows one
    /// where the native package did not unpack. It is an ordinary outcome, so the page says so
    /// and stays usable rather than reporting a failure.
    /// </summary>
    [Fact]
    public async Task AMachineThatCannotPlayIsToldSoAndIsNeverAsked()
    {
        _playback.IsAvailable.Returns(false);
        GameDetailViewModel model = await ShowingAsync(Video("clip"));

        model.PlayVideoCommand.Execute(model.Videos[0]);

        Assert.False(model.CanPlayVideo);
        Assert.False(model.IsPlayingVideo);
        Assert.NotNull(model.VideoError);
        _playback.DidNotReceive().Play(Arg.Any<string>());
    }

    [Fact]
    public async Task AVideoThatWillNotStartLeavesASentenceRatherThanASilentEmptyFrame()
    {
        _playback.Play(Arg.Any<string>()).Returns(false);
        GameDetailViewModel model = await ShowingAsync(Video("clip"));

        model.PlayVideoCommand.Execute(model.Videos[0]);

        Assert.False(model.IsPlayingVideo);
        Assert.NotNull(model.VideoError);
    }

    /// <summary>A screenshot is not a video, and pressing play on one must ask for nothing.</summary>
    [Fact]
    public async Task NothingButAVideoCanBePlayed()
    {
        GameDetailViewModel model = await ShowingAsync(Shot("shot"), Video("clip"));

        model.PlayVideoCommand.Execute(model.Screenshots[0]);
        model.PlayVideoCommand.Execute(null);

        _playback.DidNotReceive().Play(Arg.Any<string>());
        Assert.False(model.IsPlayingVideo);
    }

    // --- and every way of leaving --------------------------------------------------------------

    [Fact]
    public async Task StopEndsIt()
    {
        GameDetailViewModel model = await ShowingAsync(Video("clip"));
        model.PlayVideoCommand.Execute(model.Videos[0]);

        model.StopVideoCommand.Execute(null);

        _playback.Received().StopPlayback();
        Assert.False(model.IsPlayingVideo);
    }

    /// <summary>Going back stops the sound. Nothing else would: the page outlives the visit.</summary>
    [Fact]
    public async Task GoingBackStopsIt()
    {
        GameDetailViewModel model = await ShowingAsync(Video("clip"));
        model.PlayVideoCommand.Execute(model.Videos[0]);

        model.BackCommand.Execute(null);

        _playback.Received().StopPlayback();
        Assert.False(model.IsPlayingVideo);
    }

    /// <summary>Opening another game stops the previous one's trailer.</summary>
    [Fact]
    public async Task LoadingAnotherGameStopsIt()
    {
        GameDetailViewModel model = await ShowingAsync(Video("clip"));
        model.PlayVideoCommand.Execute(model.Videos[0]);

        await model.LoadAsync("g2", TestContext.Current.CancellationToken);

        _playback.Received().StopPlayback();
        Assert.False(model.IsPlayingVideo);
    }

    /// <summary>
    /// And so does losing the account (D70). A trailer playing over somebody else's session is
    /// the sharpest version of the state this page is not allowed to keep.
    /// </summary>
    [Fact]
    public async Task ChangingAccountStopsItAndEmptiesTheList()
    {
        GameDetailViewModel model = await ShowingAsync(Video("clip"));
        model.PlayVideoCommand.Execute(model.Videos[0]);

        model.ResetForAccountChange();

        _playback.Received().StopPlayback();
        Assert.False(model.IsPlayingVideo);
        Assert.Empty(model.Videos);
    }
}
