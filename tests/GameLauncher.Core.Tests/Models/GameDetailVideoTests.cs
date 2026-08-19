using GameLauncher.Core.Models;

namespace GameLauncher.Core.Tests.Models;

/// <summary>
/// The videos are their own list, and the two galleries must not leak into each other: a video
/// in the screenshot strip would be handed to an image decoder, and a screenshot among the
/// videos would be handed to a player.
/// </summary>
public sealed class GameDetailVideoTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static GameMedia Media(
        string id, MediaKind kind, int sortOrder = 0, int createdDaysAfterEpoch = 0) =>
        new()
        {
            Id = id,
            Kind = kind,
            SortOrder = sortOrder,
            CreatedAt = Epoch.AddDays(createdDaysAfterEpoch),
            Url = "https://files.example/media/" + id,
        };

    [Fact]
    public void TheTwoGalleriesDoNotLeakIntoEachOther()
    {
        GameDetail detail = new()
        {
            Media =
            [
                Media("shot", MediaKind.Screenshot),
                Media("clip", MediaKind.Video),
                Media("cover", MediaKind.Cover),
            ],
        };

        Assert.Equal(["shot"], detail.Screenshots.Select(item => item.Id));
        Assert.Equal(["clip"], detail.Videos.Select(item => item.Id));
    }

    /// <summary>
    /// Ordered the way the screenshots are, ties broken by upload time — so two videos left at
    /// the default sort order do not swap places between loads.
    /// </summary>
    [Fact]
    public void VideosFollowTheOrderThePublisherArrangedAndThenTheirAge()
    {
        GameDetail detail = new()
        {
            Media =
            [
                Media("third", MediaKind.Video, sortOrder: 2),
                Media("second", MediaKind.Video, sortOrder: 0, createdDaysAfterEpoch: 5),
                Media("first", MediaKind.Video, sortOrder: 0, createdDaysAfterEpoch: 1),
            ],
        };

        Assert.Equal(["first", "second", "third"], detail.Videos.Select(item => item.Id));
    }

    /// <summary>
    /// <c>Artwork</c> answers about the kinds there is at most one of. A video is a gallery,
    /// so asking for "the" video is a question with no answer — and returning the first one
    /// would put a trailer where a banner goes.
    /// </summary>
    [Fact]
    public void ThereIsNoSuchThingAsTheVideo()
    {
        GameDetail detail = new() { Media = [Media("clip", MediaKind.Video)] };

        Assert.Null(detail.Artwork(MediaKind.Video));
        Assert.Null(detail.Artwork(MediaKind.Screenshot));
    }

    [Fact]
    public void AGameWithNoVideosHasAnEmptyListRatherThanNothing() =>
        Assert.Empty(new GameDetail().Videos);
}
