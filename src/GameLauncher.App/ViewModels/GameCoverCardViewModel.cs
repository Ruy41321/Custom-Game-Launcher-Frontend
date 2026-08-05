using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.App.Services;
using GameLauncher.Core.Models;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// What every card of a game has in common: the game, its cover, and something to show in
/// place of the cover. Explore and the library disagree about the buttons on a card and agree
/// about the picture on it, so the picture lives here and the buttons do not.
/// </summary>
public abstract partial class GameCoverCardViewModel(Game game) : ViewModelBase
{
    [ObservableProperty]
    private Bitmap? _cover;

    public Game Game { get; } = game;

    public string Title => Game.Title;

    public string Summary => Game.Summary;

    public bool HasCover => Cover is not null;

    /// <summary>
    /// Drawn where the cover would be. A publisher who has uploaded nothing still gets a card
    /// that looks like a card, rather than a hole the size of a picture.
    /// </summary>
    public string CoverPlaceholder => Title.Length > 0
        ? Title[..1].ToUpper(CultureInfo.CurrentCulture)
        : "?";

    /// <summary>
    /// Fetched after the list is on screen rather than before it. A cover is worth waiting for
    /// only once the thing it belongs to is already there.
    /// </summary>
    public async Task LoadCoverAsync(
        IImageProvider images, CancellationToken cancellationToken = default) =>
        Cover = await images.GetAsync(Game.CoverUrl, cancellationToken).ConfigureAwait(true);

    partial void OnCoverChanged(Bitmap? value) => OnPropertyChanged(nameof(HasCover));
}
