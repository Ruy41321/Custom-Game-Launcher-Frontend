using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.App.Services;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;

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

    /// <summary>
    /// Whether the newest build for this machine is one this install does not have. Set by the
    /// list after the cards are on screen (see <c>LibraryViewModel.CheckForUpdatesAsync</c>),
    /// and false until then: the card is drawn before the answer exists and must not offer
    /// Play and take it away again, so a card that has not been checked yet shows nothing at
    /// all where the button goes.
    /// </summary>
    [ObservableProperty]
    private bool _hasUpdate;

    /// <summary>
    /// The same rule as the game page (D61), now that the card can know: an update is not
    /// optional, so Play is absent rather than greyed out, and
    /// <see cref="MustUpdateBeforePlaying"/> puts a sentence where it was.
    ///
    /// It is <b>a known update</b> that takes the button away, not the absence of an answer.
    /// The card is drawn before the check comes back, and offline no check is made at all —
    /// and a launcher that would not start a game already on this disk because it could not
    /// reach a server is the thing the offline library exists to avoid. So the button is there
    /// until something says otherwise, and what it costs is a card that can offer Play for the
    /// length of one request and then withdraw it.
    /// </summary>
    public bool CanPlay => IsInstalled && !IsRunning && !HasUpdate;

    /// <summary>
    /// Whether the game's own page is worth offering. It is not, offline: that page is built
    /// from the catalog and there is no server to answer it, so what a press produces is an
    /// error — and with nobody signed in it is the *wrong* error, because "your session has
    /// expired" is what a page says when a request came back 401, and no session ever existed
    /// here. D61's rule about a button that only leads to a refusal, applied to a page.
    ///
    /// Set by the list, like <see cref="HasUpdate"/>, and true until it says otherwise.
    /// </summary>
    [ObservableProperty]
    private bool _canOpenDetails = true;

    /// <summary>Shown exactly where the Play button would have been, as on the game page.</summary>
    public bool MustUpdateBeforePlaying => IsInstalled && !IsRunning && HasUpdate;

    /// <summary>
    /// Same rule as the game page: a game cannot leave the library while its files are on this
    /// disk, because what is left is an install the account no longer owns and cannot update.
    /// </summary>
    public bool CanRemove => Install is null;

    /// <summary>
    /// What this machine has, and not whether it is the newest: the version is a fact the
    /// install row already holds, while "an update exists" is an answer that arrives later and
    /// belongs where it changes something, which is the Play button.
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
        OnPropertyChanged(nameof(MustUpdateBeforePlaying));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
    }

    partial void OnInstallChanged(InstalledGame? value) => Refresh();

    partial void OnHasUpdateChanged(bool value) => Refresh();
}

