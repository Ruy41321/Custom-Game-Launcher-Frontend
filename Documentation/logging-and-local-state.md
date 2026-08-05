# Logging and local state

Everything the launcher writes to disk: where each thing lives on the three operating systems,
what happens to it when the process dies mid-write, and what is safe to delete.

Implemented in `Core/Platform/IPathProvider.cs`, `Infrastructure/Platform/PathProvider.cs`,
`Infrastructure/Logging/LauncherLogging.cs`, `Infrastructure/Installs/SqliteInstallStore.cs`,
`Infrastructure/Authentication/FileTokenStore.cs`, `Infrastructure/Configuration/JsonUserSettingsStore.cs`
and `Infrastructure/Media/CachingImageLoader.cs`.

---

## No magic paths

**Nothing anywhere in the codebase builds a path from a literal or calls
`Environment.GetFolderPath` at the use site.** Every location comes from `IPathProvider`.

That is a hard rule rather than a convention, and the reason is that Windows, Linux and macOS
disagree about all of them. A single `Path.Combine` with a literal is a bug that only shows up
on the two platforms the maintainer does not run.

It is also what makes the whole client drivable from a test or a console project: registering
an `IPathProvider` pointing at a temporary directory redirects everything at once, instead of
requiring each component to be told separately.

---

## Where things are

```
<user data>/                        %LOCALAPPDATA%\CustomGameLauncher            (Windows)
                                    ~/Library/Application Support/CustomGameLauncher (macOS)
                                    $XDG_DATA_HOME/CustomGameLauncher            (Linux)
                                      falling back to ~/.local/share/…
  launcher.db                       what is installed on this machine (SQLite, WAL)
  session.json                      the stored session
  launcher.settings.json            the user's preferences
  logs/                             launcher-YYYYMMDD.log, crash-*.log
  staging/                          in-flight downloads, content-addressed
  images/                           the artwork cache

<install root>/CustomGameLauncher/Games/<slug>/     the games themselves
```

Two choices in there are deliberate:

- **`%LOCALAPPDATA%` on Windows, not `%APPDATA%`.** Local application data is correctly excluded
  from roaming profiles. Nobody wants gigabytes of game data syncing to a domain server.
- **Games install beside the user's data, never into a system-wide location.** The launcher
  must never need elevation to install something.

### What is safe to delete

| Path | Deleting it costs |
|---|---|
| `images/` | a few thumbnails on the next launch — everything in it is re-fetchable |
| `staging/` | an interrupted download restarts from zero instead of resuming |
| `logs/` | the record of what happened |
| `session.json` | signing in again |
| `launcher.settings.json` | the user's preferences reset to the shipped defaults |
| `launcher.db` | **the launcher forgets what is installed.** The games stay on disk and become invisible to it |

---

## The install database (D21, D4)

One row per game, keyed by the game id, in SQLite with **WAL** enabled.

**Why one row per game.** A game occupies one directory on this machine, so two rows would be
two answers to where it lives. Keying by install directory instead would invite two installs of
one game and answer nothing the launcher actually asks.

**Why a database and not JSON.** The row being written during an update is *precisely* the one
saying the install directory is half of one build and half of another — the moment a
rewrite-in-place corrupts a JSON file is the moment that fact matters most. WAL means a process
killed mid-write leaves a database that **opens**, rather than one that has to be thrown away.

**The schema is versioned in `PRAGMA user_version`** and migrated by appending to an array, each
element bringing the schema from its index to the next. Never edit one that has shipped — the
file remembers how far it has come, so a rewritten migration is one that never runs again on a
machine that already applied it.

Three migrations so far: the table, `launch_options`, and `cover_url`. Both additions are
`ALTER TABLE … ADD COLUMN … NOT NULL DEFAULT ''`, and the default is the whole point — it is
what an **existing row** gets when somebody updates the launcher, which is the case that
actually happens and the one worth a test. An empty string rather than a nullable column,
because a missing value reads as an empty string everywhere else in the model.

**The row carries `cover_url` so the library has pictures offline.** The artwork cache is keyed
by URL and does not need a server; what it needed was somebody who remembered the URL. See
[catalog-and-artwork.md](catalog-and-artwork.md) for the rule that an update never overwrites a
cover with nothing.

**Enums and instants are stored as text**, not as integers or ticks. The file is meant to be
readable with any SQLite tool at the moment the launcher is the thing that is broken, and a
column of `2` tells a person nothing.

