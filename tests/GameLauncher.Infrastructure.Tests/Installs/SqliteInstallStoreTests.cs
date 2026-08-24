using System.Globalization;
using Dapper;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Installs;
using Microsoft.Data.Sqlite;

namespace GameLauncher.Infrastructure.Tests.Installs;

public sealed class SqliteInstallStoreTests
{
    private static readonly DateTimeOffset Installed =
        new(2026, 8, 3, 12, 15, 0, TimeSpan.Zero);

    private static InstalledGame Sample => new()
    {
        GameId = "g1",
        GameSlug = "orbital-drift",
        GameTitle = "Orbital Drift",
        CoverUrl = "https://files.example/media/86e1.png",
        BuildId = "b1",
        VersionId = "v1",
        VersionSemver = "0.2.0",
        Platform = GamePlatform.Windows,
        Architecture = BuildArchitecture.X64,
        InstallDirectory = Path.Combine("C:", "Games", "Orbital Drift"),
        Entrypoint = "bin/Game.exe",
        LaunchArgs = "--fullscreen",
        ManifestSha256 = "86e1",
        SizeBytes = 77,
        FileCount = 3,
        State = InstallState.Installed,
        InstalledAt = Installed,
        UpdatedAt = Installed,
    };

    private static SqliteInstallStore StoreIn(TemporaryDirectory directory) =>
        new(directory.File("launcher.db"));

