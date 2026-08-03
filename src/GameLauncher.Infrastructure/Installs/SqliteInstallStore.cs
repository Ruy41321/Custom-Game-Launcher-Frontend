using System.Globalization;
using Dapper;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using Microsoft.Data.Sqlite;

namespace GameLauncher.Infrastructure.Installs;

/// <summary>
/// The installed-games table, in SQLite. Write-ahead logging is on, so a process killed in the
/// middle of an update leaves a database that opens rather than one that has to be thrown away
/// — which matters precisely because the row being written at that moment is the one saying
/// the install directory is half of one build and half of another.
/// </summary>
public sealed class SqliteInstallStore : IInstallStore, IDisposable
{
    /// <summary>
    /// Each element brings the schema from its index to the next, and the file records how far
    /// it has come in <c>PRAGMA user_version</c>. Never edit one that has shipped: append.
    /// </summary>
    private static readonly string[] Migrations =
    [
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
        """,
    ];

    private const string SelectColumns = """
        SELECT game_id           AS GameId,
               game_slug         AS GameSlug,
               game_title        AS GameTitle,
               build_id          AS BuildId,
               version_id        AS VersionId,
               version_semver    AS VersionSemver,
               platform          AS Platform,
               architecture      AS Architecture,
               install_directory AS InstallDirectory,
               entrypoint        AS Entrypoint,
               launch_args       AS LaunchArgs,
               manifest_sha256   AS ManifestSha256,
               size_bytes        AS SizeBytes,
               file_count        AS FileCount,
               state             AS State,
               installed_at      AS InstalledAt,
               updated_at        AS UpdatedAt,
               last_verified_at  AS LastVerifiedAt,
               last_played_at    AS LastPlayedAt
        FROM installs
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public SqliteInstallStore(IPathProvider pathProvider)
        : this(pathProvider.LocalDatabasePath)
    {
    }

    public SqliteInstallStore(string databasePath) =>
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

    public async Task<IReadOnlyList<InstalledGame>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        IEnumerable<InstallRow> rows = await connection.QueryAsync<InstallRow>(
            new CommandDefinition(
                SelectColumns + " ORDER BY game_title", cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return [.. rows.Select(row => row.ToInstall())];
    }

    public async Task<InstalledGame?> FindAsync(
        string gameId, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        InstallRow? row = await connection.QuerySingleOrDefaultAsync<InstallRow>(
            new CommandDefinition(
                SelectColumns + " WHERE game_id = @gameId",
                new { gameId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row?.ToInstall();
    }

    public async Task SaveAsync(
        InstalledGame install, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO installs (
                game_id, game_slug, game_title, build_id, version_id, version_semver,
                platform, architecture, install_directory, entrypoint, launch_args,
                manifest_sha256, size_bytes, file_count, state,
                installed_at, updated_at, last_verified_at, last_played_at)
            VALUES (
                @GameId, @GameSlug, @GameTitle, @BuildId, @VersionId, @VersionSemver,
                @Platform, @Architecture, @InstallDirectory, @Entrypoint, @LaunchArgs,
                @ManifestSha256, @SizeBytes, @FileCount, @State,
                @InstalledAt, @UpdatedAt, @LastVerifiedAt, @LastPlayedAt)
            ON CONFLICT (game_id) DO UPDATE SET
                game_slug         = excluded.game_slug,
                game_title        = excluded.game_title,
                build_id          = excluded.build_id,
                version_id        = excluded.version_id,
                version_semver    = excluded.version_semver,
                platform          = excluded.platform,
                architecture      = excluded.architecture,
                install_directory = excluded.install_directory,
                entrypoint        = excluded.entrypoint,
                launch_args       = excluded.launch_args,
                manifest_sha256   = excluded.manifest_sha256,
                size_bytes        = excluded.size_bytes,
                file_count        = excluded.file_count,
                state             = excluded.state,
                installed_at      = excluded.installed_at,
                updated_at        = excluded.updated_at,
                last_verified_at  = excluded.last_verified_at,
                last_played_at    = excluded.last_played_at
            """,
            InstallRow.From(install),
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task RemoveAsync(string gameId, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM installs WHERE game_id = @gameId",
            new { gameId },
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public void Dispose() => _schemaLock.Dispose();

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (_schemaReady)
        {
            return connection;
        }

        try
        {
            await MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task MigrateAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await _schemaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            // WAL survives a process that dies mid-write, which is the whole reason the install
            // state is in a database rather than in a file that gets rewritten.
            await connection.ExecuteAsync("PRAGMA journal_mode = WAL").ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA foreign_keys = ON").ConfigureAwait(false);

            int applied = await connection
                .ExecuteScalarAsync<int>("PRAGMA user_version").ConfigureAwait(false);

            for (int version = applied; version < Migrations.Length; version++)
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await connection.ExecuteAsync(Migrations[version], transaction: transaction)
                    .ConfigureAwait(false);

                // A PRAGMA takes no parameters, so the number is interpolated. It is a loop
                // index over a compile-time array, never anything a caller supplies.
                await connection.ExecuteAsync(
                    "PRAGMA user_version = " + (version + 1).ToString(CultureInfo.InvariantCulture),
                    transaction: transaction).ConfigureAwait(false);
                transaction.Commit();
            }

            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    /// <summary>
    /// The database's own vocabulary. Enums and instants are stored as text so the file can be
    /// read with any SQLite tool when something has gone wrong and the launcher is not the
    /// thing looking at it.
    /// </summary>
    private sealed class InstallRow
    {
        public string GameId { get; init; } = string.Empty;

        public string GameSlug { get; init; } = string.Empty;

        public string GameTitle { get; init; } = string.Empty;

        public string BuildId { get; init; } = string.Empty;

        public string VersionId { get; init; } = string.Empty;

        public string VersionSemver { get; init; } = string.Empty;

        public string Platform { get; init; } = string.Empty;

        public string Architecture { get; init; } = string.Empty;

        public string InstallDirectory { get; init; } = string.Empty;

        public string Entrypoint { get; init; } = string.Empty;

        public string LaunchArgs { get; init; } = string.Empty;

        public string ManifestSha256 { get; init; } = string.Empty;

        public long SizeBytes { get; init; }

        public int FileCount { get; init; }

        public string State { get; init; } = string.Empty;

        public string InstalledAt { get; init; } = string.Empty;

        public string UpdatedAt { get; init; } = string.Empty;

        public string? LastVerifiedAt { get; init; }

        public string? LastPlayedAt { get; init; }

        public static InstallRow From(InstalledGame install) => new()
        {
            GameId = install.GameId,
            GameSlug = install.GameSlug,
            GameTitle = install.GameTitle,
            BuildId = install.BuildId,
            VersionId = install.VersionId,
            VersionSemver = install.VersionSemver,
            Platform = install.Platform.ToString(),
            Architecture = install.Architecture.ToString(),
            InstallDirectory = install.InstallDirectory,
            Entrypoint = install.Entrypoint,
            LaunchArgs = install.LaunchArgs,
            ManifestSha256 = install.ManifestSha256,
            SizeBytes = install.SizeBytes,
            FileCount = install.FileCount,
            State = install.State.ToString(),
            InstalledAt = Text(install.InstalledAt),
            UpdatedAt = Text(install.UpdatedAt),
            LastVerifiedAt = install.LastVerifiedAt is { } verified ? Text(verified) : null,
            LastPlayedAt = install.LastPlayedAt is { } played ? Text(played) : null,
        };

        public InstalledGame ToInstall() => new()
        {
            GameId = GameId,
            GameSlug = GameSlug,
            GameTitle = GameTitle,
            BuildId = BuildId,
            VersionId = VersionId,
            VersionSemver = VersionSemver,
            Platform = Enum.Parse<GamePlatform>(Platform, ignoreCase: true),
            Architecture = Enum.Parse<BuildArchitecture>(Architecture, ignoreCase: true),
            InstallDirectory = InstallDirectory,
            Entrypoint = Entrypoint,
            LaunchArgs = LaunchArgs,
            ManifestSha256 = ManifestSha256,
            SizeBytes = SizeBytes,
            FileCount = FileCount,
            State = Enum.Parse<InstallState>(State, ignoreCase: true),
            InstalledAt = Instant(InstalledAt),
            UpdatedAt = Instant(UpdatedAt),
            LastVerifiedAt = LastVerifiedAt is null ? null : Instant(LastVerifiedAt),
            LastPlayedAt = LastPlayedAt is null ? null : Instant(LastPlayedAt),
        };

        private static string Text(DateTimeOffset instant) =>
            instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        private static DateTimeOffset Instant(string text) =>
            DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
