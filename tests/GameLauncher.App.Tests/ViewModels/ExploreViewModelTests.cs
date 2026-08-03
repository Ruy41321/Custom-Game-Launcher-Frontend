using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class ExploreViewModelTests
{
    private readonly ICatalogApi _catalog = Substitute.For<ICatalogApi>();
    private readonly ILibraryApi _library = Substitute.For<ILibraryApi>();
    private readonly ResourceManagerLocalizationService _localization =
        new("en");

    private ExploreViewModel CreateViewModel() =>
        new(_catalog, _library, new ApiErrorPresenter(_localization), _localization);

    private static PagedResult<Game> PageOf(int total, int limit, int offset, params string[] titles) =>
        new()
        {
            Items = [.. titles.Select(title => new Game { Id = title, Slug = title, Title = title })],
            Total = total,
            Limit = limit,
            Offset = offset,
        };

    private void Returns(PagedResult<Game> page) =>
        _catalog.ExploreAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>()).Returns(page);

    [Fact]
    public async Task LoadingFillsTheListAndThePageCount()
    {
        Returns(PageOf(41, 20, 0, "Orbital Drift", "Deep Cut"));
        ExploreViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Games.Count);
        Assert.Equal(41, model.Total);
        Assert.Equal(3, model.PageCount);
        Assert.False(model.IsEmpty);
    }

    // "Nothing here" and "we have not asked yet" look the same on screen and must not.
    [Fact]
    public void NothingIsReportedAsEmptyBeforeTheFirstLoad()
    {
        Assert.False(CreateViewModel().IsEmpty);
    }

    [Fact]
    public async Task AnEmptyResultIsReportedAsEmpty()
    {
        Returns(PageOf(0, 20, 0));
        ExploreViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsEmpty);
    }

    [Fact]
    public async Task TheSearchTermAndSortReachTheQuery()
    {
        Returns(PageOf(0, 20, 0));
        ExploreViewModel model = CreateViewModel();
        model.SearchText = "drift";
        model.SelectedSort = model.SortOptions.Single(option => option.Sort == GameSort.Title);

        await model.LoadAsync(TestContext.Current.CancellationToken);

        await _catalog.Received().ExploreAsync(
            Arg.Is<GameQuery>(query => query!.Search == "drift" && query.Sort == GameSort.Title),
            Arg.Any<CancellationToken>());
    }

    // Searching from page 4 and staying there would show an empty result for a real match.
    [Fact]
    public async Task ANewSearchStartsFromTheFirstPage()
    {
        Returns(PageOf(100, 20, 60, "a"));
        ExploreViewModel model = CreateViewModel();
        model.Page = 4;

        await model.SearchCommand.ExecuteAsync(null);

        await _catalog.Received().ExploreAsync(
            Arg.Is<GameQuery>(query => query!.Page == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangingTheSortOrderAlsoReturnsToTheFirstPage()
    {
        Returns(PageOf(100, 20, 0, "a"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        model.Page = 3;

        model.SelectedSort = model.SortOptions.Single(option => option.Sort == GameSort.Recent);

        Assert.Equal(1, model.Page);
    }

    [Fact]
    public async Task PagingStopsAtBothEnds()
    {
        Returns(PageOf(30, 20, 0, "a"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(model.HasPreviousPage);
        Assert.True(model.HasNextPage);

        Returns(PageOf(30, 20, 20, "b"));
        await model.NextPageCommand.ExecuteAsync(null);

        Assert.Equal(2, model.Page);
        Assert.True(model.HasPreviousPage);
        Assert.False(model.HasNextPage);

        await model.NextPageCommand.ExecuteAsync(null);

        Assert.Equal(2, model.Page);
    }

    [Fact]
    public async Task AFailedLoadShowsAMessageAndLeavesNoStaleResults()
    {
        Returns(PageOf(1, 20, 0, "Orbital Drift"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        _catalog.ExploreAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "offline"));
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(model.Games);
        Assert.Equal(_localization.Translate("Error.Network"), model.ErrorMessage);

        // An error is not emptiness: telling the user "nothing matches" would be a lie.
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void OpeningAGameAsksForItBySlug()
    {
        ExploreViewModel model = CreateViewModel();
        string? requested = null;
        model.GameSelected += (_, idOrSlug) => requested = idOrSlug;

        model.OpenGameCommand.Execute(new Game { Id = "g1", Slug = "orbital-drift" });

        Assert.Equal("orbital-drift", requested);
    }

    // An unlisted game reached by id may have no slug worth putting in a URL.
    [Fact]
    public void AGameWithoutASlugIsOpenedById()
    {
        ExploreViewModel model = CreateViewModel();
        string? requested = null;
        model.GameSelected += (_, idOrSlug) => requested = idOrSlug;

        model.OpenGameCommand.Execute(new Game { Id = "g1", Slug = string.Empty });

        Assert.Equal("g1", requested);
    }

    [Fact]
    public async Task AddingToTheLibraryUsesTheGameId()
    {
        ExploreViewModel model = CreateViewModel();

        await model.AddToLibraryCommand.ExecuteAsync(
            new Game { Id = "g1", Slug = "orbital-drift" });

        await _library.Received(1).AddAsync("g1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedAddSaysSoWithoutLosingTheListing()
    {
        Returns(PageOf(1, 20, 0, "Orbital Drift"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        _library.AddAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Forbidden, "no"));
        await model.AddToLibraryCommand.ExecuteAsync(new Game { Id = "g1" });

        Assert.Equal(_localization.Translate("Error.Forbidden"), model.ErrorMessage);
        Assert.Single(model.Games);
    }

    [Fact]
    public void TheSortNamesFollowTheChosenLanguage()
    {
        var localization = new ResourceManagerLocalizationService("en");
        var model = new ExploreViewModel(
            _catalog, _library, new ApiErrorPresenter(localization), localization);

        Assert.Equal("Release date", model.SortOptions[0].Name);

        localization.TrySetLanguage("it");

        Assert.Equal("Data di uscita", model.SortOptions[0].Name);
    }
}
