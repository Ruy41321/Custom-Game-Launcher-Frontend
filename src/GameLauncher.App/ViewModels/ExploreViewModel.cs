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
public sealed partial class ExploreViewModel : ViewModelBase, IDisposable
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

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SortOption _selectedSort;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousPage))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _page = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _pageCount = 1;

    [ObservableProperty]
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

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < PageCount;

    public string PageLabel => _localization.Translate("Explore.PageOf", Page, PageCount);

    private bool HasLoaded { get; set; }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
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

        IsBusy = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsEmpty));

        try
        {
            PagedResult<Game> result = await _catalog
                .ExploreAsync(
                    new GameQuery
                    {
                        Search = SearchText,
                        Sort = SelectedSort.Sort,
                        Page = Page,
                    },
                    request.Token)
                .ConfigureAwait(true);

            Games.Clear();
            foreach (Game game in result.Items)
            {
                Games.Add(new StoreCardViewModel(game));
            }

            Total = result.Total;
            PageCount = result.PageCount;
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
            Games.Clear();
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
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(HasNextPage));
                OnPropertyChanged(nameof(HasPreviousPage));
            }

            request.Dispose();
        }

        // After the grid is on screen: the titles are the page, and a cover that has not
        // arrived yet is a placeholder rather than a page that has not appeared.
        foreach (StoreCardViewModel card in Games.ToList())
        {
            await card.LoadCoverAsync(_images, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Enter, or the search button. It searches at once and drops any pending debounce:
    /// somebody who presses Enter has already said they have finished typing, so the pause the
    /// debounce waits for has nobody left to wait for.
    ///
    /// A new search starts from the first page; staying on page 4 would show nothing.
    /// </summary>
    [RelayCommand]
    private Task SearchAsync(CancellationToken cancellationToken)
    {
        Disarm();
        Page = 1;
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
        _debounce ??= _time.CreateTimer(
            _ => OnUiThread(() =>
            {
                Page = 1;
                _ = LoadAsync(CancellationToken.None);
            }),
            state: null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _debounce.Change(SearchDebounce, Timeout.InfiniteTimeSpan);
    }

    private void Disarm() => _debounce?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    [RelayCommand]
    private Task NextPageAsync(CancellationToken cancellationToken)
    {
        if (!HasNextPage)
        {
            return Task.CompletedTask;
        }

        Page++;
        return LoadAsync(cancellationToken);
    }

    [RelayCommand]
    private Task PreviousPageAsync(CancellationToken cancellationToken)
    {
        if (!HasPreviousPage)
        {
            return Task.CompletedTask;
        }

        Page--;
        return LoadAsync(cancellationToken);
    }

    [RelayCommand]
    private void OpenGame(StoreCardViewModel? card)
    {
        if (card is { Game: { } game })
        {
            GameSelected?.Invoke(this, game.Slug.Length > 0 ? game.Slug : game.Id);
        }
    }

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

    partial void OnSelectedSortChanged(SortOption value) => Page = 1;

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
