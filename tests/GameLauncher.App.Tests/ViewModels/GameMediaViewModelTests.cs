using GameLauncher.App.Services;
using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class GameMediaViewModelTests
{
    private readonly ICatalogApi _catalog = Substitute.For<ICatalogApi>();
    private readonly IPublishingApi _publishing = Substitute.For<IPublishingApi>();
    private readonly IServerCapabilityProvider _capabilities =
        Substitute.For<IServerCapabilityProvider>();
    private readonly IFilePicker _files = Substitute.For<IFilePicker>();
    private readonly ResourceManagerLocalizationService _localization = new("en");

    /// <summary>
    /// Deliberately narrower than the fallback, so every assertion below has to come from what
    /// the *server announced* rather than from a constant that happens to agree with it.
    /// </summary>
    private static readonly ServerCapabilities Narrow = ServerCapabilities.Fallback with
    {
        Media = new MediaCapabilities
        {
            MaxBytes = 1024,
            MaxScreenshotsPerGame = 2,
            MaxAltTextLength = 12,
        },
    };

    private static readonly Game TheGame = new()
    {
        Id = "g1",
        Slug = "orbital-drift",
        Title = "Orbital Drift",
    };

    public GameMediaViewModelTests() =>
        _capabilities.GetAsync(Arg.Any<CancellationToken>()).Returns(Narrow);

    private static byte[] Png(int totalBytes = 32)
    {
        byte[] bytes = new byte[totalBytes];
        ReadOnlySpan<byte> signature =
            [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        return bytes;
    }

    private static GameMedia Shot(string id, int sortOrder, string altText = "") => new()
    {
        Id = id,
        GameId = "g1",
        Kind = MediaKind.Screenshot,
        Url = "http://files.example/media/ab/cd/" + id + ".png",
        AltText = altText,
        SortOrder = sortOrder,
        CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(sortOrder),
    };

    private void HasMedia(params GameMedia[] media) =>
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GameDetail { Game = TheGame, Media = media });

    private GameMediaViewModel CreateViewModel() =>
        new(_catalog,
            _publishing,
            _capabilities,
            new ApiErrorPresenter(_localization),
            _localization,
            _files);

    private async Task<GameMediaViewModel> ShowingAsync(params GameMedia[] media)
    {
        HasMedia(media);
        GameMediaViewModel model = CreateViewModel();
        await model.ShowAsync(TheGame, TestContext.Current.CancellationToken);
        return model;
    }

    private void UserPicks(byte[] content, string name = "shot.png") =>
        _files.PickAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new PickedFile(name, content));

    // --- the limits come from the server -------------------------------------------------------

    // The whole point of reading capabilities: the publisher learns the limit from the page,
    // not from a refusal that arrives after the upload.
    [Fact]
    public async Task TheLimitsAreShownBeforeAFileIsChosen()
    {
        GameMediaViewModel model = await ShowingAsync();

        Assert.Contains("PNG", model.LimitsText, StringComparison.Ordinal);
        Assert.Contains("2", model.LimitsText, StringComparison.Ordinal);
        Assert.Equal(12, model.MaxAltTextLength);
    }

    [Fact]
    public async Task AFileOverTheServersSizeLimitIsRefusedWithoutBeingSent()
    {
        GameMediaViewModel model = await ShowingAsync();
        UserPicks(Png(totalBytes: 1025));

        await model.UploadCommand.ExecuteAsync(null);

        Assert.NotNull(model.ErrorMessage);
        await _publishing.DidNotReceive().UploadMediaAsync(
            Arg.Any<string>(), Arg.Any<MediaUpload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AGalleryAtTheServersCapRefusesAnotherScreenshot()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0), Shot("m2", 1));
        UserPicks(Png());

        await model.UploadCommand.ExecuteAsync(null);

        Assert.NotNull(model.ErrorMessage);
        await _publishing.DidNotReceive().UploadMediaAsync(
            Arg.Any<string>(), Arg.Any<MediaUpload>(), Arg.Any<CancellationToken>());
    }

    // The client refuses what is obviously not one of the server's formats to save a pointless
    // upload. SVG is the case that matters: it is a document that can carry script.
    [Fact]
    public async Task AnSvgIsNeverSent()
    {
        GameMediaViewModel model = await ShowingAsync();
        UserPicks(
            System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"),
            "logo.svg");

        await model.UploadCommand.ExecuteAsync(null);

        Assert.Equal(
            _localization.Translate("Publish.Media.UnsupportedFormat"), model.ErrorMessage);
        await _publishing.DidNotReceive().UploadMediaAsync(
            Arg.Any<string>(), Arg.Any<MediaUpload>(), Arg.Any<CancellationToken>());
    }

    // --- uploading -----------------------------------------------------------------------------

    [Fact]
    public async Task AnAcceptedPictureIsSentWithItsKindAndDescription()
    {
        GameMediaViewModel model = await ShowingAsync();
        UserPicks(Png());

        model.UploadKind = MediaKind.Cover;
        model.UploadAltText = "Key art";

        await model.UploadCommand.ExecuteAsync(null);

        await _publishing.Received(1).UploadMediaAsync(
            "g1",
            Arg.Is<MediaUpload>(upload =>
                upload!.Kind == MediaKind.Cover && upload.AltText == "Key art"),
            Arg.Any<CancellationToken>());

        Assert.Null(model.ErrorMessage);
        Assert.Equal(string.Empty, model.UploadAltText);
    }

    // A new screenshot going to the front would rearrange an order the publisher already chose.
    [Fact]
    public async Task ANewScreenshotGoesToTheEndOfTheGallery()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0), Shot("m2", 4));
        UserPicks(Png());

        // The cap is 2 in this deployment, so widen it for this one case.
        _capabilities.GetAsync(Arg.Any<CancellationToken>())
            .Returns(Narrow with { Media = Narrow.Media with { MaxScreenshotsPerGame = 12 } });
        await model.ShowAsync(TheGame, TestContext.Current.CancellationToken);

        await model.UploadCommand.ExecuteAsync(null);

        await _publishing.Received(1).UploadMediaAsync(
            "g1",
            Arg.Is<MediaUpload>(upload => upload!.SortOrder == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellingThePickerUploadsNothingAndIsNotAnError()
    {
        GameMediaViewModel model = await ShowingAsync();
        _files.PickAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns((PickedFile?)null);

        await model.UploadCommand.ExecuteAsync(null);

        Assert.Null(model.ErrorMessage);
        await _publishing.DidNotReceive().UploadMediaAsync(
            Arg.Any<string>(), Arg.Any<MediaUpload>(), Arg.Any<CancellationToken>());
    }

    // --- describing and reordering -------------------------------------------------------------

    [Fact]
    public async Task TheGalleryIsShownInThePublishersOrder()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m2", 1), Shot("m1", 0));

        Assert.Equal(["m1", "m2"], model.Gallery.Select(media => media.Id));
    }

    [Fact]
    public async Task TheIdentityKindsAreListedApartFromTheGallery()
    {
        GameMediaViewModel model = await ShowingAsync(
            Shot("m1", 0),
            new GameMedia { Id = "c1", Kind = MediaKind.Cover },
            new GameMedia { Id = "b1", Kind = MediaKind.Banner });

        Assert.Equal(["m1"], model.Gallery.Select(media => media.Id));
        Assert.Equal(["c1", "b1"], model.Identity.Select(media => media.Id));
    }

    // Both positions are written: two screenshots left at the default order share a sort order,
    // and nudging only one of them would leave them tied and the swap invisible.
    [Fact]
    public async Task MovingAScreenshotWritesBothPositions()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0), Shot("m2", 1));

        await model.MoveDownCommand.ExecuteAsync(model.Gallery[0]);

        await _publishing.Received(1).UpdateMediaAsync(
            "m1", Arg.Is<MediaChanges>(changes => changes!.SortOrder == 1),
            Arg.Any<CancellationToken>());
        await _publishing.Received(1).UpdateMediaAsync(
            "m2", Arg.Is<MediaChanges>(changes => changes!.SortOrder == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheEndsOfTheGalleryDoNotMove()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0), Shot("m2", 1));

        await model.MoveUpCommand.ExecuteAsync(model.Gallery[0]);
        await model.MoveDownCommand.ExecuteAsync(model.Gallery[1]);

        await _publishing.DidNotReceive().UpdateMediaAsync(
            Arg.Any<string>(), Arg.Any<MediaChanges>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditingADescriptionSendsOnlyTheAltText()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0, "The bridge"));

        model.Selected = model.Gallery[0];
        Assert.Equal("The bridge", model.EditedAltText);

        model.EditedAltText = "The hangar";
        Assert.True(model.CanSaveDescription);

        await model.SaveDescriptionCommand.ExecuteAsync(null);

        await _publishing.Received(1).UpdateMediaAsync(
            "m1",
            Arg.Is<MediaChanges>(changes =>
                changes!.AltText == "The hangar" && changes.SortOrder == null),
            Arg.Any<CancellationToken>());
    }

    // The alt-text limit is the server's on this route too, and reachable without a file.
    [Fact]
    public async Task ADescriptionOverTheServersLimitIsRefusedWithoutBeingSent()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0));

        model.Selected = model.Gallery[0];
        model.EditedAltText = new string('a', 13);

        await model.SaveDescriptionCommand.ExecuteAsync(null);

        Assert.NotNull(model.ErrorMessage);
        await _publishing.DidNotReceive().UpdateMediaAsync(
            Arg.Any<string>(), Arg.Any<MediaChanges>(), Arg.Any<CancellationToken>());
    }

    // --- deleting ------------------------------------------------------------------------------

    [Fact]
    public async Task AskingToDeleteSendsNothingAndSaysWhichPictureGoes()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0, "The bridge"));

        model.AskToDeleteCommand.Execute(model.Gallery[0]);

        Assert.NotNull(model.PendingDeletion);
        Assert.Contains("The bridge", model.PendingDeletion.Prompt, StringComparison.Ordinal);
        await _publishing.DidNotReceive()
            .DeleteMediaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A screenshot with no description still has to be identifiable in the prompt.
    [Fact]
    public async Task APictureWithNoDescriptionIsNamedByItsKind()
    {
        GameMediaViewModel model = await ShowingAsync(
            new GameMedia { Id = "c1", Kind = MediaKind.Cover });

        model.AskToDeleteCommand.Execute(model.Identity[0]);

        Assert.Contains(
            _localization.Translate("Publish.Kind.Cover"),
            model.PendingDeletion!.Prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmingDeletesAndReports()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0, "The bridge"));

        model.AskToDeleteCommand.Execute(model.Gallery[0]);
        await model.ConfirmDeletionCommand.ExecuteAsync(null);

        await _publishing.Received(1).DeleteMediaAsync("m1", Arg.Any<CancellationToken>());
        Assert.Null(model.PendingDeletion);
        Assert.Equal(_localization.Translate("Publish.ImageDeleted"), model.StatusMessage);
    }

    [Fact]
    public async Task ChangingYourMindDeletesNothing()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0));

        model.AskToDeleteCommand.Execute(model.Gallery[0]);
        model.CancelDeletionCommand.Execute(null);

        Assert.Null(model.PendingDeletion);
        await _publishing.DidNotReceive()
            .DeleteMediaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARefusalIsReportedAsUnavailable()
    {
        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0));
        _publishing.DeleteMediaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.NotFound, "gone"));

        model.AskToDeleteCommand.Execute(model.Gallery[0]);
        await model.ConfirmDeletionCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Error.NotFound"), model.ErrorMessage);
    }

    // --- the capabilities document never blocks the page ---------------------------------------

    // The provider never throws by contract, but the page must not depend on that being the
    // *only* thing that could go wrong: an unreachable capabilities route falls back rather
    // than leaving a publisher with no artwork tab.
    [Fact]
    public async Task AServerThatCannotSayItsLimitsStillLetsThePageOpen()
    {
        _capabilities.GetAsync(Arg.Any<CancellationToken>())
            .Returns(ServerCapabilities.Fallback);

        GameMediaViewModel model = await ShowingAsync(Shot("m1", 0));

        Assert.True(model.HasGame);
        Assert.True(model.CanUpload);
        Assert.Equal(
            ServerCapabilities.Fallback.Media.MaxAltTextLength, model.MaxAltTextLength);
    }
}
