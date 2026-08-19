using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.App.Services;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Media;
using GameLauncher.Core.Models;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// A game's artwork, from the publisher's side: upload a picture, describe it, move it in the
/// gallery, remove it.
///
/// Three things here are worth more than the code that does them.
///
/// **Every limit comes from the server.** `media.maxBytes`, `maxScreenshotsPerGame` and
/// `maxAltTextLength` are read from <c>GET /api/v1/capabilities</c> (D39) and reach the user as
/// a sentence *before* they choose a file, so a refusal names the number that caused it instead
/// of arriving after the upload. No constant in this file is a limit.
///
/// **The client does not decide what an image is.** It refuses what is obviously not one of the
/// server's formats, to save a pointless upload, and never claims the reverse — the server
/// decides from the same bytes and its answer is the one that counts. SVG is refused on both
/// sides because it is a document that can carry script.
///
/// **A picture is never replaced in place.** There is no route that swaps bytes under an
/// existing id, so changing a cover is uploading a new one; only the description and the
/// position are editable.
///
/// The lists hold <see cref="MediaCardViewModel"/> rather than <see cref="GameMedia"/> because
/// this page used to show a gallery as **alt text and nothing else** — a publisher arranging
/// screenshots was reading their own descriptions and guessing which picture each one was. The
/// cards are the same type the game page uses, so what is arranged here and what a player sees
/// there are decoded by one piece of code.
/// </summary>
public sealed partial class GameMediaViewModel : ViewModelBase
{
    private readonly ICatalogApi _catalog;
    private readonly IPublishingApi _publishing;
    private readonly IServerCapabilityProvider _capabilities;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;
    private readonly IFilePicker _files;
    private readonly IImageProvider _images;

    private Game? _game;
    private MediaCapabilities _limits = ServerCapabilities.Fallback.Media;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private PendingDeletion? _pendingDeletion;

    [ObservableProperty]
    private MediaKind _uploadKind = MediaKind.Screenshot;

    [ObservableProperty]
    private string _uploadAltText = string.Empty;

    [ObservableProperty]
    private MediaCardViewModel? _selected;

    /// <summary>The alt text being edited on <see cref="Selected"/>, before it is saved.</summary>
    [ObservableProperty]
    private string _editedAltText = string.Empty;

    public GameMediaViewModel(
        ICatalogApi catalog,
        IPublishingApi publishing,
        IServerCapabilityProvider capabilities,
        IApiErrorPresenter errors,
        ILocalizationService localization,
        IFilePicker files,
        IImageProvider images)
    {
        _catalog = catalog;
        _publishing = publishing;
        _capabilities = capabilities;
        _errors = errors;
        _localization = localization;
        _files = files;
        _images = images;
    }

    /// <summary>Raised when the artwork changed, so a cover shown elsewhere can be refreshed.</summary>
    public event EventHandler? ArtworkChanged;

    /// <summary>Ordered exactly as the game page orders it, so what is arranged is what is seen.</summary>
    public ObservableCollection<MediaCardViewModel> Gallery { get; } = [];

    public ObservableCollection<MediaCardViewModel> Identity { get; } = [];

    /// <summary>
    /// What may be uploaded here. Video is offered only where the server said it stores one:
    /// a deployment that names no video limits cannot take one, and an option whose only
    /// outcome is a refusal is the dead end D61 removes from buttons.
    /// </summary>
    public IReadOnlyList<MediaKind> Kinds =>
        _limits.SupportsVideo
            ? Enum.GetValues<MediaKind>()
            : [.. Enum.GetValues<MediaKind>().Where(kind => kind != MediaKind.Video)];

    /// <summary>The videos, which are a gallery of their own with a cap of their own.</summary>
    public ObservableCollection<MediaCardViewModel> Videos { get; } = [];

    public bool SupportsVideo => _limits.SupportsVideo;

    public bool UploadIsVideo => UploadKind == MediaKind.Video;

    public bool HasGame => _game is not null;

    public bool HasArtwork => Gallery.Count + Identity.Count + Videos.Count > 0;

    public bool CanUpload => HasGame && !IsBusy && PendingDeletion is null;

    public bool CanSaveDescription =>
        Selected is not null && !IsBusy && PendingDeletion is null
        && !string.Equals(EditedAltText, Selected.AltText, StringComparison.Ordinal);

