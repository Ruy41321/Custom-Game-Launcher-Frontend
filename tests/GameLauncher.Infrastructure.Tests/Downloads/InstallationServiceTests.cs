using System.Security.Cryptography;
using System.Text;
using GameLauncher.Core.Api;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using GameLauncher.Infrastructure.Downloads;
using GameLauncher.Infrastructure.Installs;
using GameLauncher.Infrastructure.Platform;
using GameLauncher.Infrastructure.Tests.Progressing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GameLauncher.Infrastructure.Tests.Downloads;

public sealed class InstallationServiceTests : IDisposable
{
    private const string ExeContent = "the executable";
    private const string PakContent = "the shared asset";

    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _root = new();
    private readonly IDownloadApi _downloadApi = Substitute.For<IDownloadApi>();
    private readonly FakeBlobFetcher _fetcher = new();
    private readonly IDiskSpaceProbe _diskSpace = Substitute.For<IDiskSpaceProbe>();
    private readonly IUserSettingsStore _settings = Substitute.For<IUserSettingsStore>();
    private readonly SqliteInstallStore _store;
    private readonly InstallationService _service;

    public InstallationServiceTests()
    {
        Directory.CreateDirectory(UserData);
        _store = new SqliteInstallStore(Path.Combine(UserData, "launcher.db"));
        _diskSpace.AvailableFreeBytes(Arg.Any<string>()).Returns(long.MaxValue);
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new UserSettings());

        _service = new InstallationService(
            _downloadApi,
            _fetcher,
            _store,
            new PathProvider(userDataDirectory: UserData),
            _diskSpace,
            _settings,
            new FakeTimeProvider(Now),
            NullLogger<InstallationService>.Instance);