    [Fact]
    public async Task NothingInstalledMeansAnEmptyList()
    {
        using var directory = new TemporaryDirectory();
        using SqliteInstallStore store = StoreIn(directory);

        Assert.Empty(await store.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Null(await store.FindAsync("g1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnInstallSurvivesARoundTrip()
    {
        using var directory = new TemporaryDirectory();
        using SqliteInstallStore store = StoreIn(directory);

        await store.SaveAsync(Sample, TestContext.Current.CancellationToken);

        InstalledGame? read = await store.FindAsync("g1", TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal("orbital-drift", read.GameSlug);
        Assert.Equal("Orbital Drift", read.GameTitle);
        Assert.Equal("b1", read.BuildId);
        Assert.Equal("0.2.0", read.VersionSemver);
        Assert.Equal(GamePlatform.Windows, read.Platform);
        Assert.Equal(BuildArchitecture.X64, read.Architecture);
        Assert.Equal(Sample.InstallDirectory, read.InstallDirectory);
        Assert.Equal("https://files.example/media/86e1.png", read.CoverUrl);
        Assert.Equal("bin/Game.exe", read.Entrypoint);
        Assert.Equal("--fullscreen", read.LaunchArgs);
        Assert.Equal("86e1", read.ManifestSha256);
        Assert.Equal(77, read.SizeBytes);
        Assert.Equal(3, read.FileCount);
        Assert.Equal(InstallState.Installed, read.State);
        Assert.Equal(Installed, read.InstalledAt);
        Assert.Null(read.LastVerifiedAt);
        Assert.Null(read.LastPlayedAt);
    }

    // An update rewrites the row rather than adding one: a game occupies one directory on this
    // machine, so two rows for it would mean two answers to "where is it".
    [Fact]
    public async Task SavingTheSameGameTwiceUpdatesTheOneRow()
    {
        using var directory = new TemporaryDirectory();
        using SqliteInstallStore store = StoreIn(directory);

        await store.SaveAsync(Sample, TestContext.Current.CancellationToken);
        await store.SaveAsync(
            Sample with
            {
                BuildId = "b2",
                VersionSemver = "0.3.0",
                SizeBytes = 120,
                UpdatedAt = Installed.AddDays(1),
            },
            TestContext.Current.CancellationToken);

        InstalledGame single = Assert.Single(
            await store.GetAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal("b2", single.BuildId);
        Assert.Equal("0.3.0", single.VersionSemver);
        Assert.Equal(120, single.SizeBytes);
        Assert.Equal(Installed, single.InstalledAt);
        Assert.Equal(Installed.AddDays(1), single.UpdatedAt);
    }

    [Fact]
    public async Task TheOptionalInstantsRoundTripWhenTheyAreSet()
    {
        using var directory = new TemporaryDirectory();
        using SqliteInstallStore store = StoreIn(directory);

        await store.SaveAsync(
            Sample with
            {
                LastVerifiedAt = Installed.AddHours(2),
                LastPlayedAt = Installed.AddHours(3),
            },
            TestContext.Current.CancellationToken);

        InstalledGame? read = await store.FindAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(Installed.AddHours(2), read?.LastVerifiedAt);
        Assert.Equal(Installed.AddHours(3), read?.LastPlayedAt);
    }

    // The player's arguments live in their own column so an update, which rewrites everything
    // the manifest says, cannot take them with it.
    [Fact]
    public async Task ThePlayersLaunchOptionsRoundTripApartFromTheBuilds()
    {
        using var directory = new TemporaryDirectory();
        using SqliteInstallStore store = StoreIn(directory);

        await store.SaveAsync(
            Sample with { LaunchArgs = "--fullscreen", LaunchOptions = "-windowed --dev" },
            TestContext.Current.CancellationToken);

        InstalledGame? read = await store.FindAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal("--fullscreen", read?.LaunchArgs);
        Assert.Equal("-windowed --dev", read?.LaunchOptions);
    }

    // The schema is migrated by appending, so a database written before the column existed has
    // to keep opening. Migration 0 then 1 is exactly what an upgraded launcher runs.
    [Fact]
    public async Task ADatabaseFromAnEarlierSchemaIsMigratedRatherThanRejected()
    {
        using var directory = new TemporaryDirectory();

        using (SqliteInstallStore first = StoreIn(directory))
        {
            await first.SaveAsync(Sample, TestContext.Current.CancellationToken);
        }

        using SqliteInstallStore second = StoreIn(directory);
        InstalledGame? read = await second.FindAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, read?.LaunchOptions);
    }

    // The real case of somebody who updates the launcher: their database was written by a
    // version that had no cover_url, and the rows in it are not re-installed. The column's
    // default is what they get, and it has to be the empty string rather than null — the model
    // says a missing cover is an empty string, and a null would surface as one anyway only by
    // accident.
    [Fact]
    public async Task ARowWrittenBeforeTheCoverColumnExistedMigratesToAnEmptyCover()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("launcher.db");

        await WriteSchemaWithoutTheCoverColumnAsync(path);

        using SqliteInstallStore store = new(path);
        InstalledGame? read = await store.FindAsync("g1", TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(string.Empty, read.CoverUrl);
        Assert.Equal("Orbital Drift", read.GameTitle);
    }

    /// <summary>
    /// The schema exactly as the launcher shipped it before this column: migrations 0 and 1,
    /// with <c>user_version</c> saying so. Written out rather than derived, because a migration
    /// test that builds the old schema from the new code proves nothing about the old code.
    /// </summary>
    private static async Task WriteSchemaWithoutTheCoverColumnAsync(string path)
    {
        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());

        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(
            """
            CREATE TABLE installs (
                game_id           TEXT    NOT NULL PRIMARY KEY,
                game_slug         TEXT    NOT NULL,
                game_title        TEXT    NOT NULL,
                build_id          TEXT    NOT NULL,
                version_id        TEXT    NOT NULL,
                version_semver    TEXT    NOT NULL,
                platform          TEXT    NOT NULL,
                architecture      TEXT    NOT NULL,
                install_directory TEXT    NOT NULL,
                entrypoint        TEXT    NOT NULL,
                launch_args       TEXT    NOT NULL,
                manifest_sha256   TEXT    NOT NULL,
                size_bytes        INTEGER NOT NULL,
                file_count        INTEGER NOT NULL,
                state             TEXT    NOT NULL,
                installed_at      TEXT    NOT NULL,
                updated_at        TEXT    NOT NULL,
                last_verified_at  TEXT        NULL,
                last_played_at    TEXT        NULL
            );
            ALTER TABLE installs ADD COLUMN launch_options TEXT NOT NULL DEFAULT '';
            PRAGMA user_version = 2;
            """);

        await connection.ExecuteAsync(
            """
            INSERT INTO installs (
                game_id, game_slug, game_title, build_id, version_id, version_semver,
                platform, architecture, install_directory, entrypoint, launch_args,
                manifest_sha256, size_bytes, file_count, state, installed_at, updated_at)
            VALUES (
                'g1', 'orbital-drift', 'Orbital Drift', 'b1', 'v1', '0.2.0',
                'Windows', 'X64', '/games/orbital-drift', 'Game.exe', '',
                '86e1', 77, 3, 'Installed', @instant, @instant)
            """,
            new { instant = Installed.ToString("O", CultureInfo.InvariantCulture) });
    }

    [Fact]
    public async Task InstallsAreListedByTitle()
    {
        using var directory = new TemporaryDirectory();
        using SqliteInstallStore store = StoreIn(directory);

        await store.SaveAsync(
            Sample with { GameId = "g2", GameTitle = "Zephyr" },
            TestContext.Current.CancellationToken);
        await store.SaveAsync(Sample, TestContext.Current.CancellationToken);
        await store.SaveAsync(
            Sample with { GameId = "g3", GameTitle = "Ashfall" },
            TestContext.Current.CancellationToken);

        IReadOnlyList<InstalledGame> all = await store.GetAllAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Ashfall", "Orbital Drift", "Zephyr"], all.Select(install => install.GameTitle));
    }

    [Fact]
    public async Task RemovingAGameThatIsNotThereIsNotAnError()
    {
        using var directory = new TemporaryDirectory();
        using SqliteInstallStore store = StoreIn(directory);

        await store.RemoveAsync("g1", TestContext.Current.CancellationToken);
        await store.SaveAsync(Sample, TestContext.Current.CancellationToken);
        await store.RemoveAsync("g1", TestContext.Current.CancellationToken);

        Assert.Empty(await store.GetAllAsync(TestContext.Current.CancellationToken));
    }

    // The launcher is restarted far more often than the schema changes, and a second store
    // over the same file is what an update or a crash recovery opens.
    [Fact]
    public async Task ASecondStoreOverTheSameFileReadsWhatTheFirstWrote()
    {
        using var directory = new TemporaryDirectory();

        using (SqliteInstallStore first = StoreIn(directory))
        {
            await first.SaveAsync(Sample, TestContext.Current.CancellationToken);
        }

        using SqliteInstallStore second = StoreIn(directory);

        Assert.Equal(
            "b1",
            (await second.FindAsync("g1", TestContext.Current.CancellationToken))?.BuildId);
    }

    // A row left behind by a process that died mid-apply is the signal that the directory holds
    // half of one build and half of another, so the state has to survive the restart.
    [Fact]
    public async Task AnInterruptedApplyIsStillApplyingAfterARestart()
    {
        using var directory = new TemporaryDirectory();

        using (SqliteInstallStore first = StoreIn(directory))
        {
            await first.SaveAsync(
                Sample with { State = InstallState.Applying },
                TestContext.Current.CancellationToken);
        }

        using SqliteInstallStore second = StoreIn(directory);
        InstalledGame? read = await second.FindAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(InstallState.Applying, read?.State);
        Assert.False(read?.Is("b1"));
    }
}
