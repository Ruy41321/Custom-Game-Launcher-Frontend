using GameLauncher.App.Services;
using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
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
            new ApiErrorPresenter(_localization, NullLogger<ApiErrorPresenter>.Instance),
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
    public async Task LoadingFillsTheListAndSaysThereIsMore()
    {
        Returns(PageOf(41, 20, 0, "Orbital Drift", "Deep Cut"));
        ExploreViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Games.Count);
        Assert.Equal(41, model.Total);
        Assert.True(model.HasMore);
        Assert.False(model.HasEnded);
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

    // ---------------------------------------------------------------------
    // Scrolling for more. These replace the Previous/Next tests: the questions are the same —
    // where the list starts, where it ends, what a filter change does — and only the mechanism
    // moved.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ScrollingAppendsTheNextPageInsteadOfReplacingTheList()
    {
        Returns(PageOf(30, 20, 0, "a", "b"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Returns(PageOf(30, 20, 20, "c"));
        await model.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(["a", "b", "c"], model.Games.Select(card => card.Title));
        await _catalog.Received(1).ExploreAsync(
            Arg.Is<GameQuery>(query => query!.Page == 2), Arg.Any<CancellationToken>());
    }

    // The end is a state the server's own count decides, not something discovered by asking one
    // more time and getting nothing back.
    [Fact]
    public async Task TheEndOfTheResultsIsSaidRatherThanDiscovered()
    {
        Returns(PageOf(2, 20, 0, "a", "b"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(model.HasMore);
        Assert.True(model.HasEnded);

        await model.LoadMoreCommand.ExecuteAsync(null);

        // Still one request in total: scrolling at the bottom of a finished list asks nothing.
        await _catalog.Received(1).ExploreAsync(
            Arg.Any<GameQuery>(), Arg.Any<CancellationToken>());
    }

    // A total the server cannot actually serve — rows deleted between two pages, say — would
    // otherwise be an unbounded sequence of requests answering nothing.
    [Fact]
    public async Task APageThatComesBackEmptyEndsTheListEvenIfTheTotalDisagrees()
    {
        Returns(PageOf(500, 20, 0, "a"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(model.HasMore);

        Returns(PageOf(500, 20, 20));
        await model.LoadMoreCommand.ExecuteAsync(null);

        Assert.False(model.HasMore);
        Assert.Single(model.Games);
    }

    [Fact]
    public async Task ScrollingWhileAPageIsStillArrivingAsksForNothing()
    {
        Returns(PageOf(100, 20, 0, "a"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        RecordTokensAndNeverAnswer();
        Task appending = model.LoadMoreAsync(TestContext.Current.CancellationToken);

        // The scroll handler fires on every scroll event, so this is the ordinary case rather
        // than an unlikely one: without the guard, one flick of a wheel is several requests for
        // the same page and several copies of it in the list.
        //
        // Asserted as "refused without starting anything" rather than by awaiting: a guard that
        // stopped working would hang this test instead of failing it, and a suite that hangs is
        // a suite somebody stops running.
        Assert.True(model.LoadMoreAsync(TestContext.Current.CancellationToken).IsCompleted);
        Assert.True(model.LoadMoreAsync(TestContext.Current.CancellationToken).IsCompleted);

        await _catalog.Received(1).ExploreAsync(
            Arg.Is<GameQuery>(query => query!.Page == 2), Arg.Any<CancellationToken>());

        model.Dispose();
        await appending;
    }

    [Fact]
    public async Task NothingIsAppendedBeforeTheFirstPageHasLoaded()
    {
        Returns(PageOf(100, 20, 0, "a"));
        ExploreViewModel model = CreateViewModel();

        Assert.True(model.LoadMoreAsync(TestContext.Current.CancellationToken).IsCompleted);

        await _catalog.DidNotReceive().ExploreAsync(
            Arg.Any<GameQuery>(), Arg.Any<CancellationToken>());
    }

    // Searching after scrolling three pages in would otherwise leave sixty results of the old
    // search above the first page of the new one.
    [Fact]
    public async Task ANewSearchEmptiesTheListAndStartsFromTheFirstPage()
    {
        Returns(PageOf(100, 20, 0, "a", "b"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Returns(PageOf(100, 20, 20, "c"));
        await model.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(3, model.Games.Count);

        Returns(PageOf(1, 20, 0, "match"));
        model.SearchText = "match";
        await model.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["match"], model.Games.Select(card => card.Title));
        await _catalog.Received().ExploreAsync(
            Arg.Is<GameQuery>(query => query!.Search == "match" && query.Page == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangingTheSortOrderAlsoStartsTheListAgain()
    {
        Returns(PageOf(100, 20, 0, "a", "b"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Returns(PageOf(100, 20, 20, "c"));
        await model.LoadMoreCommand.ExecuteAsync(null);

        Returns(PageOf(100, 20, 0, "z"));
        model.SelectedSort = model.SortOptions.Single(option => option.Sort == GameSort.Recent);
        await Task.Yield();

        Assert.Equal(["z"], model.Games.Select(card => card.Title));
        await _catalog.Received().ExploreAsync(
            Arg.Is<GameQuery>(query => query!.Sort == GameSort.Recent && query.Page == 1),
            Arg.Any<CancellationToken>());
    }

    // What is already on screen is still the right answer to the question that was asked, and
    // the page that failed is the one the next scroll retries.
    [Fact]
    public async Task AFailedAppendKeepsTheResultsAndRetriesTheSamePage()
    {
        Returns(PageOf(100, 20, 0, "a", "b"));
        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        _catalog.ExploreAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "offline"));
        await model.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(2, model.Games.Count);
        Assert.Equal(_localization.Translate("Error.Network"), model.ErrorMessage);

        Returns(PageOf(100, 20, 20, "c"));
        await model.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(3, model.Games.Count);
        await _catalog.Received(2).ExploreAsync(
            Arg.Is<GameQuery>(query => query!.Page == 2), Arg.Any<CancellationToken>());
    }

    // The covers of a page already on screen were fetched when that page arrived.
    [Fact]
    public async Task AppendingOnlyAsksForTheCoversOfWhatItAdded()
    {
        _catalog.ExploreAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>()).Returns(
            new PagedResult<Game>
            {
                Items = [new Game { Id = "g1", Title = "First", CoverUrl = "https://f/1.png" }],
                Total = 2,
                Limit = 1,
            });

        ExploreViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        _catalog.ExploreAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>()).Returns(
            new PagedResult<Game>
            {
                Items = [new Game { Id = "g2", Title = "Second", CoverUrl = "https://f/2.png" }],
                Total = 2,
                Limit = 1,
                Offset = 1,
            });
        await model.LoadMoreCommand.ExecuteAsync(null);

        await _images.Received(1).GetAsync("https://f/1.png", Arg.Any<CancellationToken>());
        await _images.Received(1).GetAsync("https://f/2.png", Arg.Any<CancellationToken>());
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

    // The only way to reach an unlisted game: it is in no listing and the search box only ever
    // asks for public ones, so a typed identifier is the whole feature.
    [Fact]
    public void AnIdentifierTypedByHandIsOpenedAsItIs()
    {
        ExploreViewModel model = CreateViewModel();
        string? requested = null;
        model.GameSelected += (_, idOrSlug) => requested = idOrSlug;

        model.IdentifierText = "  orbital-drift  ";
        model.OpenIdentifierCommand.Execute(null);

        Assert.Equal("orbital-drift", requested);
    }

    // The button is the one thing standing between an empty box and a request for nothing.
    [Fact]
    public void AnIdentifierOfWhitespaceCannotBeOpened()
    {
        ExploreViewModel model = CreateViewModel();

        Assert.False(model.OpenIdentifierCommand.CanExecute(null));

        model.IdentifierText = "   ";
        Assert.False(model.OpenIdentifierCommand.CanExecute(null));

        model.IdentifierText = "g1";
        Assert.True(model.OpenIdentifierCommand.CanExecute(null));
    }

    // Typing an identifier must not disturb the list underneath it: the debounce belongs to the
    // search box alone, and a reload here would replace the results while somebody is reading.
    [Fact]
    public async Task TypingAnIdentifierAsksTheServerForNothing()
    {
        ExploreViewModel model = CreateViewModel();

        model.IdentifierText = "orbital-drift";
        _clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Yield();

        await _catalog.DidNotReceive().ExploreAsync(
            Arg.Any<GameQuery>(), Arg.Any<CancellationToken>());
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
            new ApiErrorPresenter(localization, NullLogger<ApiErrorPresenter>.Instance),
            localization,
            _images,
            _clock);

        Assert.Equal("Release date", model.SortOptions[0].Name);

        localization.TrySetLanguage("it");

        Assert.Equal("Data di uscita", model.SortOptions[0].Name);
    }
}