/// <summary>
/// The account's games, each with what this machine has of it. Ownership comes from the
/// server; whether it is installed, and whether it is running, does not.
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase, IAccountScopedPage
{
    private readonly ILibraryApi _library;
    private readonly ILibraryCache _cache;
    private readonly IAuthenticationService _authentication;
    private readonly IServerReachability _reachability;
    private readonly ICatalogApi _catalog;
    private readonly IRuntimePlatform _platform;
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

    /// <summary>
    /// Offline **and** with nobody signed in, which is a different sentence: this is not a
    /// library that could not be refreshed, it is the games on this computer and no claim
    /// about what the account owns.
    /// </summary>
    public bool IsOfflineWithoutAccount => IsOffline && !_authentication.IsAuthenticated;

    /// <summary>Offline with a session: the list is the account's, as last seen.</summary>
    public bool IsOfflineWithAccount => IsOffline && _authentication.IsAuthenticated;

    public LibraryViewModel(
        ILibraryApi library,
        ILibraryCache cache,
        IAuthenticationService authentication,
        IServerReachability reachability,
        ICatalogApi catalog,
        IRuntimePlatform platform,
        IInstallStore installs,
        IGameLauncher games,
        IApiErrorPresenter errors,
        ILocalizationService localization,
        IImageProvider images)
    {
        _library = library;
        _cache = cache;
        _authentication = authentication;
        _reachability = reachability;
        _catalog = catalog;
        _platform = platform;
        _installs = installs;
        _games = games;
        _errors = errors;
        _localization = localization;
        _images = images;

        // The server came back while somebody was looking at an offline library. Reloading is
        // what they would do themselves, and doing it for them is the difference between a
        // launcher that recovers and one that has to be restarted. Only from the offline state:
        // a page that is already showing the server's answer has nothing to gain from a reload
        // it did not ask for. The event arrives on whatever thread finished a request (D73).
        _reachability.Changed += (_, args) => OnUiThread(() =>
        {
            if (args.IsOnline && IsOffline)
            {
                LoadCommand.Execute(null);
            }
        });

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

        // Nobody signed in: the launcher is being used offline on purpose, and there is no
        // account to ask about. What is on this disk was paid for and downloaded already, so it
        // is shown and played; the banner says the rest of the library is not here.
        if (!_authentication.IsAuthenticated)
        {
            ShowInstalledOnly(installed);

            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));

            await LoadCoversAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        try
        {
            IReadOnlyList<Game> games = await _library
                .GetLibraryAsync(cancellationToken)
                .ConfigureAwait(true);

            Show(games, installed, offline: false);

            // Kept for the next start that has no server. Written after the page is built, so
            // a disk that refuses the write costs nothing anybody is waiting for.
            await _cache
                .WriteAsync(AccountId, games, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (ApiException exception) when (exception.Code == ApiErrorCode.Network)
        {
            // Unreachable, not refused. What is installed is still installed and still
            // playable, and a launcher that showed an error where the games should be would be
            // useless on a train for no reason.
            await ShowLastKnownAsync(installed, cancellationToken).ConfigureAwait(true);
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
        await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(true);
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
    /// Asks, for every card that has something installed, whether this machine's newest build
    /// is the one on the disk — which is what lets a library card hide Play the way the game
    /// page does (D61 superseded, see D69).
    ///
    /// The cost is **one request per installed game**, and it is the shape this page already
    /// uses for covers: issued after the list is on screen, in the order the cards are shown,
    /// so nothing waits on it. One per *installed* game rather than one per row is the part
    /// worth stating — a library grows with everything an account has ever been given, while
    /// what is installed is bounded by the disk, and a card with nothing on this machine has
    /// no Play button to take away.
    ///
    /// Every failure leaves the card exactly as it was, which leaves Play where it was, and
    /// says nothing out loud. A refusal here is nearly always no server to ask, and refusing
    /// to start a game already on this disk over an unanswered question is the behaviour the
    /// offline library exists to prevent — a row that could not be checked is not a library
    /// that failed to load.
    /// </summary>
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (IsOffline)
        {
            return;
        }

        foreach (GameCardViewModel card in Games.ToList())
        {
            if (card.Install is not { State: InstallState.Installed } install)
            {
                continue;
            }

            try
            {
                GameDetail detail = await _catalog
                    .GetGameAsync(install.GameId, cancellationToken)
                    .ConfigureAwait(true);

                GameBuild? newest = detail.BuildFor(_platform.Platform, _platform.Architecture);

                // No build for this machine is not an update: the publisher has shipped
                // nothing here since, and what is installed stays playable.
                card.HasUpdate = newest is not null && !install.Is(newest.Id);
            }
            catch (ApiException)
            {
                // Unreachable, refused, or a game that is no longer served to this account.
                // The card keeps what it had and offers no Play, and nothing is said out loud:
                // a row that could not be checked is not a library that failed to load.
            }
        }
    }

    /// <summary>
    /// The offline list: what the account owned the last time a server said so, plus anything
    /// installed here that the stored answer does not mention.
    ///
    /// The cache is what makes this the library rather than a corner of it. Falling back to the
    /// install rows alone showed nothing at all to somebody who had not downloaded a game yet,
    /// and silently dropped every title an account owns and has not installed on this machine
    /// — which, for most people, is most of them.
    /// </summary>
    private async Task ShowLastKnownAsync(
        IReadOnlyList<InstalledGame> installed, CancellationToken cancellationToken)
    {
        IReadOnlyList<Game> remembered = await _cache
            .ReadAsync(AccountId, cancellationToken)
            .ConfigureAwait(true);

        // Anything on this disk the stored answer does not name is still shown: a game
        // installed since the last successful load is a game that plays perfectly well, and a
        // library that hid it would be hiding the one thing this page is for.
        HashSet<string> known = [.. remembered.Select(game => game.Id)];
        List<Game> lastKnown =
        [
            .. remembered,
            .. installed
                .Where(install => !known.Contains(install.GameId))
                .Select(FromInstall),
        ];

        Show(lastKnown, installed, offline: true);
    }

    /// <summary>
    /// The disk and nothing else. It is what a signed-out offline visit shows: the stored
    /// library belongs to an account, and handing the last person's list to whoever opens the
    /// launcher next is not something an unreachable server excuses.
    /// </summary>
    private void ShowInstalledOnly(IReadOnlyList<InstalledGame> installed)
    {
        Show([.. installed.Select(FromInstall)], installed, offline: true);
    }

    /// <summary>
    /// The card for a game only this disk remembers. The catalog fields are not on this
    /// machine, so it is built from what the install row kept for exactly this moment — the
    /// cover included, because the artwork cache is keyed by URL and needs no server (D45).
    /// </summary>
    private static Game FromInstall(InstalledGame install) => new()
    {
        Id = install.GameId,
        Slug = install.GameSlug,
        Title = install.GameTitle,
        CoverUrl = install.CoverUrl,
    };

    /// <summary>
    /// The list, joined to what this machine has of it. One method for both paths: the cards
    /// an offline library shows are the same cards, and the join is the thing that must not be
    /// written twice.
    /// </summary>
    private void Show(
        IReadOnlyList<Game> games, IReadOnlyList<InstalledGame> installed, bool offline)
    {
        // Set before the cards are built, not after: what a card offers depends on it, and an
        // ordering rule spread over three callers is one that eventually gets it wrong — which
        // it did, and a test caught.
        IsOffline = offline;
        _hasLoaded = true;

        Dictionary<string, InstalledGame> byGameId = installed.ToDictionary(
            install => install.GameId, StringComparer.Ordinal);

        Games.Clear();
        foreach (Game game in games)
        {
            byGameId.TryGetValue(game.Id, out InstalledGame? install);
            Games.Add(new GameCardViewModel(game, install, _games, _localization)
            {
                // Decided here because the card cannot know it: the game page needs a server.
                CanOpenDetails = !offline,
            });
        }
    }

    /// <summary>
    /// Whose library this is. Empty when nobody is signed in, which the cache reads as "no
    /// list to keep": a page is never built for an account that does not exist.
    /// </summary>
    private string AccountId => _authentication.CurrentSession?.User.Id ?? string.Empty;

    /// <summary>
    /// What the account owns and what this machine has of it are two different things, and only
    /// the first of them belongs to the account: the install rows stay on the disk and are read
    /// again by the next load. What goes is the list built from them.
    /// </summary>
    public void ResetForAccountChange()
    {
        Games.Clear();
        ErrorMessage = null;
        IsBusy = false;
        IsOffline = false;
        _hasLoaded = false;
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnIsOfflineChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOfflineWithAccount));
        OnPropertyChanged(nameof(IsOfflineWithoutAccount));
    }

    /// <summary>
    /// Asks again, now. The circuit reopens on its own after a short window, but somebody who
    /// has pressed a button has said they think the network is back and is owed a real attempt
    /// rather than a wait they cannot see the end of.
    /// </summary>
    [RelayCommand]
    private async Task RetryAsync(CancellationToken cancellationToken)
    {
        _reachability.RetryNow();
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenGame(GameCardViewModel? card)
    {
        // The second half of the same rule as Play: the button is gone and the command refuses
        // too, because a card is a live object and the banner can land between a press and the
        // click.
        if (card is { CanOpenDetails: true })
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
        // The second half of the same rule: the button is gone, and the command refuses too.
        // A card is a live object and the check that hid the button can land while a finger is
        // on its way down.
        if (card?.Install is null || !card.CanPlay)
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
