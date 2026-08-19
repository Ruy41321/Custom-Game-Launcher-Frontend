# Contributing

You have just cloned this. This page is the on-ramp: what the whole system is, how to get it
running, how it is put together, and the handful of rules that will otherwise cost you an
afternoon each. It is deliberately the *only* document you need before your first change.

If you want to **run and distribute your own launcher** rather than change this one, you want
[DISTRIBUTING.md](DISTRIBUTING.md) instead.

---

## 1. The whole system in five minutes

There are **two repositories**, and neither is useful alone.

| | What it is | Language |
|---|---|---|
| [Custom-Game-Launcher-Frontend](https://github.com/Ruy41321/Custom-Game-Launcher-Frontend) (this one) | The desktop launcher a player installs | C# / .NET 9 / Avalonia |
| [Custom-Game-Launcher-Backend](https://github.com/Ruy41321/Custom-Game-Launcher-Backend) | The API, the database and the file server | C++20 / Drogon / PostgreSQL |

They target **Windows and Linux** for the client and **Linux** for the server. macOS was
dropped as a target on 2026-08-07 (D59 in [CLAUDE.md](CLAUDE.md)) — the reasoning is there, and
the short version is that a platform nobody can run is a platform nobody can honestly claim.

### What it does

A hobbyist developer publishes a build of their game; a handful of testers install it and get
updates. That is the whole product. Everything below exists to make those two sentences work
without zip files on Discord.

### The three mechanisms worth understanding before anything else

**Content-addressed storage, and therefore delta updates.** Every file of every build is stored
once, named after its SHA-256. A build's *manifest* is a list of `(path → hash, size,
executable bit)`. Updating from any version to any other is therefore a **set difference of two
manifests**, computed on demand — not a chain of patch files. Unchanged files are never
re-uploaded and never re-downloaded, and two files with identical content are one blob. Read
`Documentation/downloads-and-installs.md` here and `builds-and-uploads.md` on the server.

**Nothing is trusted because it arrived over the network.** A manifest is verified against its
published hash *before* it is parsed. A path from a manifest is resolved inside the install
directory and refused if it escapes. A downloaded blob does not take its content-addressed name
until its bytes hash to it. This is not defensive habit — it is the rule the whole client is
built on, written down as D19, D24 and D40.

**A launcher update is signed, and this server cannot forge one.** Releases of the launcher
itself are announced by a **document** signed with ECDSA P-256, and the private key is not in
either repository, not in CI, and not on the server. The property that buys is the one worth
remembering:

> Somebody who takes the server, its database and its disks can stop launchers from updating.
> They cannot make them update to anything.

`Documentation/self-update.md` is the whole of it.

### The shape of a request

```
launcher  ──HTTPS──►  API :8080          (JSON, JWT, /api/v1/*)
    │                    │
    │                    └── mints a signed URL for a blob
    └──HTTPS──────────►  file server :8081   (nginx, secure_link + HTTP Range)
```

Downloads never occupy an API worker: the API signs a URL, nginx validates the signature with
no callback and serves `Range` natively.

---

## 2. Getting it running

### The server, once

You need Docker with Compose v2. Nothing else — the toolchain lives in the image.

```bash
git clone https://github.com/Ruy41321/Custom-Game-Launcher-Backend && cd Custom-Game-Launcher-Backend && cp .env.example .env
```

```bash
docker compose up --build -d
```

The first build compiles Drogon from source and takes a while; later ones are incremental. Then:

```bash
curl -s http://localhost:8080/api/v1/health
```

The development stack also starts a **mail catcher** at <http://localhost:8025>, so registration
and password recovery work end to end with no relay of your own.

### The client

```bash
git clone https://github.com/Ruy41321/Custom-Game-Launcher-Frontend && cd Custom-Game-Launcher-Frontend && dotnet restore GameLauncher.sln
```

```bash
./scripts/dev.ps1
```

`dev.ps1` does two things a bare `dotnet run` leaves you to discover the slow way: it tells you
whether the API is actually answering **before** the window opens — the client works offline by
design, so a stopped backend produces a sign-in screen that says the server cannot be reached
rather than
an error — and `-Reset` clears the per-user state, which is the only way back to a first-run
launcher. On Linux, `dotnet run --project src/GameLauncher.App`.

### Setting up on a machine that has never seen this project

Cloning the two repositories does **not** give you a working environment, and what is missing is
missing on purpose. Here is the whole list, so nothing is discovered by hitting it.

**Not in version control, and what to do with each:**

| Missing | What it is | What to do |
|---|---|---|
| The backend's `.env` | Secrets and per-machine settings | `cp .env.example .env`. Development runs on the placeholders; only a deployment needs real ones |
| Docker volumes | The database, blobs, artwork, releases | Recreated empty on first `docker compose up`. Migrations run at start-up |
| Accounts, games, builds | Everything you seeded by hand | Register again, `--grant-role` again |
| A release signing key | Signs launcher updates | **Regenerate it** — see below |
| `HANDOFF.md` | A briefing between working sessions, kept outside both repositories so it never lands in a commit | Carry it yourself: a private repository, a gist, a USB stick. It is one file and nothing depends on it |

**Regenerate the development signing key rather than carrying it.** It only ever signs releases
for a `docker compose` stack on your own machine, and that stack is being rebuilt from nothing
anyway. Two commands, and the public half goes into the new `.env`:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out release-signing.key
```

```bash
openssl ec -in release-signing.key -pubout -outform DER | openssl base64 -A
```

The **only** case where a key has to travel is one where launchers are already in somebody
else's hands: every one of them carries the matching public half compiled in, so a new key signs
releases none of them will accept. That is a distribution key, not a development one, and
[DISTRIBUTING.md](DISTRIBUTING.md) treats it accordingly.

### Working on Linux

Both halves are developed on Linux as readily as on Windows — the server more so, since it *is*
Linux. Four things to know.

**The SDK band is pinned.** `global.json` asks for 9.0.310 with `rollForward: latestPatch`, which
accepts any **9.0.3xx** and refuses 9.0.1xx or 9.0.4xx with "compatible SDK version not found".
That pin exists because a newer SDK ships new analyzer rules and `TreatWarningsAsErrors` turns
those into a red build that cannot be reproduced locally (D13). Install a matching one rather
than editing the pin:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 9.0.310
```

**Install PowerShell 7 for the scripts.** All three of `scripts/*.ps1` carry
`#!/usr/bin/env pwsh` and the only one with a platform branch already handles Linux, so they run
as documented once `pwsh` is on the machine. Without it, the underlying commands are in
`CLAUDE.md` §6 here and §7 in the server repository — `dotnet run --project src/GameLauncher.App`
and `docker compose up --build -d` cover the common cases.

**Docker is Engine plus the compose plugin**, not Desktop, and your user needs to be in the
`docker` group or every command needs `sudo`.

**A whole class of traps disappears, and one capability goes with it.** Everything in `CLAUDE.md`
§7 about Windows PowerShell 5.1 — the BOM from `Set-Content -Encoding utf8`, double quotes
mangled on the way to `git commit`, `Compress-Archive` writing backslashes into zip entries — and
everything in the server's §8 about `MSYS_NO_PATHCONV` simply does not apply. `openssl` is on the
path natively.

What you lose is the **UI Automation recipe**: driving the running launcher from a script to read
its labels and press its buttons is Windows-only, and it is how the sign-in recovery buttons, the
language switch and the whole self-update were verified. There is no set-up equivalent here.
On Linux the window is verified by looking at it, which is slower and worth budgeting for —
AT-SPI would be the equivalent and nothing in this repository uses it.

> **One thing genuinely worth doing on a first Linux session**, because it has never been done:
> the self-update swap has only ever been exercised on real Windows. The executable-bit handling
> that makes it work on Linux is covered by tests that skip themselves on Windows, so the first
> person with a Linux machine should publish a release to a local stack and watch a launcher
> replace itself — and watch a deliberately broken one get rolled back.

### Becoming a publisher

Register in the window, then grant yourself the role from the server:

```bash
docker compose exec api /app/launcher-api --grant-role you@example.com dev
```

Sign out and back in — permissions live in the access token, so an existing one does not learn
about a new role. The **Developer** tab appears.

---

## 3. How the client is put together

```
src/GameLauncher.Core            Domain models, service interfaces, pure logic.
                                 References nothing. No Avalonia, no HttpClient,
                                 no SQLite, and no file system.

src/GameLauncher.Infrastructure  The implementations: API clients, download engine,
                                 SQLite store, image cache, platform paths.  → Core

src/GameLauncher.App             Avalonia views, view models, composition root.
                                                                → Core, Infrastructure

src/GameLauncher.Updater         A separate executable that replaces the installation
                                 while the launcher is closed.  → Core
```

**The one rule that matters: Core references nothing.** If a type needs `HttpClient`, Avalonia
or the file system, an *interface* for it goes in Core and the type goes in Infrastructure or
App. `IImageLoader` is in Core, `CachingImageLoader` is in Infrastructure. `IGameLauncher` is in
Core, `ProcessGameLauncher` is in Infrastructure.

That is not tidiness. It is what makes the interesting logic testable in milliseconds without a
UI toolkit, a server or a disk — and the moment a view-model test needs an initialised Avalonia
is the moment view-model tests stop being written.

The same rule produces the repository's favourite shape, which you will see over and over:
**a pure decision plus a thin shell.** `LaunchPlanner` decides whether a game can start and
`ProcessGameLauncher` starts it. `RelaunchWatch.Judge` decides whether an update succeeded and
`SwapRunner` moves the directories. When you add something hard to test, look for the decision
inside it and move that into Core.

### MVVM, and why navigation runs one way

Views are `.axaml` with a code-behind containing nothing but `InitializeComponent`. View models
derive from `ViewModelBase` and use the CommunityToolkit source generators
(`[ObservableProperty]`, `[RelayCommand]`). Dependencies arrive through the **constructor** —
there is no service locator.

**The shell knows its children; the children raise events.** A page that wants to open another
raises an event rather than holding a navigator. A child holding a navigator that holds the
child cannot be constructed in a test without building the whole graph, and a view model that is
expensive to construct is one whose tests get skipped.

---

## 4. Adding something: a worked example

Say you want a "recently played" list on the library page.

1. **Model and interface in Core.** If it needs a new server call, the method goes on the right
   Core interface — `ICatalogApi` for the catalog, `IPublishingApi` for anything needing a
   publisher's permission, and never both. If it is local state, it goes on `IInstallStore`.
2. **Implementation in Infrastructure.** The API client parses the response into the Core type.
   A new SQLite column is added by **appending** a migration to the array in
   `SqliteInstallStore`, never by editing an existing one — a database written by an older
   launcher has to open.
3. **View model in App**, taking its dependencies in the constructor. If it is a new page,
   remember the `ViewLocator` strips the literal text `ViewModel`: the view for
   `RecentlyPlayedViewModel` is `Views/RecentlyPlayed.axaml`, **not** `RecentlyPlayedView` —
   which compiles and renders "View not found" at run time.
4. **Strings in three `.resx` files**, English, Italian and French, in one pass. Two convention
   tests fail if a language is short a key, and another fails on a literal `Text=` or `Content=`
   in any `.axaml`.
5. **Register anything new in the DI graph** and add the interface to the theory list in
   `ServiceCollectionExtensionsTests`. That test is what turns a broken graph into a red build
   instead of a blank window on somebody's machine.
6. **Tests in the same commit.** Not negotiable — see below.

---

## 5. Testing

Four projects, and which one a test belongs in follows from the layering:

| Project | Covers | Substitutes |
|---|---|---|
| `GameLauncher.Core.Tests` | Domain and service logic, no I/O | NSubstitute |
| `GameLauncher.Infrastructure.Tests` | API client (against a stub handler), download planner, SQLite store, config | temp directories, stub `HttpMessageHandler` |
| `GameLauncher.App.Tests` | View models as plain objects | NSubstitute, and the **real** localization service |
| `GameLauncher.Updater.Tests` | The swap, against real directories | a substituted process starter and clock |
| `GameLauncher.Views.Tests` | That every view can be constructed at all (D68) | nothing — it is the one project with a running Avalonia, headless |

Three rules that are not style preferences:

1. **Every feature ships with its tests in the same commit.**
2. **The entire suite is re-run on every change**, and a regression blocks the commit.
3. View-model tests use the **real** `ResourceManagerLocalizationService` rather than a stub, so
   an assertion on a user-facing message also proves the key exists in all three languages.

There are **no UI-automation tests and there will not be**. The window is exercised by hand —
but not by hand-eye: `CLAUDE.md` §6 has a recipe for driving the running launcher from
PowerShell with UI Automation, which is how the sign-in screen's recovery buttons and the whole
self-update were verified.

```bash
dotnet test GameLauncher.sln
```

---

## 6. The rules that will bite you

These are the short list. [CLAUDE.md](CLAUDE.md) §7 is the long one, and every row in it cost
somebody a debugging cycle.

- **A running launcher locks `GameLauncher.exe`**, so `dotnet test` fails with MSB3027 while you
  are driving the window. Close it first. With self-update work this happens constantly.
- **The `ViewLocator` strips the literal text `ViewModel`**, not the suffix. `FooViewModel` →
  `Foo`, never `FooView`.
- **New strings go into three `.resx` files in one pass**, generated from one table **with
  Python** — Windows PowerShell 5.1 reads a UTF-8 script with no BOM as ANSI and eats the
  accents. Check with `grep` that they survived; `dotnet format` is happy either way.
- **`Set-Content -Encoding utf8` in PowerShell 5.1 writes a BOM**, and `dotnet format` then
  fails the file with `error CHARSET`, which reads like corruption. Use
  `[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))`.
- **A `[RelayCommand]` started with `_ =` is not finished when the test asserts.** `await
  Task.Yield()`. And never `await ExecuteAsync` twice in parallel — it hangs the test host and
  looks like a broken build.
- **NSubstitute's last stub wins**, and a test *factory* is a place that stubs. Arrange shared
  defaults in the test class's constructor.
- **A control character written into a source file is invisible** and tests the wrong thing.
  Build it from its code point: `"name" + (char)7`.
- **Everything is in English** — identifiers, comments, commit messages. Only the translation
  resources are not.

---

## 7. Where the documentation is, and what each part is for

| | For | When to read it |
|---|---|---|
| This file | A new contributor | Now |
| [DISTRIBUTING.md](DISTRIBUTING.md) | Somebody shipping their own launcher | When you want to run one, not change one |
| `Documentation/*.md` | One module each | Before touching that module |
| [CLAUDE.md](CLAUDE.md) | The full decision log | When you want to know *why*, or before proposing a change to something load-bearing |

**Read the `Documentation/` page for the module you are about to work on.** Each one states what
is deliberately *not* implemented, which is the part the code cannot tell you and the part a new
contributor actually needs.

`CLAUDE.md` §3 is a table of every technical decision with its rationale and the alternatives
rejected. It is long on purpose: it is the reason the same mistakes are not made twice. **Never
delete a row.** If a decision is reversed, add one that supersedes it and say why.

---

## 8. Workflow

- Work happens on **`dev`**. Never commit to `main`; the maintainer merges it by hand.
- Atomic commits with conventional prefixes: `feat:`, `fix:`, `refactor:`, `test:`, `docs:`,
  `chore:`, `ci:`.
- **CI runs on `main`**, so nothing on GitHub will catch a red suite on `dev`. The gate is
  local, and it is not optional:

```bash
dotnet test GameLauncher.sln
```

```bash
dotnet format GameLauncher.sln --verify-no-changes
```

A push made without running both is a push made on hope. What they cannot cover is the Linux
leg — the suite runs on Windows here, and three tests skip themselves where there is no Unix
file mode to look at — so a change touching platform-specific code is worth saying out loud as
unverified there.

**A feature that changes a documented surface updates its `Documentation/` page in the same
commit.** That is the only rule keeping those pages from becoming true-on-the-first-day.

---

## 9. If you are looking for something to do

`CLAUDE.md` §10 ends with what is left. As of 2026-08-07 nothing this repository *declares* is
unimplemented, so everything open is a deliberate choice with its reasons recorded rather than a
gap. The ones that would change something for a real user:

- **No data export.** GDPR erasure exists on both sides; the other half of the same regulation
  does not. The place is beside `POST /me/deletion`.
- **`UserSettings.LaunchMinimized` is inert** — the last field in the model nobody reads. An
  inert checkbox is worse than an absent one, which is why the Settings page does not show it.
- **The game detail page needs a server.** The library works offline, including covers; the
  detail page would need the catalog document cached, which is a decision rather than a chore.
- **No virtualisation in Explore.** Cards accumulate in a `WrapPanel`, which is fine for the
  catalogs this targets and is the first thing to change if one grows.
