# CLAUDE.md — Custom Game Launcher / Client

Context file for AI-assisted development sessions. **Read this before writing any code, and
update it at the end of every session** (see [Session protocol](#session-protocol)).

Companion repository: `Custom-Game-Launcher-Backend` (C++ REST API). Cross-cutting
contracts — API shapes, manifest format, error envelope — must stay in sync with it.

---

## 1. What this project is

An open-source, cross-platform desktop launcher in the style of the Epic Games Store, aimed
at indie/hobbyist developers who need to distribute in-development builds and demos to
friends and small tester groups without zip files on Discord or manual Drive links.

This repository is the **client**: an Avalonia application for **Windows, Linux and macOS**.
It handles authentication, library and store browsing, resumable delta downloads, install
management, game launching, and updating itself.

Anyone can fork it and rebrand it by editing a single `launcher.config.json`.

---

## 2. Architecture

### Projects

```
src/GameLauncher.Core            Domain models, service interfaces, config contracts,
                                 i18n contracts. Pure .NET — no Avalonia, no HTTP, no
                                 SQLite. This is where the testable logic lives.

src/GameLauncher.Infrastructure  Implementations: API client, download engine, SQLite
                                 install store, token store, file system, platform paths.
                                 References Core only.

src/GameLauncher.App             Avalonia UI: App.axaml, Views/, ViewModels/, Assets/,
                                 Localization/. Composition root — the only project that
                                 knows about both Core and Infrastructure.

src/GameLauncher.Updater         Small standalone executable that replaces launcher files
                                 while the launcher is closed, then relaunches it.

tests/GameLauncher.Core.Tests
tests/GameLauncher.Infrastructure.Tests
tests/GameLauncher.App.Tests            View models, exercised as plain objects.
```

Dependency direction is strictly `App → {Core, Infrastructure} → Core`. **Core never
references anything outward.** If a class needs `HttpClient`, Avalonia or the file system, it
belongs in Infrastructure behind an interface declared in Core.

### MVVM

- Views are `.axaml` with a code-behind that contains **no logic** beyond `InitializeComponent`.
- ViewModels derive from `ObservableObject` and use `CommunityToolkit.Mvvm` source
  generators — `[ObservableProperty]`, `[RelayCommand]`. No hand-written `INotifyPropertyChanged`.
- ViewModels take their dependencies through the **constructor**; nothing is resolved from a
  static locator. `App.axaml.cs` builds an `IServiceProvider`
  (`Microsoft.Extensions.DependencyInjection`) and a `ViewLocator` maps ViewModel → View.
- ViewModels never touch `HttpClient`, the file system, or SQLite directly — always a Core
  interface. That is what makes them unit-testable without a UI.

---

## 3. Technical decisions

| # | Decision | Rationale | Alternatives rejected |
|---|---|---|---|
| D1 | **Avalonia 11 + CommunityToolkit.Mvvm** | One XAML codebase across Windows/Linux/macOS with a mature MVVM toolkit and source-generated boilerplate. | MAUI (weak Linux story); Electron (runtime weight) |
| D2 | **Central package management** | `Directory.Packages.props` pins every version once, so projects cannot drift apart. | Per-project `PackageReference` versions |
| D3 | **`.resx` + `{loc:Tr Key}` markup extension** | Localized strings resolve through `ILocalizationService`; the markup extension re-evaluates on language change, so switching language needs no restart. Adding a language = adding one `.resx`. | `x:Static` on resx (no runtime switch); hardcoded strings (untranslatable) |
| D4 | **SQLite (+ Dapper) for local state** | Installed games, cached manifests and download progress need transactional, queryable, crash-safe storage. Plain JSON corrupts on a mid-write crash. | JSON files; LiteDB (extra dependency, less portable) |
| D5 | **Two-file configuration** | `launcher.config.json` ships read-only with the app (branding, theme, API endpoint — the fork-and-rebrand surface); user settings live in a separate writable file under the platform app-data directory. Never mixed. | One writable config (an update would clobber user settings) |
| D6 | **Serilog rolling file sink** | Structured local logs with automatic retention; global handlers on `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` write a crash report for opt-in upload. | `Console.WriteLine`; no logging |
| D7 | **Separate updater executable** | A process cannot overwrite its own binaries while running on Windows. The updater is launched, the launcher exits, files are swapped, the launcher restarts. | In-process self-update (impossible on Windows) |
| D8 | **Server is the only authority** | Client-side permission checks exist purely for UX. Every rule is enforced again server-side; the client never assumes its own check is sufficient. | Trusting client-side checks |
| D9 | **Nullable enabled, warnings as errors** | Null-reference bugs are caught at compile time across the whole solution. `AnalysisLevel=latest-recommended` is on, so CA analyzer findings also fail the build. | Nullable disabled |
| D10 | **Avalonia 12.1.1, targeting `net9.0`** | Latest stable major at project start; 11.x will age out during this project's life. Avalonia 12 ships `net8.0` and `net10.0` asset groups, and `net9.0` resolves the `net8.0` one, so the installed .NET 9 SDK is enough. | Avalonia 11.3.x (would need a major upgrade within a year) |
| D11 | **xUnit v3 built-in `Assert`, no fluent-assertion library** | FluentAssertions 8 moved to a licence that is not free for all uses, which is a poor fit for an MIT project. Built-in assertions cost one dependency less and no licence review. | FluentAssertions (licence); Shouldly / AwesomeAssertions (extra dependency for marginal gain) |
| D12 | **LF line endings, enforced by `.gitattributes`** | The solution is built on three operating systems and `dotnet format` checks line endings; mixed endings would fail CI on some legs and not others. | Platform-default endings |
| D13 | **SDK pinned in `global.json`, CI installs it via `global-json-file`** | `dotnet-version: '9.0.x'` let the runner pick whatever SDK it had. A newer SDK ships new CA analyzer rules, and with `TreatWarningsAsErrors` (D9) that turns any SDK release into a red build for a violation that does not exist locally and cannot be reproduced before pushing — which is exactly how CA1873 broke all seven build jobs. `rollForward: latestPatch` still takes patches; moving up a feature band is now a deliberate commit. | `9.0.x` (unreproducible CI); relaxing `TreatWarningsAsErrors` (loses the guarantee D9 exists for) |
| D14 | **Two HTTP clients: one that attaches the bearer token, one that never does** | Refreshing a session has to work *precisely* when the access token has expired. A single client whose handler obtains a token before every request would call `POST /auth/refresh` through that handler, which would try to obtain a token, which is the same call. Splitting the registration makes the cycle impossible to write rather than something a reviewer has to notice. | One client whose handler skips `/auth/*` (the same rule then lives in a path comparison and in the DI graph, and only one of them is checked); refreshing by hand at each call site (every new endpoint is a chance to forget) |
| D15 | **`BearerTokenHandler` does not retry a 401** | The token is fetched at send time and `GetAccessTokenAsync` rotates it a minute before expiry, so a 401 that still arrives means the session was revoked server-side — most likely its whole family, because somebody replayed a refresh token. Replaying the request with the same credentials would only be told no twice. | Retry-once-after-refresh (hides a revoked session as a slow request, and doubles every genuinely rejected call) |
| D16 | **The session is stored in clear, in a per-user directory, mode 0600 on Unix** | DPAPI is Windows-only and a keyring means a libsecret dependency that is absent on a headless or minimal Linux install; either choice leaves a fork to solve the other two platforms itself. The file gets the strongest protection available on all three instead, and the exposure is bounded by design: signing out revokes the token, and replaying one the real client has already rotated revokes the family. | DPAPI (Windows-only); libsecret/Keychain (a platform-specific dependency each, for a credential that is already revocable) |
| D17 | **Navigation runs one way: the shell knows its children, the children raise events** | A child holding a navigator that holds the child cannot be constructed in a test without building the whole graph, which is exactly what makes view-model tests get skipped. Events keep the graph acyclic and let a page be exercised on its own. | An `INavigator` injected into each child (cycle); the shell resolving pages from `IServiceProvider` (a locator by another name — see §2) |
| D18 | **One `IApiErrorPresenter` maps every failure to a localized sentence** | The mapping from a failure to what the user is told is a product decision, and having it in one place is what stops each view model inventing its own wording. It takes an override for the single case where one code means two things: on the sign-in form a 401 means the password was wrong, not that a session aged out. | Per-view-model message building (untranslatable in practice, and inconsistent); mapping on HTTP status (the status-to-meaning mapping is the server's to define) |

---

## 4. Download and install model

The server stores build files in content-addressed storage: each file is a blob keyed by its
SHA-256, and a build's manifest lists `(relative path → blob hash, size, executable bit)`.
The client mirrors that model:

1. **Plan** — fetch the manifest of the target version; diff it against the installed
   manifest. The result is a set of blobs to fetch and paths to delete. For a fresh install
   the "installed manifest" is empty. The server may advise a full download when the delta
   exceeds a size ratio threshold.
2. **Space check** — compare the required bytes against free space on the install volume
   *before* touching anything.
3. **Fetch** — download blobs into a staging directory with HTTP `Range`, so an interrupted
   download resumes where it stopped. Each blob is written to `.part` and hash-verified before
   being accepted; a mismatch discards it and retries.
4. **Apply** — only once every blob is present and verified, move files into place and delete
   removed paths. **An interrupted download must never leave a broken installation.**
5. **Verify** — re-hash the installed tree against the manifest and record the result.

Uninstalling deletes the install directory and its rows in the local database, and reports
freed space.

---

## 5. Code conventions

Everything — identifiers, comments, docs, commit messages — is in **English**.

- **C# 13 / .NET 9**, `Nullable` enabled, `TreatWarningsAsErrors` on. Formatting is
  `.editorconfig`; CI runs `dotnet format --verify-no-changes`.
- **Naming:** `PascalCase` types/methods/properties, `camelCase` locals, `_camelCase` private
  fields, `I`-prefixed interfaces. File name matches the type.
- **Async everywhere** for I/O: `Task`-returning, `Async` suffix, `CancellationToken` as the
  last parameter — always accepted and always honoured. No `async void` outside event
  handlers, no `.Result` / `.Wait()`.
- **No hardcoded UI strings.** Every user-visible string goes through the localization
  resources; a test scans `.axaml` files and fails on literal `Text=`/`Content=` values.
- **No magic paths.** Platform directories come from `IPathProvider`, never from a literal
  or a bare `Environment.SpecialFolder` call at the use site.
- **Errors:** the API client translates the server's RFC 7807 envelope into a typed
  `ApiException`; a central handler turns it into a localized user-facing message.
  ViewModels do not build error strings themselves.
- **Comments** are sparing and explain *why*, never *what*.

---

## 6. Commands

```bash
dotnet restore GameLauncher.sln
dotnet build GameLauncher.sln -c Debug
dotnet test GameLauncher.sln
dotnet run --project src/GameLauncher.App
```

```bash
dotnet format GameLauncher.sln --verify-no-changes
```

Self-contained publish (per RID: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`):

```bash
dotnet publish src/GameLauncher.App -c Release -r win-x64 --self-contained
```

---

## 7. Environment gotchas (verified on the maintainer's machine)

| Fact | Consequence |
|---|---|
| .NET SDK 9.0.310 is installed | Local build and test work out of the box |
| No macOS machine available | The macOS target is verified **only** in CI; never claim it is tested locally |
| Backend needs Docker Desktop, whose daemon is often stopped | Start it before running the client against a local API |
| `gh` 2.97 is installed at `C:\Program Files\GitHub CLI` and authenticated as `Ruy41321` | Read CI failures with `gh run view <id> --log-failed` instead of guessing. The installer does not add it to an already-open shell's `PATH`; prepend the directory if `gh` is not found |
| **The local SDK carries fewer analyzers than the newest 9.0.x** | A clean local build proves nothing about CI unless the SDK matches. `global.json` pins it (D13); do not "modernise" the workflow back to `dotnet-version: '9.0.x'` |
| **`Avalonia.Diagnostics` has no 12.x release** | Do not add it back; the dev tools moved into the core package in Avalonia 12 |
| **`Serilog.Sinks.File` 8.0.0 is prerelease only** — 7.0.0 is the newest stable | Verify a version really exists before pinning it; the flat-container feed lists prereleases too |
| **`.editorconfig` naming rules are first-match-wins** | The `const` and `static readonly` PascalCase rules must stay *above* the private-field `_camelCase` rule, or every constant is reported as a violation |
| `dotnet format` checks line endings | `.gitattributes` normalises everything to LF; a file written with CRLF fails the format gate |
| **`Set-Content -Encoding utf8` in Windows PowerShell 5.1 writes a BOM** | `dotnet format` then fails the file with `error CHARSET`, which reads like a corrupt file rather than a byte order mark. Rewrite a source file with `[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))`, which also leaves the line endings alone |
| **The `ViewLocator` strips the literal text `ViewModel`, not the suffix** | `ViewModels.LoginViewModel` resolves to `Views.Login`. The view class is therefore `Login`, **not** `LoginView`; naming it `LoginView` compiles and silently renders "View not found" at runtime |
| **A C# record compares a collection member by reference** | `AuthSession`, `GameDetail` and `PagedResult<T>` all carry lists, so `==` on two of them is not a content comparison. Assert field by field instead; a round-trip test that used record equality passed for the wrong reason until it did not |
| **`Assert.SkipWhen` does not satisfy the platform analyzer** | CA1416 only understands `OperatingSystem.IsWindows()`. A Unix-only assertion needs the skip *and* a real `if`, or `TreatWarningsAsErrors` turns it into a failed build on every platform |
| **`SHA256.HashData` does not exist in Windows PowerShell 5.1** | It runs on .NET Framework, so the static helpers added in .NET 5 are absent. Use `[System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)` when hand-driving the upload protocol against a local server |

---

## 8. Testing policy (non-negotiable)

1. Every feature ships with its tests in the **same commit**.
2. **The entire existing suite is re-run on every change.** A feature is not done until the
   full suite is green; a regression blocks the commit.
3. `GameLauncher.Core.Tests` covers domain and service logic with no I/O — dependencies are
   substituted with NSubstitute.
4. `GameLauncher.Infrastructure.Tests` covers the API client (against a stub
   `HttpMessageHandler`), the download planner, the SQLite store, and config loading.
5. ViewModels are tested as plain objects in `GameLauncher.App.Tests`, with the real
   `ResourceManagerLocalizationService` rather than a stub — an assertion on a user-facing
   message then also proves the resource key exists in every language. There are no
   UI-automation tests.
6. Stack: **xUnit v3 + NSubstitute**, using xUnit's built-in `Assert` (see D11). Async tests
   take `TestContext.Current.CancellationToken`.
7. Two convention tests exist and must keep passing: one fails when a language is missing a
   key English has, the other when a `.axaml` file contains a literal user-visible string.

---

## 9. Git workflow

- All work happens on **`dev`**. Never commit to `main`.
- `main` is merged **manually by the repository owner** once work is validated — never
  propose or perform that merge.
- Atomic, well-described commits. Conventional-commit prefixes: `feat:`, `fix:`, `refactor:`,
  `test:`, `docs:`, `chore:`, `ci:`.
- CI runs on every push and pull request targeting `dev`, across
  `windows-latest` / `ubuntu-latest` / `macos-latest`.

### Finishing a milestone

Pushing `dev` at the end of a milestone is **not** something to ask permission for — do it,
then watch the run it triggers. A milestone is not finished until CI is green:

```bash
git push origin dev
# gh lives in "C:\Program Files\GitHub CLI" and is not on an already-open shell's PATH
gh run list --branch dev --limit 3          # the new run appears a few seconds after the push
gh run watch <id>
gh run view <id> --log-failed               # only what failed, not the whole log
```

Three operating systems build in parallel here, so a failure on one of them is still a red
milestone: fix it, push the fix, and check again. Mid-milestone pushes remain the maintainer's
call.

---

## 10. Progress

Legend: ✅ done · 🚧 in progress · ⬜ not started

### Milestone 1 — Repository scaffolding ✅
- ✅ MIT `LICENSE`, `README.md`, `.gitignore`, `.editorconfig`
- ✅ Solution with Core / Infrastructure / App / Updater projects
- ✅ `Directory.Build.props` + `Directory.Packages.props`
- ✅ `launcher.config.json` and its typed loader
- ✅ Avalonia shell window, dark FluentTheme by default, theme from config
- ✅ DI host wiring and `ViewLocator`
- ✅ i18n: `Strings.resx` (en) + `it` + `fr`, `ILocalizationService`, `{loc:Tr}` extension
- ✅ Serilog file logging and global crash handlers
- ✅ xUnit test projects, GitHub Actions CI matrix on `dev`

### Verified on 2026-08-02
- 56/56 tests green (33 Core, 23 Infrastructure)
- `dotnet format --verify-no-changes` clean
- The shell window opens and stays up on Windows
### GitHub Actions, first real run (2026-08-03)
The workflow ran. `Format` passed; all seven build jobs — three platforms plus four publish
RIDs — failed identically on `CA1873` in `LauncherConfigurationProvider`, a rule the runner's
SDK has and 9.0.310 does not. Fixed by guarding the log call and by pinning the SDK (D13).

The next run was green end to end: all 8 jobs, 56 tests on Windows, Linux **and macOS**, and
all four self-contained publishes. The macOS leg is therefore verified for the first time —
§7 still holds that it can only ever be verified here, never locally.

### Milestone 6 — The client talks to the server ✅
- ✅ Core contracts mirroring the server's catalog, auth and RFC 7807 shapes; two converters
  translate the server's empty-string-for-absent dates once instead of at every call site
- ✅ `ApiTransport` + typed clients for `/auth`, the catalog and the library; every failure
  leaves as an `ApiException`, including a refused connection and a client-side timeout
- ✅ `AuthenticationService`: restore on startup, single-flighted rotation, sign-out that
  succeeds offline; `FileTokenStore` persists the session (D16)
- ✅ `BearerTokenHandler` on the authenticated client only (D14, D15)
- ✅ Login and registration, Explore with search/sort/paging, library, game detail with
  patch-note cards and the build this machine could install
- ✅ `IApiErrorPresenter` (D18); 51 new resource keys in English, Italian and French
- ✅ `GameLauncher.App.Tests` for the view models

### Verified on 2026-08-03
- 243/243 tests green (101 Core, 78 Infrastructure, 64 App)
- `dotnet format --verify-no-changes` clean
- The window opens against no server at all and lands on the sign-in screen, with nothing in
  the log but "Launcher starting"
- **End to end against the real stack** (`docker compose up -d --build`, a seeded publisher
  account, one game with a ready Windows build): the client's own DI graph signed in, listed
  Explore sorted by title, searched, opened the game detail with its beta version and release
  notes, picked the Windows/x64 build and correctly found none for macOS/arm64, added the game
  to the library twice without error, removed it, and got `NotFound` — with a request id — for
  a game that does not exist. A draft created by the same publisher never appeared in Explore.
  Signing out left `ICatalogApi` answering `Unauthenticated` without a round trip.

### Next up
- ⬜ **M7** Download engine: `Range` resume, staging + atomic apply, disk-space check,
  uninstall, progress reporting
- ⬜ **M8** Dev dashboard, launch parameters, offline mode, self-update
- ⬜ **M10** `Documentation/` per module, crash-report upload

---

## Session protocol

At the end of every working session, update:

1. **§10 Progress** — move items between ✅/🚧/⬜, add what is genuinely next.
2. **§3 Technical decisions** — append any new decision *with its rationale and the
   alternatives rejected*. Never delete a row; if a decision is reversed, add a new row that
   supersedes it and say why.
3. **§6 Commands** — add any command a future session would otherwise have to rediscover.
4. **§7 Environment gotchas** — record anything that cost time to figure out.

Keep it accurate over optimistic: a wrong progress table is worse than no progress table.

### At the end of a milestone, additionally

5. **Push `dev` and see CI through to green** — see §9. Not something to ask about.
6. **Update `HANDOFF.md`**, which lives one directory above both repositories
   (`C:\Users\Luigi\Developing\Personal\GameLauncher\HANDOFF.md`) and is deliberately outside
   version control, so it never lands in a commit. It is the first thing the next session
   reads, before either `CLAUDE.md`. Bring these up to date:
   - **Stato** — which milestones are done, the current test count, what is pushed;
   - **Prossimo** — the next milestone, in enough detail to start without re-deriving it;
   - **Cosa esiste già lato server** — the API contract the client codes against, so the next
     session inherits it rather than rediscovering it;
   - **Debiti aperti** — anything deferred, added or paid off.

   `HANDOFF.md` is a briefing, not a changelog: it says what is true now and what to do next,
   and everything already captured by `CLAUDE.md` or the `Documentation/` files belongs there
   instead, referenced by name.
