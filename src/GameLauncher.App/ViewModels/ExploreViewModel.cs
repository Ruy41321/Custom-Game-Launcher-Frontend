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
public sealed partial class ExploreViewModel : ViewModelBase
{
    private readonly ICatalogApi _catalog;
    private readonly ILibraryApi _library;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;
    private readonly IImageProvider _images;

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
        IImageProvider images)
    {
        _catalog = catalog;
        _library = library;
        _errors = errors;
        _localization = localization;
        _images = images;

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
                    cancellationToken)
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
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
            Games.Clear();
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNextPage));
            OnPropertyChanged(nameof(HasPreviousPage));
        }

        // After the grid is on screen: the titles are the page, and a cover that has not
        // arrived yet is a placeholder rather than a page that has not appeared.
        foreach (StoreCardViewModel card in Games.ToList())
        {
            await card.LoadCoverAsync(_images, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>A new search starts from the first page; staying on page 4 would show nothing.</summary>
    [RelayCommand]
    private Task SearchAsync(CancellationToken cancellationToken)
    {
        Page = 1;
        return LoadAsync(cancellationToken);
    }

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
}
