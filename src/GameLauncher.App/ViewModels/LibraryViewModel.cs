using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.App.Services;
using GameLauncher.Core.Api;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// One game in the library, with what this machine knows about it. The card exists because
/// two different sources have to meet somewhere: the account's ownership, which is the
/// server's, and the installation, which is this disk's.
/// </summary>
public sealed partial class GameCardViewModel(
    Game game,
    InstalledGame? install,
    IGameLauncher games,
    ILocalizationService localization) : GameCoverCardViewModel(game)
{
    [ObservableProperty]
    private InstalledGame? _install = install;

    public bool IsInstalled => Install?.State == InstallState.Installed;

    public bool IsBroken => Install?.State == InstallState.Broken;

    public bool IsRunning => Install is not null && games.IsRunning(Install.GameId);

    public bool CanPlay => IsInstalled && !IsRunning;

    /// <summary>
    /// Deliberately silent about whether an update exists. Knowing that would mean asking the
    /// server for every game's builds to draw one list, and a badge that is sometimes right is
    /// worse than no badge — the game page fetches that and says so there.
    /// </summary>
    public string StatusText => Install switch
    {
        null => string.Empty,
        _ when IsRunning => localization.Translate("Detail.Running"),
        { State: InstallState.Installed } => localization.Translate(
            "Detail.InstalledVersion", Install.VersionSemver),
        { State: InstallState.Broken } => localization.Translate("Library.Damaged"),
        _ => localization.Translate("Library.Unfinished"),
    };

    public bool HasStatus => Install is not null;

    /// <summary>Called when something outside the card changed what it says.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsBroken));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
    }

    partial void OnInstallChanged(InstalledGame? value) => Refresh();
}

/// <summary>
/// The account's games, each with what this machine has of it. Ownership comes from the
/// server; whether it is installed, and whether it is running, does not.
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase
{
    private readonly ILibraryApi _library;
    private readonly IInstallStore _installs;
    private readonly IGameLauncher _games;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;
    private readonly IImageProvider _images;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// The list is what is on this disk rather than what the account owns, because the server
    /// could not be asked.
    /// </summary>
    [ObservableProperty]
    private bool _isOffline;

    private bool _hasLoaded;

    public LibraryViewModel(
        ILibraryApi library,
        IInstallStore installs,
        IGameLauncher games,
        IApiErrorPresenter errors,
        ILocalizationService localization,
        IImageProvider images)
    {
        _library = library;
        _installs = installs;
        _games = games;
        _errors = errors;
        _localization = localization;
        _images = images;

        // A game exits on a thread that is not the UI's, and only its own card changes.
        _games.GameExited += (_, args) => OnUiThread(() =>
        {
            foreach (GameCardViewModel card in Games)
            {
                if (card.Install?.GameId == args.GameId)
                {
                    card.Refresh();
                }
            }
        });
    }

    public event EventHandler<string>? GameSelected;

    public ObservableCollection<GameCardViewModel> Games { get; } = [];

    public bool IsEmpty => !IsBusy && Games.Count == 0 && ErrorMessage is null && _hasLoaded;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsEmpty));

        // Read first and unconditionally: it is the half of the answer that does not need a
        // server, and it is the half that is left when there is none.
        IReadOnlyList<InstalledGame> installed = await _installs
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(true);

        try
        {
            IReadOnlyList<Game> games = await _library
                .GetLibraryAsync(cancellationToken)
                .ConfigureAwait(true);

            Dictionary<string, InstalledGame> byGameId = installed.ToDictionary(
                install => install.GameId, StringComparer.Ordinal);

            Games.Clear();
            foreach (Game game in games)
            {
                byGameId.TryGetValue(game.Id, out InstalledGame? install);
                Games.Add(new GameCardViewModel(game, install, _games, _localization));
            }

            IsOffline = false;
            _hasLoaded = true;
        }
        catch (ApiException exception) when (exception.Code == ApiErrorCode.Network)
        {
            // Unreachable, not refused. What is installed is still installed and still
            // playable, and a launcher that showed an error where the games should be would be
            // useless on a train for no reason.
            ShowInstalledOnly(installed);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
            Games.Clear();
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }

        await LoadCoversAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// After the list is on screen, and never in front of it: the games are the page, and the
    /// pictures are what arrives once the page is already usable. Offline is no different: the
    /// install row carries the URL and the artwork cache answers from disk.
    /// </summary>
    private async Task LoadCoversAsync(CancellationToken cancellationToken)
    {
        foreach (GameCardViewModel card in Games.ToList())
        {
            await card.LoadCoverAsync(_images, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The offline list. The catalog fields the cards would show are not on this machine, so
    /// the card is built from what the install row kept for exactly this moment.
    /// </summary>
    private void ShowInstalledOnly(IReadOnlyList<InstalledGame> installed)
    {
        Games.Clear();
        foreach (InstalledGame install in installed)
        {
            Game game = new()
            {
                Id = install.GameId,
                Slug = install.GameSlug,
                Title = install.GameTitle,

                // The row kept the URL, and the artwork cache is indexed by URL and needs no
                // server. So an offline card asks for its cover exactly as an online one does,
                // and finds it on this disk whenever it has been seen before.
                CoverUrl = install.CoverUrl,
            };

            Games.Add(new GameCardViewModel(game, install, _games, _localization));
        }

        IsOffline = true;
        _hasLoaded = true;
    }

    [RelayCommand]
    private void OpenGame(GameCardViewModel? card)
    {
        if (card is not null)
        {
            GameSelected?.Invoke(
                this, card.Game.Slug.Length > 0 ? card.Game.Slug : card.Game.Id);
        }
    }

    /// <summary>
    /// Starts a game from the list. The install page is where a game is repaired or removed;
    /// here the only action worth one click is the one people came for.
    /// </summary>
    [RelayCommand]
    private async Task PlayAsync(GameCardViewModel? card)
    {
        if (card?.Install is null)
        {
            return;
        }

        ErrorMessage = null;

        try
        {
            await _games.LaunchAsync(card.Install.GameId).ConfigureAwait(true);
            card.Refresh();
        }
        catch (GameLaunchException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(GameCardViewModel? card, CancellationToken cancellationToken)
    {
        if (card is null)
        {
            return;
        }

        try
        {
            await _library.RemoveAsync(card.Game.Id, cancellationToken).ConfigureAwait(true);

            // Removed locally rather than by reloading: the server has confirmed it, and a
            // second round trip would make the list flicker for no new information. The files
            // stay on disk — losing the licence is not the same as asking for the space back.
            Games.Remove(card);
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
    }
}
