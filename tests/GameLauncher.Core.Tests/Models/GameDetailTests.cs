using GameLauncher.Core.Models;

namespace GameLauncher.Core.Tests.Models;

public sealed class GameDetailTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Released = "v-released";
    private const string Unreleased = "v-unreleased";

    private static GameBuild Build(
        string id,
        GamePlatform platform,
        BuildArchitecture architecture = BuildArchitecture.X64,
        BuildStatus status = BuildStatus.Ready,
        int readyDaysAfterEpoch = 0,
        string versionId = Released) =>
        new()
        {
            Id = id,
            VersionId = versionId,
            Platform = platform,
            Architecture = architecture,
            Status = status,
            ReadyAt = Epoch.AddDays(readyDaysAfterEpoch),
            CreatedAt = Epoch,
        };

    /// <summary>
    /// A detail carrying both kinds of version, which is what a *publisher* is served: their
    /// unpublished versions come down beside the released ones.
    /// </summary>
    private static GameDetail Detail(params GameBuild[] builds) =>
        new()
        {
            Versions =
            [
                new GameVersion { Id = Released, Semver = "1.0.0", Published = true },
                new GameVersion { Id = Unreleased, Semver = "2.0.0", Published = false },
            ],
            Builds = builds,
        };

    [Fact]
    public void NoBuildForThePlatformMeansNothingToInstall()
    {
        GameDetail detail = Detail(Build("linux", GamePlatform.Linux));

        Assert.Null(detail.BuildFor(GamePlatform.Windows, BuildArchitecture.X64));
    }

    // A build still uploading has no manifest, so there is nothing to download from it.
    [Fact]
    public void AnUnfinishedBuildIsNeverOffered()
    {
        GameDetail detail = Detail(
            Build("uploading", GamePlatform.Windows, status: BuildStatus.Uploading),
            Build("failed", GamePlatform.Windows, status: BuildStatus.Failed));

        Assert.Null(detail.BuildFor(GamePlatform.Windows, BuildArchitecture.X64));
    }

    [Fact]
    public void TheNewestReadyBuildWins()
    {
        GameDetail detail = Detail(
            Build("old", GamePlatform.Windows, readyDaysAfterEpoch: 1),
            Build("new", GamePlatform.Windows, readyDaysAfterEpoch: 9),
            Build("middle", GamePlatform.Windows, readyDaysAfterEpoch: 5));

        Assert.Equal("new", detail.BuildFor(GamePlatform.Windows, BuildArchitecture.X64)?.Id);
    }

    // An arm64 machine runs an x64 build under emulation, so a mismatch is a fallback rather
    // than a reason to report nothing installable.
    [Fact]
    public void TheRunningArchitectureIsPreferredButNotRequired()
    {
        GameDetail detail = Detail(Build("x64-only", GamePlatform.MacOs, BuildArchitecture.X64));

        Assert.Equal(
            "x64-only", detail.BuildFor(GamePlatform.MacOs, BuildArchitecture.Arm64)?.Id);
    }

    [Fact]
    public void AMatchingArchitectureBeatsANewerMismatchedOne()
    {
        GameDetail detail = Detail(
            Build("newer-x64", GamePlatform.MacOs, BuildArchitecture.X64, readyDaysAfterEpoch: 9),
            Build("older-arm64", GamePlatform.MacOs, BuildArchitecture.Arm64, readyDaysAfterEpoch: 1));

        Assert.Equal(
            "older-arm64", detail.BuildFor(GamePlatform.MacOs, BuildArchitecture.Arm64)?.Id);
    }

    // D71. The server hides an unpublished version from everybody but its publisher, so this
    // only ever bites the publisher — who was losing Play on their own library card over a
    // build nobody, themselves included, may download.
    [Fact]
    public void ABuildOfAnUnpublishedVersionIsNotOffered()
    {
        GameDetail detail = Detail(
            Build("released", GamePlatform.Windows, readyDaysAfterEpoch: 1),
            Build("draft", GamePlatform.Windows, readyDaysAfterEpoch: 9, versionId: Unreleased));

        Assert.Equal("released", detail.BuildFor(GamePlatform.Windows, BuildArchitecture.X64)?.Id);
    }

    [Fact]
    public void OnlyUnpublishedVersionsMeansNothingToInstall()
    {
        GameDetail detail = Detail(
            Build("draft", GamePlatform.Windows, versionId: Unreleased));

        Assert.Null(detail.BuildFor(GamePlatform.Windows, BuildArchitecture.X64));
    }

    // Withholding is the safe direction, the same one the server's versionPublished defaults in.
    [Fact]
    public void ABuildWhoseVersionIsNotInTheListIsWithheld()
    {
        GameDetail detail = Detail(
            Build("orphan", GamePlatform.Windows, versionId: "a-version-nobody-sent"));

        Assert.Null(detail.BuildFor(GamePlatform.Windows, BuildArchitecture.X64));
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
