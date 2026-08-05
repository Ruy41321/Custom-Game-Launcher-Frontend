using GameLauncher.App.Services;
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

    private readonly IImageProvider _images = Substitute.For<IImageProvider>();

    private readonly FakeTimeProvider _clock =
        new(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

    private ExploreViewModel CreateViewModel() =>
        new(
            _catalog,
            _library,
            new ApiErrorPresenter(_localization),
            _localization,
            _images,
            _clock);

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

        model.OpenGameCommand.Execute(
            new StoreCardViewModel(new Game { Id = "g1", Slug = "orbital-drift" }));

        Assert.Equal("orbital-drift", requested);
    }

    // An unlisted game reached by id may have no slug worth putting in a URL.
    [Fact]
    public void AGameWithoutASlugIsOpenedById()
    {
        ExploreViewModel model = CreateViewModel();
        string? requested = null;
        model.GameSelected += (_, idOrSlug) => requested = idOrSlug;

        model.OpenGameCommand.Execute(
            new StoreCardViewModel(new Game { Id = "g1", Slug = string.Empty }));

        Assert.Equal("g1", requested);
    }

    [Fact]
    public async Task AddingToTheLibraryUsesTheGameId()
    {
        ExploreViewModel model = CreateViewModel();

        await model.AddToLibraryCommand.ExecuteAsync(
            new StoreCardViewModel(new Game { Id = "g1", Slug = "orbital-drift" }));

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
        await model.AddToLibraryCommand.ExecuteAsync(
            new StoreCardViewModel(new Game { Id = "g1" }));

        Assert.Equal(_localization.Translate("Error.Forbidden"), model.ErrorMessage);
        Assert.Single(model.Games);
    }

    [Fact]
    public async Task EveryCardIsAskedForItsCoverAfterTheGridIsFilled()
    {
        _catalog.ExploreAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>()).Returns(
            new PagedResult<Game>
            {
                Items =
                [
                    new Game { Id = "g1", Title = "Orbital Drift", CoverUrl = "https://f/1.png" },
                    new Game { Id = "g2", Title = "Deep Cut" },
                ],
                Total = 2,
                Limit = 20,
            });

        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        await _images.Received(1).GetAsync("https://f/1.png", Arg.Any<CancellationToken>());

        // A game with no cover is still asked, because deciding there is nothing to fetch is
        // the provider's job and it answers null for an empty URL.
        await _images.Received(1).GetAsync(string.Empty, Arg.Any<CancellationToken>());
    }

    // A cover that never arrives leaves a card that still says what the game is.
    [Fact]
    public async Task ACardWithNoCoverShowsTheFirstLetterOfItsTitle()
    {
        Returns(PageOf(1, 20, 0, "Orbital Drift"));
        ExploreViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);

        StoreCardViewModel card = Assert.Single(model.Games);
        Assert.False(card.HasCover);
        Assert.Equal("O", card.CoverPlaceholder);
    }

    /// <summary>
    /// A request that never answers of its own accord, so a test can watch what happens to it
    /// when the next one starts.
    /// </summary>
    private static Task<PagedResult<Game>> NeverAnswers(CancellationToken cancellationToken)
    {
        TaskCompletionSource<PagedResult<Game>> pending = new();
        cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        return pending.Task;
    }

    private List<CancellationToken> RecordTokensAndNeverAnswer()
    {
        List<CancellationToken> asked = [];

        _catalog.ExploreAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                CancellationToken token = call.Arg<CancellationToken>();
                asked.Add(token);
                return NeverAnswers(token);
            });

        return asked;
    }

    // Typing a word was one request per letter, of which only the last was ever read.
    [Fact]
    public async Task TypingAWordIsOneRequest()
    {
        Returns(PageOf(0, 20, 0));
        ExploreViewModel model = CreateViewModel();

        model.SearchText = "orb";
        model.SearchText = "orbi";
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        await Task.Yield();

        // Still typing: the pause the debounce waits for has not happened yet.
        await _catalog.DidNotReceive().ExploreAsync(
            Arg.Any<GameQuery>(), Arg.Any<CancellationToken>());

        model.SearchText = "orbital";
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        await Task.Yield();

        await _catalog.Received(1).ExploreAsync(
            Arg.Any<GameQuery>(), Arg.Any<CancellationToken>());
        await _catalog.Received(1).ExploreAsync(
            Arg.Is<GameQuery>(query => query!.Search == "orbital" && query.Page == 1),
            Arg.Any<CancellationToken>());
    }

    // The debounce alone would still leave a race: a slow answer for "orb" landing after the
    // answer for "orbital" is not a wasted request, it is the wrong results on screen.
    [Fact]
    public async Task ANewSearchCancelsTheOneStillInFlight()
    {
        List<CancellationToken> asked = RecordTokensAndNeverAnswer();
        ExploreViewModel model = CreateViewModel();

        Task first = model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);

        model.SearchText = "orbital";
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(2, asked.Count);
        Assert.True(asked[0].IsCancellationRequested);
        Assert.False(asked[1].IsCancellationRequested);

        await first;
    }

    // Cancelling is how the launcher keeps up with typing, so it must not read as a failure.
    [Fact]
    public async Task ASearchThatWasSupersededLeavesNoErrorOnScreen()
    {
        RecordTokensAndNeverAnswer();
        ExploreViewModel model = CreateViewModel();

        Task first = model.LoadAsync(TestContext.Current.CancellationToken);

        model.SearchText = "orbital";
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        await first;

        Assert.Null(model.ErrorMessage);

        // Nor as emptiness: nothing has been answered yet, and "no games match" would be a lie.
        Assert.False(model.IsEmpty);

        // The search that replaced it is still running, so the page still says it is working.
        Assert.True(model.IsBusy);
    }

    [Fact]
    public async Task PressingEnterSearchesWithoutWaitingForTheDebounce()
    {
        Returns(PageOf(0, 20, 0));
        ExploreViewModel model = CreateViewModel();
        model.SearchText = "orbital";

        await model.SearchCommand.ExecuteAsync(null);

        await _catalog.Received(1).ExploreAsync(
            Arg.Is<GameQuery>(query => query!.Search == "orbital"), Arg.Any<CancellationToken>());

        // And the pending debounce is dropped rather than repeating the same search a moment
        // later, which the user would see as the page reloading for no reason.
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();

        await _catalog.Received(1).ExploreAsync(
            Arg.Any<GameQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheSortNamesFollowTheChosenLanguage()
    {
        var localization = new ResourceManagerLocalizationService("en");
        var model = new ExploreViewModel(
            _catalog,
            _library,
            new ApiErrorPresenter(localization),
            localization,
            _images,
            _clock);

        Assert.Equal("Release date", model.SortOptions[0].Name);

        localization.TrySetLanguage("it");

        Assert.Equal("Data di uscita", model.SortOptions[0].Name);
    }
}
