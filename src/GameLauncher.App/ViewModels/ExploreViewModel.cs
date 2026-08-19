using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.App.Services;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;

namespace GameLauncher.App.ViewModels;

/// <summary>One entry of the sort picker, named in the user's language.</summary>
public sealed partial class SortOption(GameSort sort, string resourceKey, ILocalizationService localization)
    : ViewModelBase
{
    public GameSort Sort { get; } = sort;

    public string Name => localization.Translate(resourceKey);

    public void RefreshName() => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// One result in Explore. Nothing but the game and its cover: what this machine has of it is
/// the library's question, and asking it here would mean a disk lookup per card.
/// </summary>
public sealed class StoreCardViewModel(Game game) : GameCoverCardViewModel(game);

/// <summary>
/// The store front. Paging, sorting and searching are all the server's decisions — this only
/// asks and renders, which is why an older client cannot be broken by a new sort order.
/// </summary>
public sealed partial class ExploreViewModel : ViewModelBase, IAccountScopedPage, IDisposable
{
    /// <summary>
    /// How long the box waits after the last keystroke. Long enough that typing a word is one
    /// request instead of one per letter, and short enough that somebody who has stopped typing
    /// does not experience it as a pause — around a fifth of a second is where a delay starts
    /// being felt, and the request itself costs more than this either way.
    /// </summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    private readonly ICatalogApi _catalog;
    private readonly ILibraryApi _library;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;
    private readonly IImageProvider _images;
    private readonly TimeProvider _time;

    /// <summary>
    /// Armed by a keystroke and rearmed by the next one, so only the pause at the end of the
    /// word fires it. Created lazily: a page nobody types into never needs one.
    /// </summary>
    private ITimer? _debounce;

    /// <summary>
    /// The request currently in flight, so a newer one can stop it. Owned by the call that
    /// created it, which disposes it in its own <c>finally</c> whether it finished or was
    /// superseded.
    /// </summary>
    private CancellationTokenSource? _inFlight;

    /// <summary>
    /// Held while <see cref="ResetForAccountChange"/> empties the query. Both fields below mean
    /// "somebody changed what they are looking for" when they are written — one arms the
    /// debounce, the other loads at once — and emptying them would otherwise issue a request
    /// with nobody signed in. The same guard as the dashboard's <c>_suppressSelectionReload</c>,
    /// and it exists rather than a write to the backing field because the toolkit's analyzer
    /// makes that an error (MVVMTK0034).
    /// </summary>
    private bool _suppressReload;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// An identifier typed by hand, for a game this list will never contain. An unlisted game is
    /// served to anybody holding its id or slug and appears in no listing, so without somewhere
    /// to type one there is no way at all to reach a game a publisher shared privately.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenIdentifierCommand))]
    private string _identifierText = string.Empty;

    [ObservableProperty]
    private SortOption _selectedSort;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Appending the next page, as opposed to <see cref="IsBusy"/>, which is the list being
    /// replaced. They are separate because they look different: one is a spinner under results
    /// somebody is reading, the other is a page with nothing on it yet.
    /// </summary>
    [ObservableProperty]
    private bool _isLoadingMore;

    /// <summary>
    /// Whether the server has said there is more. It is set from a real answer rather than
    /// inferred from an empty page arriving, because "ask until nothing comes back" means the
    /// end of the list is a wasted request every time somebody scrolls to the bottom.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEnded))]
    private bool _hasMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultsLabel))]
    private int _total;

    public ExploreViewModel(
        ICatalogApi catalog,
        ILibraryApi library,
        IApiErrorPresenter errors,
        ILocalizationService localization,
        IImageProvider images,
        TimeProvider time)
    {
        _catalog = catalog;
        _library = library;
        _errors = errors;
        _localization = localization;
        _images = images;
        _time = time;

        SortOptions =
        [
            new SortOption(GameSort.ReleaseDate, "Explore.Sort.ReleaseDate", localization),
            new SortOption(GameSort.Title, "Explore.Sort.Title", localization),
            new SortOption(GameSort.Recent, "Explore.Sort.Recent", localization),
        ];
        _selectedSort = SortOptions[0];

        localization.LanguageChanged += (_, _) =>
        {
            foreach (SortOption option in SortOptions)
            {
                option.RefreshName();
            }
        };
    }

    /// <summary>Raised when the user asks to see one game in full.</summary>
    public event EventHandler<string>? GameSelected;

    public ObservableCollection<StoreCardViewModel> Games { get; } = [];

    public IReadOnlyList<SortOption> SortOptions { get; }

    /// <summary>True once a load has completed and found nothing.</summary>
    public bool IsEmpty => !IsBusy && Games.Count == 0 && ErrorMessage is null && HasLoaded;

    /// <summary>
    /// The end of the results, said out loud, and only once there is a list to be at the end
    /// of. Somebody who scrolled through everything has earned an answer rather than a page
    /// that simply stops.
    /// </summary>
    public bool HasEnded => !HasMore && HasLoaded && Games.Count > 0;

    public string ResultsLabel =>
        _localization.Translate("Explore.Results", Games.Count, Total);

    /// <summary>
    /// The page a scroll to the bottom will ask for. Advanced only by an answer that arrived,
    /// so a failed or superseded request leaves the next scroll asking for the same page rather
    /// than stepping over one nobody ever saw.
    /// </summary>
    private int NextPage { get; set; } = 1;

    private bool HasLoaded { get; set; }

    /// <summary>
    /// Starts the listing again from the top: a first load, a new search, a different sort, or
    /// a retry after a failure. Everything on screen goes, because the results that replace it
    /// answer a different question.
    /// </summary>
    [RelayCommand]
    public Task LoadAsync(CancellationToken cancellationToken) =>
        FetchAsync(1, replacing: true, cancellationToken);

    /// <summary>
    /// The next page, appended. Called by the view when the grid is scrolled near its end, so
    /// it is asked far more often than it does anything — every refusal here is one this has to
    /// make cheaply and silently.
    /// </summary>
    [RelayCommand]
    public Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        // Three reasons to do nothing, and each is a bug if it is missing. There is no more to
        // fetch; a request is already in flight, so scrolling during one would ask for the same
        // page again; or nothing has been loaded at all, in which case the first page is
        // `LoadAsync`'s to fetch and racing it here would duplicate it.
        if (!HasMore || _inFlight is not null || !HasLoaded)
        {
            return Task.CompletedTask;
        }

        return FetchAsync(NextPage, replacing: false, cancellationToken);
    }

    private async Task FetchAsync(int page, bool replacing, CancellationToken cancellationToken)
    {
        // A newer request stops the one it replaces rather than racing it. Without this, a slow
        // answer for "orb" arriving after the answer for "orbital" is not merely a wasted
        // request — it is the wrong results left on screen, which no amount of debouncing makes
        // impossible.
        CancellationTokenSource request =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        CancellationTokenSource? superseded = _inFlight;
        _inFlight = request;
        superseded?.Cancel();

        if (replacing)
        {
            IsBusy = true;
            ErrorMessage = null;
            OnPropertyChanged(nameof(IsEmpty));
        }
        else
        {
            IsLoadingMore = true;
        }

        int appendedFrom = Games.Count;

        try
        {
            PagedResult<Game> result = await _catalog
                .ExploreAsync(
                    new GameQuery
                    {
                        Search = SearchText,
                        Sort = SelectedSort.Sort,
                        Page = page,
                    },
                    request.Token)
                .ConfigureAwait(true);

            // Checked after the await as well as relied upon to throw: a request that was
            // superseded between the answer arriving and this line resuming would otherwise
            // append somebody else's results to a list that has already been replaced.
            if (!ReferenceEquals(_inFlight, request))
            {
                return;
            }

            if (replacing)
            {
                // The only place the list is emptied. An append that cleared first would make
                // the grid flash and lose the scroll position of somebody reading it.
                Games.Clear();
                appendedFrom = 0;
            }

            foreach (Game game in result.Items)
            {
                Games.Add(new StoreCardViewModel(game));
            }

            Total = result.Total;
            NextPage = page + 1;

            // Two conditions, and the second is not redundant. The count is the answer; the
            // empty page is the guard against a total that disagrees with what is being served,
            // which would otherwise be an unbounded sequence of requests returning nothing.
            HasMore = Games.Count < result.Total && result.Items.Count > 0;
            HasLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // A search nobody is waiting for any more: the user typed another letter, or left
            // the page. Neither is a failure, and putting one where the results will be would
            // make ordinary typing look broken.
            return;
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);

            if (replacing)
            {
                Games.Clear();
                HasMore = false;
            }

            // An append that failed leaves what is already on screen alone: the results are
            // still the right answer to the question that was asked, and `NextPage` is
            // unchanged, so scrolling again retries the page nobody has seen.
        }
        finally
        {
            // Only the request that is still the current one owns what is on screen. A
            // superseded one clearing the busy indicator would clear it for the search that
            // replaced it and is still running.
            if (ReferenceEquals(_inFlight, request))
            {
                _inFlight = null;
                IsBusy = false;
                IsLoadingMore = false;
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(HasEnded));
                OnPropertyChanged(nameof(ResultsLabel));
            }

            request.Dispose();
        }

        // After the grid is on screen: the titles are the page, and a cover that has not
        // arrived yet is a placeholder rather than a page that has not appeared. Only the cards
        // this call added, so appending a page does not re-ask for every cover above it.
        foreach (StoreCardViewModel card in Games.Skip(appendedFrom).ToList())
        {
            await card.LoadCoverAsync(_images, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Enter, or the search button. It searches at once and drops any pending debounce:
    /// somebody who presses Enter has already said they have finished typing, so the pause the
    /// debounce waits for has nobody left to wait for.
    ///
    /// A new search replaces the results rather than adding to them — which is what
    /// <see cref="LoadAsync"/> does, and the reason the two entry points are separate.
    /// </summary>
    [RelayCommand]
    private Task SearchAsync(CancellationToken cancellationToken)
    {
        Disarm();
        return LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Every keystroke rearms the timer, so only the pause at the end fires it. The delay comes
    /// from <see cref="TimeProvider"/> rather than from <c>Task.Delay</c> so a test advances it
    /// by hand instead of sleeping: a debounce a test really waits out is a slow test that
    /// eventually fails on a loaded machine rather than on a bug.
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        if (_suppressReload)
        {
            return;
        }

        _debounce ??= _time.CreateTimer(
            _ => OnUiThread(() => _ = LoadAsync(CancellationToken.None)),
            state: null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _debounce.Change(SearchDebounce, Timeout.InfiniteTimeSpan);
    }

    private void Disarm() => _debounce?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    [RelayCommand]
    private void OpenGame(StoreCardViewModel? card)
    {
        if (card is { Game: { } game })
        {
            GameSelected?.Invoke(this, game.Slug.Length > 0 ? game.Slug : game.Id);
        }
    }

    /// <summary>
    /// Opens whatever was typed, without asking the server first whether it exists. The detail
    /// page has to describe a refusal anyway — it is reached from three other places — and a
    /// lookup here would only mean the same 404 arriving twice, in two wordings.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenIdentifier))]
    private void OpenIdentifier() => GameSelected?.Invoke(this, IdentifierText.Trim());

    private bool CanOpenIdentifier() => IdentifierText.Trim().Length > 0;

    [RelayCommand]
    private async Task AddToLibraryAsync(
        StoreCardViewModel? card, CancellationToken cancellationToken)
    {
        if (card is not { Game: { } game })
        {
            return;
        }

        try
        {
            await _library.AddAsync(game.Id, cancellationToken).ConfigureAwait(true);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
    }

    /// <summary>
    /// A different order is a different list, not more of the same one, so it reloads from the
    /// top rather than leaving three pages of the old order above the new first page.
    /// </summary>
    partial void OnSelectedSortChanged(SortOption value)
    {
        if (!_suppressReload)
        {
            _ = LoadAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Everything here came from a search made with the previous account's token, including
    /// the search itself: what somebody typed is as much theirs as the results it found.
    ///
    /// The query is emptied under <see cref="_suppressReload"/>: both of those setters mean
    /// "somebody changed the query", and a reset that reloaded the list would issue a request
    /// while nobody is signed in, which is the opposite of what it is for.
    /// </summary>
    public void ResetForAccountChange()
    {
        Disarm();
        _inFlight?.Cancel();

        Games.Clear();
        ErrorMessage = null;
        IsBusy = false;
        IsLoadingMore = false;
        HasMore = false;
        Total = 0;
        HasLoaded = false;
        NextPage = 1;
        IdentifierText = string.Empty;

        _suppressReload = true;
        try
        {
            SearchText = string.Empty;
            SelectedSort = SortOptions[0];
        }
        finally
        {
            _suppressReload = false;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasEnded));
        OnPropertyChanged(nameof(ResultsLabel));
    }

    /// <summary>
    /// The page lives as long as the window, so this runs when the container is torn down. It
    /// exists because the view model owns a timer and a cancellation source, not because
    /// anything here is expected to be reclaimed early.
    /// </summary>
    public void Dispose()
    {
        _debounce?.Dispose();
        _debounce = null;

        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;
    }
}
