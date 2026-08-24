using System.Text;
using GameLauncher.App.Services;
using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GameLauncher.App.Tests.ViewModels;

/// <summary>
/// Uploading a video from the dashboard. Its own class because it needs a different set of
/// announced limits, and because the interesting cases are all about the two galleries being
/// counted and measured separately.
/// </summary>
public sealed class GameMediaVideoTests
{
    private readonly ICatalogApi _catalog = Substitute.For<ICatalogApi>();
    private readonly IPublishingApi _publishing = Substitute.For<IPublishingApi>();
    private readonly IServerCapabilityProvider _capabilities =
        Substitute.For<IServerCapabilityProvider>();
    private readonly IFilePicker _files = Substitute.For<IFilePicker>();
    private readonly IImageProvider _images = Substitute.For<IImageProvider>();
    private readonly ResourceManagerLocalizationService _localization = new("en");

    /// <summary>A deployment that stores video, with numbers that are nobody's default.</summary>
    private static readonly ServerCapabilities WithVideo = ServerCapabilities.Fallback with
    {
        Media = new MediaCapabilities
        {
            MaxBytes = 1024,
            MaxScreenshotsPerGame = 2,
            MaxAltTextLength = 12,
            MaxVideoBytes = 4096,
            MaxVideosPerGame = 2,
            VideoContentTypes = ["video/mp4", "video/webm"],
        },
    };

    /// <summary>A server that says nothing about video, which is how one that has none answers.</summary>
    private static readonly ServerCapabilities WithoutVideo = ServerCapabilities.Fallback with
    {
        Media = new MediaCapabilities { MaxBytes = 1024, MaxScreenshotsPerGame = 2 },
    };

    private static readonly Game TheGame = new() { Id = "g1", Slug = "orbital-drift", Title = "Orbital Drift" };

    /// <summary>
    /// Arranged in the constructor, before any view model exists: NSubstitute's last stub wins,
    /// and a test that wants the other deployment says so afterwards.
    /// </summary>
    public GameMediaVideoTests() =>
        _capabilities.GetAsync(Arg.Any<CancellationToken>()).Returns(WithVideo);

    private static byte[] Mp4(int totalBytes = 64)
    {
        byte[] bytes = new byte[totalBytes];
        Encoding.ASCII.GetBytes("....ftypisom").CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Png(int totalBytes = 32)
    {
        byte[] bytes = new byte[totalBytes];
        ReadOnlySpan<byte> signature =
            [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        return bytes;
    }

    private static GameMedia Video(string id, int sortOrder = 0, string altText = "") => new()
    {
        Id = id,
        GameId = "g1",
        Kind = MediaKind.Video,
        ContentType = "video/mp4",
        Url = "http://files.example/media/ab/cd/" + id + ".mp4",
        AltText = altText,
        SortOrder = sortOrder,
        CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(sortOrder),
    };

    private static GameMedia Shot(string id, int sortOrder = 0) => new()
    {
        Id = id,
        GameId = "g1",
        Kind = MediaKind.Screenshot,
        Url = "http://files.example/media/ab/cd/" + id + ".png",
        SortOrder = sortOrder,
        CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(sortOrder),
    };

    private GameMediaViewModel CreateViewModel() =>
        new(_catalog,
            _publishing,
            _capabilities,
            new ApiErrorPresenter(_localization, NullLogger<ApiErrorPresenter>.Instance),
            _localization,
            _files,
            _images);

    private async Task<GameMediaViewModel> ShowingAsync(params GameMedia[] media)
    {
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GameDetail { Game = TheGame, Media = media });

        GameMediaViewModel model = CreateViewModel();
        await model.ShowAsync(TheGame, TestContext.Current.CancellationToken);
        return model;
    }

    private void UserPicks(byte[] content, string name = "trailer.mp4") =>
        _files.PickAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new PickedFile(name, content));

    // --- what the deployment says it can hold --------------------------------------------------

    [Fact]
    public async Task VideoIsOfferedOnlyWhereTheServerSaidItStoresOne()
    {
        GameMediaViewModel model = await ShowingAsync();
        Assert.Contains(MediaKind.Video, model.Kinds);
        Assert.True(model.SupportsVideo);

        _capabilities.GetAsync(Arg.Any<CancellationToken>()).Returns(WithoutVideo);
        GameMediaViewModel silent = await ShowingAsync();

        Assert.DoesNotContain(MediaKind.Video, silent.Kinds);
        Assert.False(silent.SupportsVideo);
        Assert.Equal(string.Empty, silent.VideoLimitsText);
    }

    /// <summary>
    /// The video sentence carries the video numbers. A publisher choosing a trailer and reading
    /// "up to 1 kB" has been told the picture limit, which is the mistake this prevents.
    /// </summary>
    [Fact]
    public async Task TheVideoLimitsAreItsOwnNumbers()
    {
        GameMediaViewModel model = await ShowingAsync();

        Assert.Contains("MP4", model.VideoLimitsText, StringComparison.Ordinal);
        Assert.Contains("2", model.VideoLimitsText, StringComparison.Ordinal);
        Assert.DoesNotContain("MP4", model.LimitsText, StringComparison.Ordinal);
    }

