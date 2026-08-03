using GameLauncher.Core.Models;

namespace GameLauncher.Core.Tests.Models;

public sealed class GameDetailTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static GameBuild Build(
        string id,
        GamePlatform platform,
        BuildArchitecture architecture = BuildArchitecture.X64,
        BuildStatus status = BuildStatus.Ready,
        int readyDaysAfterEpoch = 0) =>
        new()
        {
            Id = id,
            Platform = platform,
            Architecture = architecture,
            Status = status,
            ReadyAt = Epoch.AddDays(readyDaysAfterEpoch),
            CreatedAt = Epoch,
        };

    [Fact]
    public void NoBuildForThePlatformMeansNothingToInstall()
    {
        var detail = new GameDetail { Builds = [Build("linux", GamePlatform.Linux)] };

        Assert.Null(detail.BuildFor(GamePlatform.Windows, BuildArchitecture.X64));
    }

    // A build still uploading has no manifest, so there is nothing to download from it.
    [Fact]
    public void AnUnfinishedBuildIsNeverOffered()
    {
        var detail = new GameDetail
        {
            Builds =
            [
                Build("uploading", GamePlatform.Windows, status: BuildStatus.Uploading),
                Build("failed", GamePlatform.Windows, status: BuildStatus.Failed),
            ],
        };

        Assert.Null(detail.BuildFor(GamePlatform.Windows, BuildArchitecture.X64));
    }

    [Fact]
    public void TheNewestReadyBuildWins()
    {
        var detail = new GameDetail
        {
            Builds =
            [
                Build("old", GamePlatform.Windows, readyDaysAfterEpoch: 1),
                Build("new", GamePlatform.Windows, readyDaysAfterEpoch: 9),
                Build("middle", GamePlatform.Windows, readyDaysAfterEpoch: 5),
            ],
        };

        Assert.Equal("new", detail.BuildFor(GamePlatform.Windows, BuildArchitecture.X64)?.Id);
    }

    // An arm64 machine runs an x64 build under emulation, so a mismatch is a fallback rather
    // than a reason to report nothing installable.
    [Fact]
    public void TheRunningArchitectureIsPreferredButNotRequired()
    {
        var detail = new GameDetail
        {
            Builds = [Build("x64-only", GamePlatform.MacOs, BuildArchitecture.X64)],
        };

        Assert.Equal(
            "x64-only", detail.BuildFor(GamePlatform.MacOs, BuildArchitecture.Arm64)?.Id);
    }

    [Fact]
    public void AMatchingArchitectureBeatsANewerMismatchedOne()
    {
        var detail = new GameDetail
        {
            Builds =
            [
                Build("newer-x64", GamePlatform.MacOs, BuildArchitecture.X64, readyDaysAfterEpoch: 9),
                Build("older-arm64", GamePlatform.MacOs, BuildArchitecture.Arm64, readyDaysAfterEpoch: 1),
            ],
        };

        Assert.Equal(
            "older-arm64", detail.BuildFor(GamePlatform.MacOs, BuildArchitecture.Arm64)?.Id);
    }
}

public sealed class PagedResultTests
{
    [Fact]
    public void OffsetsAreReportedBackAsOneBasedPages()
    {
        var page = new PagedResult<Game> { Total = 95, Limit = 20, Offset = 40 };

        Assert.Equal(3, page.Page);
    }

    [Fact]
    public void APartialLastPageStillCounts()
    {
        var page = new PagedResult<Game> { Total = 95, Limit = 20, Offset = 0 };

        Assert.Equal(5, page.PageCount);
    }

    [Fact]
    public void AnEmptyResultIsStillOnePage()
    {
        var page = new PagedResult<Game> { Total = 0, Limit = 20, Offset = 0 };

        Assert.Equal(1, page.PageCount);
        Assert.Equal(1, page.Page);
    }

    // Never divide by a limit the server did not send.
    [Fact]
    public void AMissingLimitDoesNotThrow()
    {
        var page = new PagedResult<Game> { Total = 10 };

        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.PageCount);
    }
}
