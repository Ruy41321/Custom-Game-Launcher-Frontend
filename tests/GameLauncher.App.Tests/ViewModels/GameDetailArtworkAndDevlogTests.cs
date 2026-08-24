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

/// <summary>
/// The half of the game page that is pictures and posts. Split from
/// <see cref="GameDetailViewModelTests"/> because it is about what the page shows rather than
/// about what it installs.
/// </summary>
public sealed class GameDetailArtworkAndDevlogTests
{
    private readonly ICatalogApi _catalog = Substitute.For<ICatalogApi>();
    private readonly IImageProvider _images = Substitute.For<IImageProvider>();
    private readonly IVideoPlayback _playback = Substitute.For<IVideoPlayback>();
    private readonly ResourceManagerLocalizationService _localization = new("en");

    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every load asks for the devlog, so an empty one is the default. Arranged in the
    /// constructor rather than in the factory: NSubstitute's last stub wins, and a test that
    /// arranges a devlog does it after this and before building the view model.
    /// </summary>
    public GameDetailArtworkAndDevlogTests() => NoDevlog();

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

    /// <summary>
    /// The install flow reads the preferences, and an unconfigured substitute answers a
    /// <c>Task&lt;UserSettings&gt;</c> with null rather than with defaults — which crashes the
    /// view model rather than failing an assertion.
    /// </summary>
    private static IUserSettingsStore SettingsStore()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new UserSettings());
        return store;
    }

    private static GameMedia Picture(
        MediaKind kind, string id, int sortOrder = 0, string? url = null) => new()
        {
            Id = id,
            GameId = "g1",
            Kind = kind,
            Url = url ?? $"https://files.example/media/{id}.png",
            ContentType = "image/png",
            SortOrder = sortOrder,
            AltText = id,
            CreatedAt = Now,
        };

    private static PatchNote Note(
        string id,
        string title,
        bool published = true,
        string versionId = "") => new()
        {
            Id = id,
            GameId = "g1",
            VersionId = versionId,
            Title = title,
            BodyMarkdown = "Body of " + id,
            Published = published,
            PublishedAt = published
                ? new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)
                : null,
            Author = new Publisher { Id = "u1", DisplayName = "Luigi" },
            CreatedAt = Now,
            UpdatedAt = Now,
        };

    private static GameDetail Detail(params GameMedia[] media) => new()
    {
        Game = new Game { Id = "g1", Slug = "orbital-drift", Title = "Orbital Drift" },
        Versions = [new GameVersion { Id = "v1", Semver = "0.2.0", Published = true }],
        Media = media,
    };

    private void Returns(GameDetail detail) =>
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(detail);

    private void NoDevlog() => Devlog(0);

    private void Devlog(int total, params PatchNote[] notes) =>
        _catalog
            .GetPatchNotesAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<PatchNote>
            {
                Items = notes,
                Total = total,
                Limit = 10,
                Offset = 0,
            });

    // A banner is the wide picture a page like this is for; the cover is the fallback.
    [Fact]
    public async Task TheHeroIsTheBannerWhenThereIsOneAndTheCoverOtherwise()
    {
        Returns(Detail(
            Picture(MediaKind.Cover, "cover"),
            Picture(MediaKind.Banner, "banner")));

        await CreateViewModel().LoadAsync("g1", TestContext.Current.CancellationToken);

        await _images.Received(1).GetAsync(
            "https://files.example/media/banner.png", Arg.Any<CancellationToken>());
        await _images.DidNotReceive().GetAsync(
            "https://files.example/media/cover.png", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNoBannerTheCoverIsUsedInstead()
    {
        Returns(Detail(Picture(MediaKind.Cover, "cover")));

        await CreateViewModel().LoadAsync("g1", TestContext.Current.CancellationToken);

        await _images.Received(1).GetAsync(
            "https://files.example/media/cover.png", Arg.Any<CancellationToken>());
    }

    // A game with no artwork is an ordinary game, not a page with a hole in it.
    [Fact]
    public async Task AGameWithNoArtworkShowsNoHeroAndNoGallery()
    {
        Returns(Detail());

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);

        Assert.False(model.HasHero);
        Assert.False(model.HasScreenshots);
        Assert.Empty(model.Screenshots);
        Assert.Null(model.SelectedScreenshot);
    }

    [Fact]
    public async Task TheGalleryFollowsThePublishersOrderAndOpensOnTheFirstPicture()
    {
        Returns(Detail(
            Picture(MediaKind.Screenshot, "third", sortOrder: 3),
            Picture(MediaKind.Screenshot, "first", sortOrder: 1),
            Picture(MediaKind.Screenshot, "second", sortOrder: 2)));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);

        Assert.True(model.HasScreenshots);
        Assert.Equal(
            [
                "https://files.example/media/first.png",
                "https://files.example/media/second.png",
                "https://files.example/media/third.png",
            ],
            model.Screenshots.Select(shot => shot.Url));

        Assert.Same(model.Screenshots[0], model.SelectedScreenshot);
    }

    [Fact]
    public async Task PickingAThumbnailChangesWhatIsShownLarge()
    {
        Returns(Detail(
            Picture(MediaKind.Screenshot, "first", sortOrder: 1),
            Picture(MediaKind.Screenshot, "second", sortOrder: 2)));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);

        model.ShowScreenshotCommand.Execute(model.Screenshots[1]);

        Assert.Same(model.Screenshots[1], model.SelectedScreenshot);
    }

    [Fact]
    public async Task LoadingAnotherGameLeavesNothingOfTheLastOne()
    {
        Returns(Detail(Picture(MediaKind.Screenshot, "first")));
        Devlog(1, Note("n1", "Patch 0.2.0"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);

        Returns(Detail());
        NoDevlog();
        await model.LoadAsync("g2", TestContext.Current.CancellationToken);

        Assert.Empty(model.Screenshots);
        Assert.Empty(model.Devlog);
        Assert.Null(model.SelectedScreenshot);
        Assert.True(model.DevlogIsEmpty);
    }

    [Fact]
    public async Task TheDevlogIsShownNewestFirstAsTheServerSentIt()
    {
        Returns(Detail());
        Devlog(2, Note("n1", "Patch 0.2.0", versionId: "v1"), Note("n2", "Plans"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(["Patch 0.2.0", "Plans"], model.Devlog.Select(note => note.Title));
        Assert.False(model.DevlogIsEmpty);
        Assert.False(model.HasMoreDevlog);

        // A devlog whose every card is shut is a page that looks like it failed to load, and
        // one where they are all open is the wall of text this replaced.
        Assert.True(model.Devlog[0].IsExpanded);
        Assert.False(model.Devlog[1].IsExpanded);

        model.Devlog[1].ToggleCommand.Execute(null);
        Assert.True(model.Devlog[1].IsExpanded);
        Assert.True(model.Devlog[0].IsExpanded);
    }

    // The body is Markdown and was written as Markdown; a shut card shows a line of it as
    // text rather than as the syntax.
    [Fact]
    public async Task AShutCardPreviewsTheProseRatherThanTheSyntax()
    {
        Returns(Detail());
        Devlog(1, Note("n1", "Release 0.1") with
        {
            BodyMarkdown = "# Ciao a tutti\n## benvenuti\n\nun **saluto** a tutti",
        });

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.Equal("Ciao a tutti", Assert.Single(model.Devlog).Preview);
    }

    // The badge names the version, and the id is never what a reader is shown.
    [Fact]
    public async Task AnEntryAboutAVersionIsLabelledWithItsSemver()
    {
        Returns(Detail());
        Devlog(1, Note("n1", "Patch 0.2.0", versionId: "v1"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);

        PatchNoteCardViewModel card = Assert.Single(model.Devlog);
        Assert.True(card.ShowVersion);
        Assert.Equal("0.2.0", card.VersionLabel);
    }

    // An entry can name a version this account cannot see. No badge beats an unexplained id.
    [Fact]
    public async Task AnEntryAboutAVersionThatIsNotOnThePageShowsNoBadge()
    {
        Returns(Detail());
        Devlog(1, Note("n1", "Patch 9.9.9", versionId: "v-unknown"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(model.Devlog).ShowVersion);
    }

    [Fact]
    public async Task ADraftSaysSoWhereADateWouldBe()
    {
        Returns(Detail());
        Devlog(1, Note("n1", "Not out yet", published: false));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(
            _localization.Translate("Detail.DevlogDraft"),
            Assert.Single(model.Devlog).PublishedOn);
    }

    [Fact]
    public async Task MoreIsOfferedWhileTheServerSaysThereAreMoreAndAppendsThem()
    {
        Returns(Detail());
        Devlog(12, [.. Enumerable.Range(1, 10).Select(n => Note("n" + n, "Post " + n))]);

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);

        Assert.True(model.HasMoreDevlog);

        Devlog(12, Note("n11", "Post 11"), Note("n12", "Post 12"));
        await model.LoadMoreDevlogCommand.ExecuteAsync(null);

        Assert.Equal(12, model.Devlog.Count);
        Assert.False(model.HasMoreDevlog);

        // The second call asks for the second page: the page number follows from what is
        // already on screen, so nothing is ever fetched twice.
        await _catalog.Received(1).GetPatchNotesAsync(
            "g1", 2, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // The devlog is the least important thing on the page and must not take the page with it.
    [Fact]
    public async Task ADevlogThatWillNotLoadLeavesTheRestOfThePageAlone()
    {
        Returns(Detail());
        _catalog
            .GetPatchNotesAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "no"));

        GameDetailViewModel model = CreateViewModel();
        await model.LoadAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(_localization.Translate("Error.Network"), model.DevlogError);
        Assert.Null(model.ErrorMessage);
        Assert.NotNull(model.Detail);
        Assert.Equal("Orbital Drift", model.Title);
    }
}