    // --- uploading -----------------------------------------------------------------------------

    [Fact]
    public async Task AVideoIsSentWithItsKindAndLandsAtTheEndOfItsOwnGallery()
    {
        GameMediaViewModel model = await ShowingAsync(Video("v1", sortOrder: 4), Shot("s1", 0));
        model.UploadKind = MediaKind.Video;
        UserPicks(Mp4());

        await model.UploadCommand.ExecuteAsync(null);

        await _publishing.Received(1).UploadMediaAsync(
            "g1",
            Arg.Is<MediaUpload>(upload =>
                upload != null && upload.Kind == MediaKind.Video && upload.SortOrder == 5),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The picker offers the containers rather than the picture formats. Offering "png" while
    /// choosing a trailer is an invitation to waste an upload.
    /// </summary>
    [Fact]
    public async Task ThePickerOffersVideoExtensionsWhenAVideoIsBeingChosen()
    {
        GameMediaViewModel model = await ShowingAsync();
        model.UploadKind = MediaKind.Video;
        UserPicks(Mp4());

        await model.UploadCommand.ExecuteAsync(null);

        await _files.Received(1).PickAsync(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(extensions =>
                extensions != null && extensions.Contains("mp4")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The refusal that matters most, because past this size the server never sends a sentence:
    /// the framework in front of it answers a bare 413 with no problem document in it.
    /// </summary>
    [Fact]
    public async Task AVideoOverTheServersLimitIsRefusedWithoutBeingSent()
    {
        GameMediaViewModel model = await ShowingAsync();
        model.UploadKind = MediaKind.Video;
        UserPicks(Mp4(totalBytes: 4097));

        await model.UploadCommand.ExecuteAsync(null);

        Assert.NotNull(model.ErrorMessage);
        await _publishing.DidNotReceive().UploadMediaAsync(
            Arg.Any<string>(), Arg.Any<MediaUpload>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// And the other direction: a body too large for a picture is fine as a video, from the same
    /// announced limits. This is the assertion that would fail if one number served both.
    /// </summary>
    [Fact]
    public async Task ABodyTooLargeForAPictureIsAcceptedAsAVideo()
    {
        GameMediaViewModel model = await ShowingAsync();
        model.UploadKind = MediaKind.Video;
        UserPicks(Mp4(totalBytes: 2048));

        await model.UploadCommand.ExecuteAsync(null);

        Assert.Null(model.ErrorMessage);
        await _publishing.Received(1).UploadMediaAsync(
            "g1", Arg.Any<MediaUpload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APictureChosenAsAVideoIsRefusedBeforeItTravels()
    {
        GameMediaViewModel model = await ShowingAsync();
        model.UploadKind = MediaKind.Video;
        UserPicks(Png(), "not-really.mp4");

        await model.UploadCommand.ExecuteAsync(null);

        Assert.NotNull(model.ErrorMessage);
        await _publishing.DidNotReceive().UploadMediaAsync(
            Arg.Any<string>(), Arg.Any<MediaUpload>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Two galleries, two caps. A game at its video cap can still take a screenshot, which is
    /// what counting them separately buys.
    /// </summary>
    [Fact]
    public async Task TheVideoCapIsCountedApartFromTheScreenshotOne()
    {
        GameMediaViewModel model = await ShowingAsync(Video("v1"), Video("v2"));
        model.UploadKind = MediaKind.Video;
        UserPicks(Mp4());

        await model.UploadCommand.ExecuteAsync(null);

        Assert.NotNull(model.ErrorMessage);
        await _publishing.DidNotReceive().UploadMediaAsync(
            Arg.Any<string>(), Arg.Any<MediaUpload>(), Arg.Any<CancellationToken>());

        model.ErrorMessage = null;
        model.UploadKind = MediaKind.Screenshot;
        UserPicks(Png(), "shot.png");

        await model.UploadCommand.ExecuteAsync(null);

        Assert.Null(model.ErrorMessage);
        await _publishing.Received(1).UploadMediaAsync(
            "g1", Arg.Any<MediaUpload>(), Arg.Any<CancellationToken>());
    }

    // --- what the list shows -------------------------------------------------------------------

    [Fact]
    public async Task TheVideosAreTheirOwnListAndNeverReachTheImageDecoder()
    {
        GameMediaViewModel model = await ShowingAsync(Video("v1"), Shot("s1"));

        Assert.Equal(["v1"], model.Videos.Select(card => card.Id));
        Assert.Equal(["s1"], model.Gallery.Select(card => card.Id));
        Assert.True(model.Videos[0].IsVideo);

        // A container is not a thumbnail: asking a decoder to open one spends a download to be
        // told no.
        await _images.DidNotReceive().GetAsync(
            "http://files.example/media/ab/cd/v1.mp4", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AGameWithOnlyAVideoStillCountsAsHavingArtwork()
    {
        GameMediaViewModel model = await ShowingAsync(Video("v1"));

        Assert.True(model.HasArtwork);
        Assert.Contains("1", model.VideoCountText, StringComparison.Ordinal);
    }
}