Two testing traps, both recorded in `CLAUDE.md` §7 and both worth knowing here:

- `Microsoft.Data.Sqlite` **pools connections**, so the file stays open after the store is
  disposed. A temporary directory holding a test database can refuse to delete. Harmless for
  the suite, which ignores it, but do not assert the file is gone without
  `SqliteConnection.ClearAllPools()`.
- The 9.0.x line is the one that matches the pinned SDK; **10.x needs .NET 10.**

---

## The session file

`session.json`, written through a temporary file and a rename.

**On Unix the permissions are narrowed to the owner before anything is written to it**, not
after — otherwise the token is briefly world-readable, which is the window an attacker would
want. A no-op on Windows, where the per-user directory's ACL already says the same thing.

The token is stored **in clear**, which is a decision with its full reasoning in
[authentication-and-session.md](authentication-and-session.md): every alternative covers one
platform and abandons two, and the exposure here is bounded by rotation and family revocation.

---

## The artwork cache

Content-addressed by the SHA-256 of the URL, so **nothing about a remote name reaches the file
system**. No revalidation and no expiry — artwork is content-addressed server-side, so a cached
entry cannot be stale. Capped at 128 MiB and trimmed to 80% of that on a miss.

Details in [catalog-and-artwork.md](catalog-and-artwork.md).

---

## The staging tree

`staging/<hash of build id>/ab/cd/<sha256>` — the same two-level fan-out the server uses, so a
directory never fills up.

The build-id directory name is **derived** from a hash rather than validated, which means there
is no shape to get wrong, and it is **stable across restarts** — which is what lets an
interrupted transfer be found again by name instead of by remembering it.

Swept by **age** (seven days) at start-up, never emptied. See
[downloads-and-installs.md](downloads-and-installs.md).

---

## Logging (D6)

Serilog, rolling daily files under `logs/`, capped at **20 MB each** and **14 days**.

Three settings are load-bearing:

- **`CultureInfo.InvariantCulture` for the format provider.** Logs are read by developers and
  by diagnostic tooling, never by end users, so they must not change shape with the machine's
  locale. A timestamp that is `,` on one machine and `.` on another is a log two people cannot
  grep the same way.
- **`flushToDiskInterval` of two seconds.** A hard kill or a native crash never reaches
  `CloseAndFlush`, and an empty log file is worthless precisely when something has gone wrong.
- **`rollOnFileSizeLimit`.** A daily file is not a bound; a runaway loop is.

### The global handlers

Installed in `Program.Main`, **before Avalonia starts**, so a failure during initialisation is
still written down.

- **`AppDomain.UnhandledException`** — writes a crash report and flushes. The process is going
  down either way.
- **`TaskScheduler.UnobservedTaskException`** — writes a crash report and calls `SetObserved()`.
  The process is still healthy, and an unawaited failure must not kill the launcher.

Without these the process dies with nothing written down, which is the failure mode that costs
the most to diagnose: a user reports that "it just closed" and there is nothing to read.

### Crash reports

`crash-<utc>-<kind>.log` beside the ordinary logs, carrying the kind, the UTC instant, the OS
version, the runtime version, the app version and the exception.

The write is wrapped in a `try`/`catch (IOException)` that does nothing, because there is
nothing useful left to do: the crash report itself failed to write.

**Nothing is ever transmitted.** Crash reports are written to disk only. The `SendCrashReports`
setting exists in `UserSettings` and is read by nothing.

---

## What is not implemented

- **Crash-report upload.** The opt-in setting exists; there is no uploader, no endpoint on the
  server, and no UI offering it. This is why the Settings page does not show the checkbox.
- **No log viewer in the application.** Finding the log means finding the directory.
- **No telemetry or analytics of any kind** leaves this client. The server records download
  events from the plan it issues; the launcher sends nothing on its own.
- **No self-update, so nothing writes to the application directory.** Everything above is under
  the user's data directory, and the application directory is read-only after install.

## Related documents

- [architecture.md](architecture.md) — the start-up sequence and where logging fits into it
- [authentication-and-session.md](authentication-and-session.md) — why `session.json` is in clear
- [downloads-and-installs.md](downloads-and-installs.md) — the install rows and the staging tree
- [configuration-and-localization.md](configuration-and-localization.md) — the two configuration files
