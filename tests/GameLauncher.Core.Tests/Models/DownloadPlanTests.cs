using GameLauncher.Core.Models;

namespace GameLauncher.Core.Tests.Models;

public sealed class DownloadPlanTests
{
    private static readonly DateTimeOffset Expiry =
        new(2026, 8, 3, 16, 32, 28, TimeSpan.Zero);

    private static PlannedFile File(string path, string sha256, long size = 10) =>
        new() { Path = path, Sha256 = sha256, Size = size, Url = "http://files.example/" + sha256 };

    [Fact]
    public void APlanWithNothingToFetchAndNothingToRemoveMeansTheInstallIsCurrent()
    {
        DownloadPlan plan = new()
        {
            Kind = DownloadKind.Delta,
            Unchanged = [new ManifestEntry { Path = "Game.exe", Sha256 = "53e5", Size = 21 }],
        };

        Assert.True(plan.IsUpToDate);
    }

    // A file that only has to be deleted still makes this an update: skipping the plan would
    // leave the previous version's leftovers behind.
    [Fact]
    public void APlanThatOnlyRemovesSomethingIsNotUpToDate()
    {
        DownloadPlan plan = new() { Remove = ["old.dll"] };

        Assert.False(plan.IsUpToDate);
    }

    [Fact]
    public void TheTargetIsWhatIsFetchedPlusWhatIsAlreadyCorrect()
    {
        DownloadPlan plan = new()
        {
            Files = [File("Game.exe", "53e5", 21)],
            Unchanged = [new ManifestEntry { Path = "data/pak", Sha256 = "8430", Size = 56 }],
            Remove = ["old.dll"],
        };

        Assert.Equal(
            ["Game.exe", "data/pak"],
            plan.TargetFiles.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.Equal(77, plan.TargetFiles.Sum(file => file.Size));
    }

    [Fact]
    public void UrlsAreTreatedAsExpiringBeforeTheyActuallyExpire()
    {
        DownloadPlan plan = new() { UrlsExpireAt = Expiry };

        Assert.False(plan.IsExpiring(Expiry.AddMinutes(-10), TimeSpan.FromMinutes(5)));
        Assert.True(plan.IsExpiring(Expiry.AddMinutes(-2), TimeSpan.FromMinutes(5)));
        Assert.True(plan.IsExpiring(Expiry.AddSeconds(1), TimeSpan.Zero));
    }

    [Fact]
    public void ACopyHintIsRecognisedOnlyWhenItNamesAPath()
    {
        Assert.False(File("Game.exe", "53e5").CanBeCopiedLocally);
        Assert.False((File("Game.exe", "53e5") with { CopyFrom = "" }).CanBeCopiedLocally);
        Assert.True((File("Game.exe", "53e5") with { CopyFrom = "data/pak" }).CanBeCopiedLocally);
    }
}
