using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.App.Services;
using GameLauncher.Core.Models;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// One picture belonging to a game, with the decoded bitmap beside the record it came from.
///
/// Its own object because a picture arrives on its own: a list shows the frames it has and
/// fills them in as the rest land, which is why <see cref="LoadAsync"/> is separate from the
/// constructor and why <see cref="HasImage"/> exists at all — a card whose bytes have not
/// arrived, or never will, still has to render as something.
///
/// One type rather than one per screen. The game page and the publisher's dashboard show the
/// same pictures for different reasons, and two view models both meaning "a
/// <see cref="GameMedia"/> and its <see cref="Bitmap"/>" would be the same shape maintained in
/// two places. <see cref="Media"/> is exposed because the dashboard's commands act on the
/// record — the page only ever looks at it.
/// </summary>
public sealed partial class MediaCardViewModel(GameMedia media) : ViewModelBase
{
    [ObservableProperty]
    private Bitmap? _image;

    public GameMedia Media { get; } = media;

    public string Id => Media.Id;

    public string Url => Media.Url;

    public MediaKind Kind => Media.Kind;

    /// <summary>The publisher's description, which is what a screen reader reads out.</summary>
    public string AltText => Media.AltText;

    public bool HasImage => Image is not null;

    /// <summary>
    /// Whether this card is a moving picture. The one thing a caller has to switch on: a video
    /// is played rather than decoded, and its URL must never reach an image decoder — which is
    /// what <see cref="LoadAsync"/> would otherwise do to it.
    /// </summary>
    public bool IsVideo => Media.Kind == MediaKind.Video;

    /// <summary>
    /// Fetched after the list is on screen rather than before it, and never throwing: an
    /// unreachable picture leaves <see cref="Image"/> null and the placeholder in its place,
    /// because a gallery that failed to render one thumbnail is not a page that failed.
    ///
    /// A video is skipped rather than special-cased at every call site. There is no thumbnail
    /// for one — the server stores the container and nothing else, and asking a decoder to open
    /// an MP4 would spend a download to be told no. Its card shows a frame and its description
    /// until somebody presses play.
    /// </summary>
    public async Task LoadAsync(
        IImageProvider images, CancellationToken cancellationToken = default)
    {
        if (IsVideo)
        {
            return;
        }

        Image = await images.GetAsync(Media.Url, cancellationToken).ConfigureAwait(true);
    }

    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));
}
