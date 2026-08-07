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
  updates/                          a downloaded launcher release, one version at a time

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
| `updates/` | nothing — a downloaded launcher release is offered again on the next start |
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

## The update directory

`updates/<version>/<sha256>.zip` — a launcher release that has been fetched and whose bytes hash
to the content address inside its signed document. Nothing takes that name until it verifies, and
a partial transfer is a `.part` file that is deleted rather than resumed.

It is under the user's data directory and **never beside the executable**, because the
application directory is read-only after install and is the very thing an update replaces. One
version at a time: fetching a newer release sweeps the older directory away.

The directory is created when a download starts rather than at start-up, so a build with no
signing key compiled in — which checks for nothing — never creates it. See
[self-update.md](self-update.md).

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

`crash-<utc>-<kind>.json` beside the ordinary logs. **The file is the request body**: one JSON
document in exactly the shape `POST /api/v1/crash-reports` accepts, so there is nothing to parse
back and no second definition of the same facts to keep in step. It is still readable — the
document is indented — and the rolling log beside it carries the same exception in full.

It carries the kind, the instant, the launcher version, the OS and runtime, the exception type,
the message and the full `ToString()` — which is used rather than `StackTrace` because it
includes the inner exceptions, and those are usually the ones that say what went wrong.

The write is wrapped in a `catch` that does nothing, because there is nothing useful left to do:
the crash report itself failed to write.

#### Redacted where it is written, not where it is sent

`CrashReportRedactor` replaces this machine's user profile, data and default install
directories with `<redacted>` **before the file is written**, and a narrower backstop catches a
home directory that is not this machine's — one baked into a build by whoever compiled it, or a
second profile on the same box.

Doing it here rather than at upload time is the whole point. The file on disk is what gets sent,
so redacting later would leave the unredacted copy sitting in the log directory of a machine
whose owner asked for the opposite — and would mean the thing somebody could review was not the
thing that travelled.

What is left still says which file it was: only the prefix goes, never the rest of the path.

This is a reduction of risk and not a guarantee — a message can carry anything a caller put in
it. That is exactly why **the server stores no account against a report either**: two partial
measures that fail differently, rather than one that is trusted. See the server repository's
`Documentation/crash-reports.md`.

#### Sending them

`CrashReportUploader` runs **once at startup**, before the session is restored. A crash report
is written by a process that is dying, so the run that can send it is always the next one; there
is no queue and no retry timer, and a report that could not be sent is simply still on disk when
the launcher next starts.

| Situation | What happens to the file |
|---|---|
| `SendCrashReports` is off | **Deleted.** Not merely "not sent": somebody who said no should not have a growing pile of unsent crash reports about them on their own disk |
| The server does not accept reports | Deleted — carrying them forever for a server that will never take them is carrying them for nothing |
| The server could not be reached, or asked us to slow down | Kept, and the sweep stops there: the rest would fail the same way, and burning the rate limit on them would delay the report that matters |
| The server refused it outright | Deleted. It will be refused identically forever |
| The file is truncated or is not a report | Deleted, so one bad file cannot block every real report behind it |

At most **five** are sent per start, oldest first: a launcher that crashed thirty times
overnight has one bug, not thirty, and the file name begins with the timestamp so the sort is
the order the crashes happened in.

It **never throws**. A launcher that failed to start because it could not report a previous
failure would be the worst possible outcome of this feature.

The consent checkbox is on the Settings page and saves as soon as it is toggled, like the theme:
a consent checkbox that needs a second press to take effect is one somebody will believe they
set. It appears there now because it finally does something — until the uploader existed, an
inert checkbox would have been a promise the launcher did not keep.

---

## What is not implemented

- **No log viewer in the application.** Finding the log means finding the directory.
- **No telemetry or analytics of any kind** leaves this client beyond an opted-in crash report.
  There is no usage tracking, no session reporting and no periodic beacon: the launcher sends
  something about itself only when it has crashed and been told it may.
- **No way to review a pending crash report before it is sent.** The files are readable JSON in
  the log directory, which is the honest version of that — but nothing in the UI shows them.
- **No log attachment.** A crash report is one exception, not a session.
- **Nothing writes to the application directory.** Everything above is under the user's data
  directory, and the application directory is read-only after install — including a downloaded
  launcher release, which waits in `updates/` because replacing an installation is the updater's
  job and the updater does not do it yet ([self-update.md](self-update.md)).

## Related documents

- [architecture.md](architecture.md) — the start-up sequence and where logging fits into it
- [authentication-and-session.md](authentication-and-session.md) — why `session.json` is in clear
- [downloads-and-installs.md](downloads-and-installs.md) — the install rows and the staging tree
- [configuration-and-localization.md](configuration-and-localization.md) — the two configuration files
- [self-update.md](self-update.md) — what lands in `updates/`, and why it stays there