    /// <summary>
    /// What this deployment accepts, in one sentence, shown before a file is chosen. The whole
    /// point of reading capabilities: a publisher learns the limit from the page rather than
    /// from a refusal.
    /// </summary>
    public string LimitsText => _localization.Translate(
        "Publish.MediaLimits",
        string.Join(", ", _limits.ContentTypes.Select(ShortFormatName)),
        ByteSize.Format(_limits.MaxBytes, CultureInfo.CurrentCulture),
        _limits.MaxScreenshotsPerGame.ToString(CultureInfo.CurrentCulture));

    public string GalleryCountText => _localization.Translate(
        "Publish.GalleryCount",
        Gallery.Count.ToString(CultureInfo.CurrentCulture),
        _limits.MaxScreenshotsPerGame.ToString(CultureInfo.CurrentCulture));

    /// <summary>
    /// The video limits, in their own sentence and shown while video is the chosen kind. Its
    /// own numbers because they are not the picture ones: a publisher told "5 MB" while
    /// choosing a trailer has been told the wrong thing.
    /// </summary>
    public string VideoLimitsText => _limits.SupportsVideo
        ? _localization.Translate(
            "Publish.VideoLimits",
            string.Join(", ", _limits.VideoContentTypes.Select(ShortFormatName)),
            ByteSize.Format(_limits.MaxVideoBytes, CultureInfo.CurrentCulture),
            _limits.MaxVideosPerGame.ToString(CultureInfo.CurrentCulture))
        : string.Empty;

    public string VideoCountText => _localization.Translate(
        "Publish.VideoCount",
        Videos.Count.ToString(CultureInfo.CurrentCulture),
        _limits.MaxVideosPerGame.ToString(CultureInfo.CurrentCulture));

    public int MaxAltTextLength => _limits.MaxAltTextLength;

    /// <summary>
    /// Puts the tab back to "no game", without a request. <see cref="ShowAsync"/> with null
    /// does the same thing and reads the capabilities on the way, which is right when a
    /// publisher deselects a game and wrong when there is no longer an account to ask with.
    /// The limits are left as they are: they describe the deployment, not the game and not
    /// the person, and the next <see cref="ShowAsync"/> refreshes them anyway.
    /// </summary>
    public void Clear()
    {
        _game = null;

        Gallery.Clear();
        Identity.Clear();
        Videos.Clear();
        Selected = null;
        PendingDeletion = null;
        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = false;
        UploadAltText = string.Empty;
        UploadKind = MediaKind.Screenshot;
        EditedAltText = string.Empty;

        RaiseDerived();
    }