        _fetcher.Holds(Sha(ExeContent), ExeContent);
        _fetcher.Holds(Sha(PakContent), PakContent);
    }

    private string UserData => Path.Combine(_root.Path, "userdata");

    private string InstallDirectory => Path.Combine(_root.Path, "install");

    private static string Sha(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static PlannedFile Planned(string path, string content, string? copyFrom = null) =>
        new()
        {
            Path = path,
            Sha256 = Sha(content),
            Size = Encoding.UTF8.GetByteCount(content),
            Executable = path.EndsWith(".exe", StringComparison.Ordinal),
            Url = "http://files.example/" + Sha(content),
            CopyFrom = copyFrom,
        };

    private static DownloadPlan PlanOf(params PlannedFile[] files) => new()
    {
        BuildId = "b1",
        GameId = "g1",
        VersionId = "v1",
        Kind = DownloadKind.Full,
        ManifestSha256 = "86e1",
        Entrypoint = "Game.exe",
        LaunchArgs = "--fullscreen",
        Files = files,
        DownloadBytes = files.Sum(file => file.Size),
        TotalBytes = files.Sum(file => file.Size),
        UrlsExpireAt = Now.AddHours(1),
    };

    private static InstallRequest RequestFor(string? directory, string coverUrl = "") => new()
    {
        Game = new Game
        {
            Id = "g1",
            Slug = "orbital-drift",
            Title = "Orbital Drift",
            CoverUrl = coverUrl,
        },
        Version = new GameVersion { Id = "v1", GameId = "g1", Semver = "0.2.0" },
        Build = new GameBuild
        {
            Id = "b1",
            VersionId = "v1",
            Platform = GamePlatform.Windows,
            Architecture = BuildArchitecture.X64,
            Status = BuildStatus.Ready,
        },
        InstallDirectory = directory,
    };

    private void ServerPlans(DownloadPlan plan, string? from = null) =>
        _downloadApi.GetPlanAsync("b1", from, Arg.Any<CancellationToken>()).Returns(plan);

    public void Dispose()
    {
        _store.Dispose();
        _root.Dispose();
    }

    [Fact]
    public async Task AFreshInstallWritesEveryFileAndRecordsTheRow()
    {
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent), Planned("data/pak", PakContent)));

        InstallResult result = await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            ExeContent,
            await File.ReadAllTextAsync(
                Path.Combine(InstallDirectory, "Game.exe"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            PakContent,
            await File.ReadAllTextAsync(
                Path.Combine(InstallDirectory, "data", "pak"),
                TestContext.Current.CancellationToken));

        Assert.False(result.WasUpdate);
        Assert.Equal(InstallState.Installed, result.Install.State);
        Assert.Equal("Game.exe", result.Install.Entrypoint);
        Assert.Equal("--fullscreen", result.Install.LaunchArgs);
        Assert.Equal("86e1", result.Install.ManifestSha256);
        Assert.Equal(Now, result.Install.InstalledAt);

        InstalledGame? stored = await _store.FindAsync(
            "g1", TestContext.Current.CancellationToken);
        Assert.True(stored?.Is("b1"));
    }

    // Staging is what makes a crash during apply recoverable without downloading the build
    // again, so it survives until the row says the install is finished.
    [Fact]
    public async Task StagingIsClearedOnceTheInstallIsRecorded()
    {
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)));

        await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        string staging = Path.Combine(UserData, "staging");
        Assert.True(
            !Directory.Exists(staging)
            || !Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task AFileThatOnlyMovedIsCopiedFromDiskAndNeverFetched()
    {
        Directory.CreateDirectory(Path.Combine(InstallDirectory, "data"));
        await File.WriteAllTextAsync(
            Path.Combine(InstallDirectory, "data", "pak"),
            PakContent,
            TestContext.Current.CancellationToken);

        await _store.SaveAsync(
            Installed("b0"), TestContext.Current.CancellationToken);

        ServerPlans(
            PlanOf(Planned("assets/pak", PakContent, copyFrom: "data/pak")) with
            {
                Kind = DownloadKind.Delta,
                Unchanged = [new ManifestEntry { Path = "data/pak", Sha256 = Sha(PakContent) }],
                DownloadBytes = 0,
            },
            from: "b0");

        await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(_fetcher.Fetched);
        Assert.Equal(
            PakContent,
            await File.ReadAllTextAsync(
                Path.Combine(InstallDirectory, "assets", "pak"),
                TestContext.Current.CancellationToken));

        // The source of a copy is a path the update keeps, so it is still there afterwards.
        Assert.True(File.Exists(Path.Combine(InstallDirectory, "data", "pak")));
    }

    [Fact]
    public async Task PathsTheBuildDroppedAreDeletedAndTheirEmptyDirectoriesWithThem()
    {
        Directory.CreateDirectory(Path.Combine(InstallDirectory, "old"));
        await File.WriteAllTextAsync(
            Path.Combine(InstallDirectory, "old", "dead.dll"),
            "gone",
            TestContext.Current.CancellationToken);

        await _store.SaveAsync(Installed("b0"), TestContext.Current.CancellationToken);

        ServerPlans(
            PlanOf(Planned("Game.exe", ExeContent)) with { Remove = ["old/dead.dll"] },
            from: "b0");

        await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(InstallDirectory, "old", "dead.dll")));
        Assert.False(Directory.Exists(Path.Combine(InstallDirectory, "old")));
        Assert.True(Directory.Exists(InstallDirectory));
    }

    [Fact]
    public async Task AnUpdateAsksForADeltaFromWhatIsInstalled()
    {
        await _store.SaveAsync(Installed("b0"), TestContext.Current.CancellationToken);
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)), from: "b0");

        InstallResult result = await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.WasUpdate);
        await _downloadApi.Received(1).GetPlanAsync("b1", "b0", Arg.Any<CancellationToken>());
    }

    // A directory left half-applied is not the build its row names, so a delta against it would
    // assume files that may not be there.
    [Theory]
    [InlineData(InstallState.Applying)]
    [InlineData(InstallState.Broken)]
    public async Task AnInstallThatIsNotFinishedIsNeverUsedAsTheSourceOfADelta(InstallState state)
    {
        await _store.SaveAsync(
            Installed("b0") with { State = state }, TestContext.Current.CancellationToken);
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)));

        await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        await _downloadApi.Received(1).GetPlanAsync("b1", null, Arg.Any<CancellationToken>());
    }

    // The manifest owns LaunchArgs and an update rewrites it; the player owns LaunchOptions
    // and an update that discarded them would be a preference that silently resets.
    [Fact]
    public async Task AnUpdateKeepsTheArgumentsThePlayerSet()
    {
        await _store.SaveAsync(
            Installed("b0") with { LaunchOptions = "-windowed" },
            TestContext.Current.CancellationToken);
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)), from: "b0");

        InstallResult result = await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("-windowed", result.Install.LaunchOptions);
        Assert.Equal("--fullscreen", result.Install.LaunchArgs);
    }

    // The row carries the cover so the library has pictures with no server to ask: the artwork
    // cache is indexed by URL and survives offline, and this is what remembers the URL.
    [Fact]
    public async Task TheCoverTheCatalogAdvertisedIsRecordedOnTheRow()
    {
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)));

        InstallResult result = await _service.InstallAsync(
            RequestFor(InstallDirectory, coverUrl: "https://files.example/media/86e1.png"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://files.example/media/86e1.png", result.Install.CoverUrl);

        InstalledGame? stored = await _store.FindAsync(
            "g1", TestContext.Current.CancellationToken);
        Assert.Equal("https://files.example/media/86e1.png", stored?.CoverUrl);
    }

    // A publisher can replace a cover, and an update is where the launcher hears about it.
    [Fact]
    public async Task AnUpdateWithANewCoverWritesIt()
    {
        await _store.SaveAsync(
            Installed("b0") with { CoverUrl = "https://files.example/media/old.png" },
            TestContext.Current.CancellationToken);
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)), from: "b0");

        InstallResult result = await _service.InstallAsync(
            RequestFor(InstallDirectory, coverUrl: "https://files.example/media/new.png"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://files.example/media/new.png", result.Install.CoverUrl);
    }

    // A response that arrived without a cover is not a publisher who removed one. Taking it as
    // one would throw away the only copy of the URL this machine has when there is no server.
    [Fact]
    public async Task AnUpdateWithNoCoverLeavesTheOneAlreadyRecorded()
    {
        await _store.SaveAsync(
            Installed("b0") with { CoverUrl = "https://files.example/media/86e1.png" },
            TestContext.Current.CancellationToken);
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)), from: "b0");

        InstallResult result = await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://files.example/media/86e1.png", result.Install.CoverUrl);
    }

    // Core feature 7 of the plan: the configured directory is where new games go. The field
    // existed from the start and nothing read it, which is the same as not having it.
    [Fact]
    public async Task ANewGameGoesWhereTheUserAskedRatherThanThePlatformDefault()
    {
        string chosen = Path.Combine(_root.Path, "elsewhere");
        _settings.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new UserSettings { InstallDirectory = chosen });

        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)));

        InstallResult result = await _service.InstallAsync(
            RequestFor(null), cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith(chosen, result.Install.InstallDirectory, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(result.Install.InstallDirectory, "Game.exe")));
    }

    // A preference the user can no longer act on — an unplugged drive, a deleted folder — is a
    // worse reason to refuse an install than to quietly use the place that always works.
    [Fact]
    public async Task AnUnusableConfiguredDirectoryFallsBackInsteadOfFailing()
    {
        // A path under a file rather than a directory: creating it cannot succeed.
        string blocked = Path.Combine(_root.Path, "a-file", "games");
        await File.WriteAllTextAsync(
            Path.Combine(_root.Path, "a-file"), "not a directory",
            TestContext.Current.CancellationToken);

        _settings.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new UserSettings { InstallDirectory = blocked });

        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)));

        InstallResult result = await _service.InstallAsync(
            RequestFor(null), cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("a-file", result.Install.InstallDirectory, StringComparison.Ordinal);
        Assert.Equal(InstallState.Installed, result.Install.State);
    }

    // The setting decides where the *next* game lands, not where the ones already on disk are.
    [Fact]
    public async Task AnUpdateStaysInTheDirectoryTheGameIsAlreadyIn()
    {
        await _store.SaveAsync(Installed("b0"), TestContext.Current.CancellationToken);
        _settings.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new UserSettings { InstallDirectory = Path.Combine(_root.Path, "elsewhere") });

        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)), from: "b0");

        InstallResult result = await _service.InstallAsync(
            RequestFor(null), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(InstallDirectory, result.Install.InstallDirectory);
    }

    [Fact]
    public async Task AGameAlreadyAtTheBuildIsLeftAlone()
    {
        await _store.SaveAsync(Installed("b1"), TestContext.Current.CancellationToken);
        ServerPlans(PlanOf() with { Kind = DownloadKind.Delta }, from: "b1");

        InstallResult result = await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.DownloadedBytes);
        Assert.Empty(_fetcher.Fetched);
        Assert.False(Directory.Exists(InstallDirectory));
    }

    [Fact]
    public async Task ThereIsNoInstallWhenThereIsNoRoomForIt()
    {
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)));
        _diskSpace.AvailableFreeBytes(Arg.Any<string>()).Returns(4);

        InsufficientDiskSpaceException exception =
            await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() =>
                _service.InstallAsync(
                    RequestFor(InstallDirectory),
                    cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(4, exception.AvailableBytes);
        Assert.True(exception.RequiredBytes > 4);

        // Checked before anything is written, which is the only moment it is worth anything.
        Assert.Empty(_fetcher.Fetched);
        Assert.False(Directory.Exists(InstallDirectory));
        Assert.Null(await _store.FindAsync("g1", TestContext.Current.CancellationToken));
    }

    // The server validates manifest paths on ingestion. This check is here anyway: a client
    // that writes wherever it is told is one compromised server away from writing into the
    // user's startup folder.
    [Fact]
    public async Task APathThatWouldEscapeTheInstallDirectoryIsRefused()
    {
        ServerPlans(PlanOf(Planned("../escaped.exe", ExeContent)));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            _service.InstallAsync(
                RequestFor(InstallDirectory),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);
        Assert.False(File.Exists(Path.Combine(_root.Path, "escaped.exe")));
    }

    // The blob was verified when it arrived; this covers the one step that check cannot reach,
    // which is the copy out of staging and into the install directory.
    [Fact]
    public async Task AFileThatDoesNotSurviveTheCopyIntactIsRefused()
    {
        PlannedFile file = Planned("Game.exe", ExeContent);
        _fetcher.Holds(file.Sha256, "something else entirely");
        ServerPlans(PlanOf(file));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            _service.InstallAsync(
                RequestFor(InstallDirectory),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);
        Assert.False(File.Exists(Path.Combine(InstallDirectory, "Game.exe")));

        // The row says the directory is unfinished, which is what stops it being run.
        InstalledGame? stored = await _store.FindAsync(
            "g1", TestContext.Current.CancellationToken);
        Assert.Equal(InstallState.Applying, stored?.State);
    }

    [Fact]
    public async Task ProgressRunsFromPlanningToDone()
    {
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent), Planned("data/pak", PakContent)));
        List<DownloadProgress> reports = [];

        await _service.InstallAsync(
            RequestFor(InstallDirectory),
            new ImmediateProgress<DownloadProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(InstallPhase.Planning, reports[0].Phase);
        Assert.Contains(reports, report => report.Phase == InstallPhase.CheckingSpace);
        Assert.Contains(reports, report => report.Phase == InstallPhase.Downloading);
        Assert.Contains(reports, report => report.Phase == InstallPhase.Applying);
        Assert.Equal(InstallPhase.Done, reports[^1].Phase);
        Assert.Equal(1, reports[^1].Fraction);
        Assert.Equal(2, reports[^1].FilesApplied);
    }

    [Fact]
    public async Task UninstallingDeletesTheDirectoryAndTheRowAndSaysWhatItGaveBack()
    {
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent), Planned("data/pak", PakContent)));
        await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        UninstallResult result = await _service.UninstallAsync(
            "g1", TestContext.Current.CancellationToken);

        Assert.Equal(
            Encoding.UTF8.GetByteCount(ExeContent) + Encoding.UTF8.GetByteCount(PakContent),
            result.FreedBytes);
        Assert.False(Directory.Exists(InstallDirectory));
        Assert.Null(await _store.FindAsync("g1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UninstallingSomethingThatIsNotInstalledReportsNothingRatherThanFailing()
    {
        UninstallResult result = await _service.UninstallAsync(
            "g1", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.FreedBytes);
    }

    [Fact]
    public async Task VerifyHashesWhatIsOnDiskAndSendsItToTheServer()
    {
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent), Planned("data/pak", PakContent)));
        await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        List<InstalledFile> sent = [];
        _downloadApi
            .VerifyAsync(
                "b1",
                Arg.Do<IReadOnlyList<InstalledFile>>(files => sent = [.. files]),
                Arg.Any<CancellationToken>())
            .Returns(new IntegrityReport { BuildId = "b1", Intact = true });

        await _service.VerifyAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Game.exe", "data/pak"],
            sent.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.Equal(Sha(ExeContent), sent.Single(file => file.Path == "Game.exe").Sha256);

        InstalledGame? stored = await _store.FindAsync(
            "g1", TestContext.Current.CancellationToken);
        Assert.Equal(Now, stored?.LastVerifiedAt);
        Assert.Equal(InstallState.Installed, stored?.State);
    }

    // A broken install is what turns the next update into a full download, because there is no
    // longer a build on disk to compute a difference from.
    [Fact]
    public async Task AnInstallTheServerCallsBrokenIsRecordedAsSuch()
    {
        ServerPlans(PlanOf(Planned("Game.exe", ExeContent)));
        await _service.InstallAsync(
            RequestFor(InstallDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        _downloadApi
            .VerifyAsync("b1", Arg.Any<IReadOnlyList<InstalledFile>>(), Arg.Any<CancellationToken>())
            .Returns(new IntegrityReport
            {
                BuildId = "b1",
                Intact = false,
                Corrupt = ["Game.exe"],
            });

        IntegrityReport report = await _service.VerifyAsync(
            "g1", TestContext.Current.CancellationToken);

        Assert.False(report.Intact);
        InstalledGame? stored = await _store.FindAsync(
            "g1", TestContext.Current.CancellationToken);
        Assert.Equal(InstallState.Broken, stored?.State);
    }

    // Nothing is applying at startup, so a row that says so is the mark of a crash. Broken is
    // what the directory actually is, and it is the state the game page explains and repairs.
    [Fact]
    public async Task AnInstallLeftMidApplyIsRecordedAsDamaged()
    {
        await _store.SaveAsync(
            Installed("b0") with { State = InstallState.Applying },
            TestContext.Current.CancellationToken);

        RecoveryReport report = await _service.RecoverAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, report.UnfinishedInstalls);
        Assert.True(report.FoundAnything);

        InstalledGame? stored = await _store.FindAsync(
            "g1", TestContext.Current.CancellationToken);
        Assert.Equal(InstallState.Broken, stored?.State);
    }

    [Fact]
    public async Task AFinishedInstallIsLeftAloneByRecovery()
    {
        await _store.SaveAsync(Installed("b0"), TestContext.Current.CancellationToken);

        RecoveryReport report = await _service.RecoverAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(0, report.UnfinishedInstalls);
        Assert.False(report.FoundAnything);

        InstalledGame? stored = await _store.FindAsync(
            "g1", TestContext.Current.CancellationToken);
        Assert.Equal(InstallState.Installed, stored?.State);
    }

    // A partly fetched build is what makes resuming cheap, so recent staging survives.
    [Fact]
    public async Task StagingFromYesterdayIsKeptSoADownloadStillResumes()
    {
        string staged = StageBlob("recent", DateTime.UtcNow.AddDays(-1));

        RecoveryReport report = await _service.RecoverAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(0, report.StagingBytesReclaimed);
        Assert.True(Directory.Exists(staged));
    }

    [Fact]
    public async Task StagingNobodyCameBackForIsReclaimed()
    {
        string staged = StageBlob("ancient", DateTime.UtcNow.AddDays(-30));

        RecoveryReport report = await _service.RecoverAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("abandoned".Length, report.StagingBytesReclaimed);
        Assert.False(Directory.Exists(staged));
    }

    /// <summary>A staging directory for some build, written at the given time.</summary>
    private string StageBlob(string build, DateTime writtenUtc)
    {
        string directory = Path.Combine(UserData, "staging", build);
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, "ab12.part");
        File.WriteAllText(file, "abandoned");
        File.SetLastWriteTimeUtc(file, writtenUtc);
        Directory.SetLastWriteTimeUtc(directory, writtenUtc);

        return directory;
    }

    [Fact]
    public async Task VerifyingAGameThatIsNotInstalledIsANotFound()
    {
        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            _service.VerifyAsync("g1", TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.NotFound, exception.Code);
    }

    private InstalledGame Installed(string buildId) => new()
    {
        GameId = "g1",
        GameSlug = "orbital-drift",
        GameTitle = "Orbital Drift",
        BuildId = buildId,
        VersionId = "v0",
        VersionSemver = "0.1.0",
        Platform = GamePlatform.Windows,
        Architecture = BuildArchitecture.X64,
        InstallDirectory = InstallDirectory,
        Entrypoint = "Game.exe",
        ManifestSha256 = "0000",
        SizeBytes = 10,
        FileCount = 1,
        State = InstallState.Installed,
        InstalledAt = Now.AddDays(-1),
        UpdatedAt = Now.AddDays(-1),
    };
}
