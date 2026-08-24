using GameLauncher.Core.Models;

namespace GameLauncher.Core.Tests.Models;

public sealed class GameQueryTests
{
    [Fact]
    public void TheDefaultQueryAsksForNothingInParticular()
    {
        Assert.Equal(string.Empty, new GameQuery().ToQueryString());
    }

    [Fact]
    public void SearchTermsAreEscaped()
    {
        var query = new GameQuery { Search = "space & time" };

        Assert.Equal("search=space%20%26%20time", query.ToQueryString());
    }

    [Fact]
    public void ABlankSearchIsOmittedRatherThanSentEmpty()
    {
        Assert.Equal(string.Empty, new GameQuery { Search = "   " }.ToQueryString());
    }

    [Fact]
    public void SearchTermsAreTrimmed()
    {
        Assert.Equal("search=orbit", new GameQuery { Search = "  orbit  " }.ToQueryString());
    }

    [Theory]
    [InlineData(GameSort.Title, "sort=title")]
    [InlineData(GameSort.Recent, "sort=recent")]
    public void ANonDefaultSortIsNamedOnTheWire(GameSort sort, string expected)
    {
        Assert.Equal(expected, new GameQuery { Sort = sort }.ToQueryString());
    }

    [Fact]
    public void PagesAreOneBasedAndTheFirstOneIsImplicit()
    {
        Assert.Equal(string.Empty, new GameQuery { Page = 1 }.ToQueryString());
        Assert.Equal("page=3", new GameQuery { Page = 3 }.ToQueryString());
    }

    // The server clamps too, but sending 5000 and being quietly given 100 makes the client's
    // own paging arithmetic wrong.
    [Fact]
    public void AnOversizedPageIsClampedToWhatTheServerAccepts()
    {
        Assert.Equal("pageSize=100", new GameQuery { PageSize = 5000 }.ToQueryString());
    }

    [Fact]
    public void EveryParameterIsCombinedWithAnAmpersand()
    {
        var query = new GameQuery
        {
            Search = "orbit",
            Sort = GameSort.Title,
            Page = 2,
            PageSize = 50,
        };

        Assert.Equal("search=orbit&sort=title&page=2&pageSize=50", query.ToQueryString());
    }
}