    /// <summary>Loads the limits and then the game's pictures. Safe to call with null.</summary>
    public async Task ShowAsync(Game? game, CancellationToken cancellationToken = default)
    {
        _game = game;
        ErrorMessage = null;
        StatusMessage = null;
        PendingDeletion = null;
        Selected = null;

        // Never throws: an unreachable server or one older than the route yields the
        // conservative fallback, because refusing to show a page over a document *about* the
        // page would be worse than the guessing it replaced.
        _limits = (await _capabilities.GetAsync(cancellationToken).ConfigureAwait(true)).Media;

        if (game is null)
        {
            Gallery.Clear();
            Identity.Clear();
            Videos.Clear();
            RaiseDerived();
            return;
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UploadAsync(CancellationToken cancellationToken)
    {
        if (_game is not { } game)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;

        bool video = UploadKind == MediaKind.Video;

        PickedFile? picked = await _files
            .PickAsync(
                _localization.Translate(video ? "Publish.ChooseVideo" : "Publish.ChooseImage"),
                video ? VideoFormats.PickerExtensions : ImageFormats.PickerExtensions,
                cancellationToken)
            .ConfigureAwait(true);

        if (picked is null)
        {
            return;
        }

        // Checked against the numbers this deployment announced, before a byte travels — and for
        // a video that check is the only one that can produce a sentence, because the server's
        // refusal for an oversized body comes from the framework in front of it as a bare 413.
        MediaRejection? rejection = MediaUploadRules.Reject(
            picked.Content,
            UploadKind,
            UploadAltText.Trim(),
            video ? Videos.Count : Gallery.Count,
            _limits);

        if (rejection is not null)
        {
            ErrorMessage = Describe(rejection);
            return;
        }

        IsBusy = true;
        RaiseDerived();

        try
        {
            await _publishing.UploadMediaAsync(
                game.Id,
                new MediaUpload
                {
                    Kind = UploadKind,
                    Content = picked.Content,
                    AltText = UploadAltText.Trim(),
                    // Onto the end of its own gallery: a new screenshot — or a new video —
                    // going to the front would rearrange an order the publisher already chose.
                    SortOrder = UploadKind switch
                    {
                        MediaKind.Screenshot => NextSortOrder(Gallery),
                        MediaKind.Video => NextSortOrder(Videos),
                        _ => 0,
                    },
                },
                cancellationToken).ConfigureAwait(true);

            UploadAltText = string.Empty;
            StatusMessage = _localization.Translate("Publish.ImageUploaded", picked.Name);

            await ReloadAsync(cancellationToken).ConfigureAwait(true);
            ArtworkChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseDerived();
        }
    }

    [RelayCommand]
    private async Task SaveDescriptionAsync(CancellationToken cancellationToken)
    {
        if (Selected is not { } media)
        {
            return;
        }

        // The same limit as an upload, from the same place: editing alt text is its own route,
        // and a second copy of the number is a second place for it to be wrong.
        if (MediaUploadRules.RejectAltText(EditedAltText.Trim(), _limits) is { } rejection)
        {
            ErrorMessage = Describe(rejection);
            return;
        }

        await MutateAsync(
            () => _publishing.UpdateMediaAsync(
                media.Id,
                new MediaChanges { AltText = EditedAltText.Trim() },
                cancellationToken),
            "Publish.ImageUpdated",
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Moves a screenshot one place towards the front. Two arrows rather than a drag: a gallery
    /// is capped at a dozen entries, a swap is two deterministic PATCHes where a drop could
    /// renumber the whole list, and a command is something a test can press.
    /// </summary>
    [RelayCommand]
    private Task MoveUpAsync(MediaCardViewModel card) => SwapAsync(card, -1);

    [RelayCommand]
    private Task MoveDownAsync(MediaCardViewModel card) => SwapAsync(card, +1);

    public bool CanMoveUp(MediaCardViewModel card) => Gallery.IndexOf(card) > 0;

    public bool CanMoveDown(MediaCardViewModel card)
    {
        int index = Gallery.IndexOf(card);
        return index >= 0 && index < Gallery.Count - 1;
    }

    private async Task SwapAsync(MediaCardViewModel card, int direction)
    {
        int index = Gallery.IndexOf(card);
        int target = index + direction;

        if (index < 0 || target < 0 || target >= Gallery.Count || IsBusy)
        {
            return;
        }

        MediaCardViewModel other = Gallery[target];

        // Both positions are written explicitly rather than one of them being nudged: two
        // screenshots left at the default order share a sort order, and moving one "past" the
        // other by arithmetic alone would leave them tied and the swap invisible.
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        RaiseDerived();

        try
        {
            await _publishing.UpdateMediaAsync(
                card.Id, new MediaChanges { SortOrder = target }, CancellationToken.None)
                .ConfigureAwait(true);

            await _publishing.UpdateMediaAsync(
                other.Id, new MediaChanges { SortOrder = index }, CancellationToken.None)
                .ConfigureAwait(true);

            await ReloadAsync(CancellationToken.None).ConfigureAwait(true);
            ArtworkChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseDerived();
        }
    }

    /// <summary>
    /// Arms the deletion. Nothing is sent until <see cref="ConfirmDeletionCommand"/> runs, and
    /// the prompt says which picture goes and that it cannot be brought back.
    /// </summary>
    [RelayCommand]
    private void AskToDelete(MediaCardViewModel card)
    {
        ErrorMessage = null;
        StatusMessage = null;

        PendingDeletion = new PendingDeletion(
            _localization.Translate(
                "Publish.ConfirmDeleteImage",
                card.AltText.Length > 0
                    ? card.AltText
                    : _localization.Translate("Publish.Kind." + card.Kind)),
            async cancellationToken =>
            {
                await _publishing.DeleteMediaAsync(card.Id, cancellationToken)
                    .ConfigureAwait(true);

                if (ReferenceEquals(Selected, card))
                {
                    Selected = null;
                }
            });

        RaiseDerived();
    }

    [RelayCommand]
    private async Task ConfirmDeletionAsync(CancellationToken cancellationToken)
    {
        if (PendingDeletion is not { } deletion)
        {
            return;
        }

        PendingDeletion = null;

        await MutateAsync(
            async () =>
            {
                await deletion.ConfirmAsync(cancellationToken).ConfigureAwait(true);
                return true;
            },
            "Publish.ImageDeleted",
            cancellationToken).ConfigureAwait(true);

        ArtworkChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelDeletion()
    {
        PendingDeletion = null;
        RaiseDerived();
    }

    /// <summary>Runs a write, reports it, and reloads — the shape every mutation here has.</summary>
    private async Task MutateAsync<T>(
        Func<Task<T>> write, string successKey, CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        RaiseDerived();

        try
        {
            await write().ConfigureAwait(true);
            StatusMessage = _localization.Translate(successKey);
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseDerived();
        }
    }

    /// <summary>
    /// Re-reads the game detail, which is where the media list lives. Rereading rather than
    /// patching the local list is what keeps the order the *server* reports — including the
    /// tie-break on upload time — instead of a second ordering invented here.
    ///
    /// The pictures are fetched **after** the rows exist, in the order they are shown: the list
    /// appears at once and fills in, rather than the page waiting on a dozen downloads before
    /// anything is on screen. A picture that never arrives leaves its card without one.
    /// </summary>
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_game is not { } game)
        {
            return;
        }

        try
        {
            GameDetail detail = await _catalog.GetGameAsync(game.Id, cancellationToken)
                .ConfigureAwait(true);

            string? selectedId = Selected?.Id;

            Gallery.Clear();
            foreach (GameMedia media in detail.Screenshots)
            {
                Gallery.Add(new MediaCardViewModel(media));
            }

            Videos.Clear();
            foreach (GameMedia media in detail.Videos)
            {
                Videos.Add(new MediaCardViewModel(media));
            }

            Identity.Clear();
            foreach (MediaKind kind in (MediaKind[])[MediaKind.Cover, MediaKind.Banner, MediaKind.Logo])
            {
                if (detail.Artwork(kind) is { } artwork)
                {
                    Identity.Add(new MediaCardViewModel(artwork));
                }
            }

            Selected = Gallery.Concat(Identity).Concat(Videos)
                .FirstOrDefault(card => string.Equals(card.Id, selectedId, StringComparison.Ordinal));

            RaiseDerived();

            // The videos are in the list because their descriptions are editable and their rows
            // deletable; LoadAsync skips the fetch for them, since a container is not a
            // thumbnail.
            foreach (MediaCardViewModel card in Gallery.Concat(Identity).Concat(Videos).ToList())
            {
                await card.LoadAsync(_images, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            RaiseDerived();
        }
    }

    /// <summary>One past the end, so a new item lands where the publisher expects it.</summary>
    private static int NextSortOrder(ObservableCollection<MediaCardViewModel> list) =>
        list.Count == 0 ? 0 : list.Max(card => card.Media.SortOrder) + 1;

    private string Describe(MediaRejection rejection) => rejection.Reason switch
    {
        MediaFailure.TooLarge or MediaFailure.VideoTooLarge => _localization.Translate(
            "Publish.Media." + rejection.Reason,
            ByteSize.Format(rejection.Limit, CultureInfo.CurrentCulture)),
        MediaFailure.GalleryFull or MediaFailure.VideoGalleryFull
            or MediaFailure.AltTextTooLong => _localization.Translate(
            "Publish.Media." + rejection.Reason,
            rejection.Limit.ToString(CultureInfo.CurrentCulture)),
        _ => _localization.Translate("Publish.Media." + rejection.Reason),
    };

    /// <summary>"image/png" reads as "PNG" to somebody choosing a file.</summary>
    private static string ShortFormatName(string contentType) =>
        contentType.Split('/')[^1].ToUpperInvariant();

    partial void OnUploadKindChanged(MediaKind value) =>
        OnPropertyChanged(nameof(UploadIsVideo));

    partial void OnSelectedChanged(MediaCardViewModel? value)
    {
        EditedAltText = value?.AltText ?? string.Empty;
        RaiseDerived();
    }

    partial void OnEditedAltTextChanged(string value) =>
        OnPropertyChanged(nameof(CanSaveDescription));

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasGame));
        OnPropertyChanged(nameof(HasArtwork));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(CanSaveDescription));
        OnPropertyChanged(nameof(LimitsText));
        OnPropertyChanged(nameof(GalleryCountText));
        OnPropertyChanged(nameof(VideoLimitsText));
        OnPropertyChanged(nameof(VideoCountText));
        OnPropertyChanged(nameof(MaxAltTextLength));
        OnPropertyChanged(nameof(Kinds));
        OnPropertyChanged(nameof(SupportsVideo));
        OnPropertyChanged(nameof(UploadIsVideo));
    }
}
