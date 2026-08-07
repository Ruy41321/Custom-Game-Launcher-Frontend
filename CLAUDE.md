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
management, game launching, and publishing — builds, artwork and the devlog — from the
developer dashboard. It also **updates itself**: it checks for a newer signed release, downloads
a verified one, replaces the installation and restarts — rolling back to the old one if the new
one does not start. See §10 and `Documentation/self-update.md`.

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

src/GameLauncher.Updater         Small standalone executable that replaces the installation
                                 while the launcher is closed, relaunches it, and puts the
                                 old one back if it does not start. References Core only,
                                 and is published self-contained into `<install>/updater/`.

tests/GameLauncher.Core.Tests
tests/GameLauncher.Infrastructure.Tests
tests/GameLauncher.App.Tests            View models, exercised as plain objects.
tests/GameLauncher.Updater.Tests        The swap against real directories, with the
                                        launcher substituted.
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
| D3 | **`.resx` + `{loc:Tr Key}` markup extension** | Localized strings resolve through `ILocalizationService`; the markup extension re-evaluates on language change, so switching language needs no restart. Adding a language = adding one `.resx`. **True since 2026-08-07 and not before**: the shape was always right — a binding to an indexer on `LocalizationSource` — but the invalidation was raised as `PropertyChanged("Item[]")`, which is WPF's `Binding.IndexerName` and means nothing to Avalonia, so every label kept the language it first rendered in. It is `"Item"` now, and `TrExtensionTests` drives a real Avalonia binding so the promise is a failing test away from being broken again. | `x:Static` on resx (no runtime switch); hardcoded strings (untranslatable); `PropertyChanged(null)` as the fix (conventionally "everything changed", and Avalonia's indexer node ignores it too — measured, see §7) |
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
| D19 | **The manifest is verified against its published hash before it is parsed, and a mismatch has an error code of its own** | The server serves the exact bytes `manifestSha256` covers, so verification is hashing the response. Rebuilding a canonical form here would put a second definition of a wire contract in a second language, and the two would drift. Refusing *before* parsing is the part that matters: a document that is not the one that was published must never become the one that gets installed. `ApiErrorCode.Integrity` sits beside `Network` as a failure no server ever sends, because a server that knew would not have sent the response. | Re-canonicalising client-side (two definitions of one contract); parsing first and checking later (the wrong build is already described by then); folding it into `Unknown` (the user is told "something went wrong" for the one failure that retrying actually fixes) |
| D20 | **A third `HttpClient`, for the file server, with no bearer token and no timeout** | A signed URL carries its own authorization, so attaching the launcher's token would hand it to whatever host the API named — and the API names that host. Splitting the registration makes that impossible rather than something a reviewer has to notice, exactly as D14 did for `/auth`. The timeout is infinite because `HttpClient.Timeout` covers the response body as well as the headers and a build is arbitrarily large; a stalled transfer is bounded by the caller's cancellation, not by a clock that started at the first byte. | Reusing the authenticated client (leaks the token off-origin); a 30-minute timeout (an arbitrary cap on how big a game may be) |
| D21 | **One install row per game, keyed by the game id, in SQLite with WAL** | A game occupies one directory on this machine, so two rows would be two answers to where it lives. The file is a database and not JSON because the row being written during an update is precisely the one that says the directory is half of one build and half of another — the moment a rewrite-in-place corrupts a JSON file is the moment that fact matters most. The schema is versioned in `PRAGMA user_version` and migrated by appending to an array, and enums and instants are stored as text so the file can be read with any SQLite tool at the moment the launcher is the thing that is broken. | JSON (corrupt on a mid-write crash); a row per install directory (invites two installs of one game and answers nothing the launcher asks) |
| D22 | **The row flips to `Installed` last, and staging survives until it does** | An install directory cannot be updated atomically without twice the disk, so the guarantee on offer is different: a game is never *presented* as installed until every file is verified in place. `InstallState.Applying` is what a crash leaves behind, and it is what stops the next run computing a delta against a build that is only half there. Keeping staging until the row changes means that crash is recovered by redoing the apply, not the download. | Deleting staging as each file is applied (a crash during apply costs the whole download again); writing the row first (a directory that claims to be a build it is not) |
| D23 | **No full re-hash after an install; the copy out of staging is hashed in the same pass instead** | Every blob is already verified before it is allowed to take its content address, so the only step a download check cannot cover is the copy into the install directory — and those bytes are being read anyway, so hashing them costs nothing. A full pass over a 50 GB install would only catch a disk that changed its mind between two reads, and paying minutes for that on every install would teach people to skip it. `VerifyAsync` offers it on demand, which is where that check belongs. | Re-hashing the tree after every install (minutes of I/O for a case on-demand verification already covers); trusting the copy (the one unverified step) |
| D24 | **Manifest paths are resolved against the install root and refused if they escape it** | The server validates them on ingestion and the database enforces it again, and this is still worth a string comparison: a client that writes wherever it is told is one compromised server away from writing into the user's startup folder. The client's own check is the one that protects *this* machine. | Trusting the server's validation (the client is the last line, and it is free to be) |
| D25 | **410 from the file server is its own error code, apart from 403** | nginx distinguishes an expired signature from a bad one deliberately, and the client acts on the difference: an expiry is fixed by asking for a fresh plan, and nothing about the account or the build has changed, whereas a bad signature is a bug or a clock. Collapsing them would make the recoverable case look like the unrecoverable one. | One "download refused" code (the client cannot tell whether retrying is worth anything) |
| D26 | **Progress is bytes and a phase; the speed and the estimate come from a sliding window** | A single percentage cannot say that a step is running but transferring nothing, so the phases that move no bytes get an indeterminate bar rather than one that fills up while nothing happens. The rate is measured over the last few seconds because what a person wants to know is how fast it is going *now* — an average since the start takes minutes to notice the line has come back. Both the speed and the estimate are omitted until there is something to base them on: a countdown that says four hours and then twelve seconds is worse than no countdown. | A single percentage (cannot express a phase that transfers nothing); an average since the start (wrong for minutes after any stall) |
| D27 | **Every decision about how to start a game lives in a pure `LaunchPlanner`; the process itself is a thin wrapper** | The refusals — not installed, an unfinished install, a missing executable, an entrypoint that escapes the install directory — are the part worth testing, and none of them needs a process to exist. What is left is `Process.Start`, whose one interesting behaviour is noticing the exit, and that gets a test which really starts the platform's own shell. The game is a **child** so the launcher can tell it is running and stop offering to start it twice, and is deliberately **not** killed when the launcher closes: a player who quits the launcher has not asked to quit the game. | A launcher class that both decides and starts (nothing testable without a real executable); a detached process (cannot tell whether it is running) |
| D28 | **Arguments stay a command line, and the player's go after the build's** | The publisher wrote a string into the manifest and the player writes a string into the options. Re-tokenising either into a list here would be a second argument parser to get wrong, and it would disagree with the one the game itself uses. The player's arguments are appended because nearly every parser lets the last occurrence win, which is what makes an override an override. `LaunchOptions` is a column of its own beside `LaunchArgs`: an update rewrites everything the manifest says, and it must not take a preference the player set with it. | Parsing into `ProcessStartInfo.ArgumentList` (a second parser, and a different one); one field for both (an update silently resets the player's choice) |
| D29 | **An unreachable server keeps the session and the library falls back to disk; a server that answers and refuses does not** | Signing in is no more possible offline than refreshing is, so answering an unreachable server with the sign-in screen locks a player out of games already on their disk for no reason. `RestoreAsync` therefore keeps the stored session untouched and reports success, and the first call that reaches a server is the one that rotates it. The library reads the install store first and unconditionally, so the offline list is the half of the answer that never needed a server. A refusal is different and stays an error: an expired session has to be said out loud, or the player sees a short library and no explanation. | Signing out when the server is unreachable (locks the player out of local games); treating every failure as offline (hides a revoked session behind a banner) |
| D30 | **Publishing is its own API interface, and the client re-checks the manifest path rules before uploading** | Every publishing route needs a permission a player's account does not have, and a separate `IPublishingApi` puts that in the type system rather than in a comment. The path rules are copied from the server's `validateRelativePath` and applied first, because a name the server will refuse is worth catching before gigabytes travel — as is an entrypoint that is not one of the files, which is the same mistake with a more expensive ending. That the copy can drift is stated in the type's own comment. | Adding the routes to `ICatalogApi` (a player's client carrying calls it can never make); trusting the server's validation alone (the refusal arrives after the upload) |
| D31 | **Uploads are one blob at a time, in 4 MiB chunks, at whatever offset the server says** | The offset is assigned server-side by a conditional `UPDATE`, so a client that disagrees is the one that is wrong: a refused chunk is answered by *asking* where the session is, never by guessing, and two corrections in a row is the limit because more means a disagreement a retry will not fix. Sequential because the server bounds open sessions per user and its staging disk is that bound times the largest blob — four at once would be four times the scratch space on a machine chosen for being cheap. The chunk size is under the server's 8 MiB default with headroom; nothing advertises the real limit, so it is a guess and is recorded as a debt. | Parallel uploads (multiplies the server's staging disk); trusting the client's own offset (silently duplicates or skips a range, and the hash only catches it at the end) |
| D32 | **The folder dialog sits behind `IFolderPicker`, and background events marshal through a captured `SynchronizationContext`** | The file dialog is the one step of publishing that cannot be driven from a test, so it is the one thing behind an interface; everything else in the flow is exercised end to end. `ViewModelBase.OnUiThread` posts to the context captured where the view model was built — the UI thread in the running app, and nothing at all in a test, which is what makes a callback run inline there instead of on the thread pool. A binding updated off the UI thread is a crash that only happens on a user's machine. | Calling `StorageProvider` from the view model (untestable); `Dispatcher.UIThread` directly (needs an initialised Avalonia in every test) |
| D33 | **The install directory is a setting that is actually read, and it decides where the *next* game goes** | `UserSettings.InstallDirectory` existed from milestone 1 and nothing consulted it, which is the same as not having it — core feature 7 of the plan was declared and unimplemented. An install that already exists keeps its directory when the setting changes, because moving somebody's game because a preference changed is a different action from choosing where the next one lands, and the page says so rather than leaving it to be discovered. A configured directory that cannot be created falls back to the platform default: refusing to install would punish the user for a preference they can no longer act on, and there is always a place that works. | Moving existing installs (a preference change silently relocating gigabytes); refusing to install (the launcher has a default that always works); leaving the field unread (a setting that does nothing is worse than an absent one) |
| D34 | **Startup records what a crash left behind, and starts nothing** | Nothing is applying while the launcher is closed, so a row still saying `Applying` is the mark of a process that died mid-apply. It becomes `Broken`, which is what the directory is and the state the rest of the launcher already explains and offers to repair. Staging is swept by *age* rather than emptied, because a partly fetched build is what makes resuming cheap and clearing it every start would turn every interrupted download into a full one. Recovery deliberately downloads nothing: fetching gigabytes because an application was opened is not a decision to make on the user's behalf. | Auto-resuming the download (spends someone's bandwidth uninvited); emptying staging at startup (throws away exactly what makes a resume free); leaving `Applying` rows alone (the launcher keeps claiming an install is in progress when none is) |
| D35 | **Artwork is fetched on a fourth `HttpClient`, with no bearer token, and cached on disk by URL** | A media URL is public, unsigned and on whatever host the API named, so attaching the launcher's token would hand a credential to a host the server chose — the reasoning of D20 for the file server, and the registration is split for the same reason: to make it impossible to write rather than something a reviewer has to notice. Unlike the file-server client it keeps the ordinary timeout, because a cover is small and one taking thirty seconds is one the page is better off without. The cache needs no revalidation and no expiry because artwork is **content-addressed server-side**: the same picture is always the same URL and editing one means a different URL, so a cached entry cannot be stale. Only the size cap evicts. Three refusals are deliberate: http and https only, a read capped whatever `Content-Length` claimed, and the format decided by the leading bytes rather than the declared type — the server's own D28 rule, applied again by the side about to hand those bytes to a decoder. | Reusing the authenticated client (leaks the token off-origin); reusing the blob client (an infinite timeout for a thumbnail); an HTTP cache with revalidation (a round trip per cover to learn what the URL already guarantees); no client-side sniffing (trusting a remote host about what it is sending a decoder) |
| D36 | **A picture that will not load is reported as no picture, never as an error** | `IImageLoader` returns null for an empty URL, a refused request, an unreachable host, a response too large or bytes that are not an image; `IImageProvider` remembers the null too, so a cover the server does not have is not re-asked every time a grid scrolls past it. A missing cover is not something the user can act on, and an error banner over a page that installs and plays perfectly well would train people to ignore banners. The card keeps its frame and shows the title's first letter, so a grid does not reflow while covers arrive. | Throwing (a page that fails to open because a thumbnail did); an error message per card (noise for a condition nobody can fix); no placeholder (a grid that jumps as pictures land) |
| D37 | **Decoding a bitmap sits behind `IImageProvider`, in the App layer** | `Bitmap` needs an initialised Avalonia, and a view model that cannot be constructed without one is a view model that stops being tested — the same reasoning as `IFolderPicker` in D32. Core therefore hands out bytes and knows nothing about images; the App layer decodes, and memoises per URL for the life of the process so a cover seen in Explore is the same bitmap the library and the detail page show. | Decoding in the view model (needs Avalonia in every test); returning `object` from Core (an untyped binding, and Core learning about images anyway) |
| D38 | **The devlog is paged from the page itself, and its failures are its own** | It is an unbounded list next to a fixed-size one, so it arrives after the page and grows on request; the page number is derived from how many entries are already shown, which makes a reload and a "show older" the same call and makes fetching a page twice impossible. `DevlogError` is separate from `ErrorMessage` because the devlog is the least important thing on the page: a game that can still be installed and played must not be replaced by an apology about its blog. Bodies are rendered as text — rendering remote Markdown is rendering remote markup, for a feature that does not need it. | One error field (a devlog outage looks like a broken page); fetching the whole devlog with the detail (an unbounded list inside a fixed response); a Markdown renderer (remote markup, and a dependency, for a few paragraphs) |
| D39 | **The client asks the server what it accepts, and falls back rather than failing when it cannot** | The chunk size, the largest blob, the path length and the file count were constants copied from the server's defaults, so a deployment that narrowed one broke publishing with an error that did not name the limit. `GET /api/v1/capabilities` needs no token, so it rides on the tokenless client beside `/auth`: asking for one would mean refreshing a session before the launcher knows it can reach this server at all. The provider caches for fifteen minutes rather than for the process, so a reconfigured server does not need everybody to restart, and it **never throws** — an unreachable server, or one older than the route, yields `ServerCapabilities.Fallback`, because refusing to publish over a document *about* publishing would be worse than the guessing it replaces. A failure is not cached. The announced chunk size is obeyed exactly, capped only by what this client will allocate: a remote number reaching `new byte[]` unchecked is how a misconfigured deployment becomes an out-of-memory failure on a user's laptop. | Caching for the process (a reconfigured server needs every launcher restarted); treating a missing document as fatal (an older server becomes unusable for a limit that has a sane default); trusting the announced size unclamped (remote input sizing an allocation) |
| D40 | **Only the *numbers* of the manifest path rules come from the server; the shape rules stay client-side** | `ManifestPathRules` no longer hard-codes `maxPathLength` or `maxFiles` — those are the server's to state. The structural refusals do stay: no absolute path, no `..`, no backslash, no control character. They are what makes a path safe to resolve inside an install directory, which is this machine's problem and not the server's (D24), so the client would keep enforcing them even against a server that stopped. That splits the old debt in two rather than pretending it is closed: the part that could drift is gone, the part that must not is deliberate. | Fetching the whole rule set as data (a path grammar over the wire, for rules that protect the client from the server); leaving the numbers hard-coded (the drift this closes) |
| D41 | **The client sniffs an image to refuse it early, never to vouch for it, and declares no content type when uploading** | The server decides what an image is from its leading bytes and ignores what an uploader declares, because that answer becomes the `Content-Type` of a **public** URL (D28 of the backend). So the upload sends `application/octet-stream`: naming `image/png` would be a guess dressed as a fact and an invitation for a later reader to trust it. The client still checks the signature, but only so a file that is obviously not PNG/JPEG/WebP is refused before gigabytes — or a cover — travel; a positive answer is never treated as sufficient. **SVG is not recognised on either side**, because it is a document format that can carry script rather than a picture. `ImageFormats` therefore moved from Infrastructure to Core: the artwork loader and the uploader apply the same rule for the same reason, and one rule with two implementations is one rule that will eventually disagree with itself. | Declaring the sniffed type (a claim the server ignores and a reader believes); trusting the client's sniff as sufficient (the server is the authority, and it re-checks anyway); no client-side check (an upload spent to be told no); a second copy of the signature table in the publisher (drift between the side that fetches images and the side that sends them) |
| D42 | **A picker returns bytes, not a path, and image validation reads the server's announced limits with no constant of its own** | `IFilePicker` hands back the file's contents because a view model that received a path would have to read it — I/O in a view model, and a *second* untestable step next to the dialog; here the dialog and the read are one operation and one substitution replaces both. Its read is capped so a hostile or mistaken file cannot be pulled into memory unbounded, and the real refusal happens afterwards in `MediaUploadRules`, which reads `media.maxBytes`, `maxScreenshotsPerGame` and `maxAltTextLength` from `IServerCapabilityProvider` (D39) and hard-codes none of them. The limits are shown on the page **before a file is chosen**, which is the whole point: a publisher learns what the deployment accepts from the page rather than from a refusal after the upload. The gallery cap applies to screenshots only — uploading another cover *is* how a cover is replaced, and counting those would refuse a legitimate replacement. | Returning a path (I/O in a view model, and a second thing to substitute); an uncapped read (a remote-sized allocation, locally); constants copied from the server repository (the debt D39 closed, reopened on a new surface); validating only after the upload (the refusal costs the transfer) |
| D43 | **A deletion is armed as view-model state that says what disappears, not performed on a click and not shown through a dialog service** | Every deletion here is irreversible — the server has no undo route and the collector eventually reclaims the bytes — so the sentence the user reads is part of the safety, not decoration: "this version and its 3 builds" is actionable and "are you sure?" is not. `PendingDeletion` carries that sentence and the call, and nothing is sent until a second command runs. A dialog behind an interface would work equally well in the app and be a **second thing no test can drive**, and D32 spends that budget on the file picker alone; as state, a test arms the deletion, asserts on **exactly what the user is told**, and then confirms or cancels — so the wording is covered, which is the half that actually protects somebody's build. The devlog's prompt names the reversible alternative, because somebody who wants a post to stop being visible almost always wants to withdraw it. | A confirmation dialog behind an `IDialogService` (a second untestable step, and the wording goes uncovered); deleting on the click with an undo toast (there is nothing to undo with); a generic "are you sure?" (says nothing about what is at stake) |
| D44 | **The dashboard's four surfaces are child view models under one page, shown as tabs** | A publisher works on **one game at a time**, so the selected game is the context the builds, the details, the artwork and the devlog all share. Separate pages would mean selecting it three times, or a shared navigation state D17 deliberately does not have — the shell knows its children and the children raise events, and a page that needed to tell another page which game it was looking at would be the cycle D17 exists to prevent. Tabs over child view models are *binding*, not navigation, so nothing about the one-way rule changes. The split also buys three readable test classes instead of one 1,200-line one, which is the practical reason it survived: a view model whose tests are hard to read is a view model whose next feature ships untested. The editor announces a save with an event and the dashboard swaps the row, with the selection reload suppressed while it does — assigning `SelectedGame` otherwise means "the publisher picked another game", and letting a save mean that refetches the detail, reloads three children and wipes a message nobody has read. | One enormous `DeveloperViewModel` (untestable in practice, and the tests are what would go); four navigable pages (three extra selections, or a cycle); a game picked once and cached in a shared service (a locator by another name — see §2) |
| D45 | **The install row keeps `coverUrl`, and an update never overwrites one with nothing** | The artwork cache is keyed by URL and needs no server at all, so the only thing missing from an offline library was somebody who remembered the URL — the row kept the id, the slug and the title, and the covers already on this disk were unreachable for want of a string. The column is added by **appending** a migration, so a database written before it existed opens and its rows take the empty default, which is what an upgraded launcher really runs. An update does rewrite the cover, because a publisher can change one; but only ever *with* a cover, because a response that arrived without one is not a publisher who removed it, and treating it as one would discard the only copy of the URL this machine has precisely when there is no server to ask again. | Refetching the catalog offline (there is no server, which is the whole case); a second cache keyed by game id (two indexes over one set of pictures); overwriting unconditionally (a partial response erases the offline cover) |
| D46 | **The Explore search debounces on `TimeProvider`, and a new search cancels the one in flight** | Seven keystrokes were seven requests, and six of the answers were for text nobody would read — but the waste was the smaller half. Nothing ordered the answers, so a slow reply for "orb" arriving after the reply for "orbital" left the *wrong results on screen*: a correctness bug wearing a performance bug's clothes. A debounce makes that unlikely and cancellation makes it impossible, so both are here rather than either. The delay is a `TimeProvider` timer rather than `Task.Delay` because a debounce a test really waits out is a slow test that eventually fails on a loaded machine instead of on a bug; the App project's `FakeTimeProvider` therefore implements `CreateTimer` by hand rather than the suite taking `Microsoft.Extensions.TimeProvider.Testing`, which is thirty lines of test code against a dependency in a project that keeps few on purpose (D11). It lives in the **view model**, not the view: that is where Enter arrives too, and a debounce in a code-behind is a rule no test can press. `OperationCanceledException` from a superseded search never reaches `ErrorMessage` — cancelling is how the page keeps up with typing, and an error where the results go would make ordinary typing look broken. | `Task.Delay` (a test that sleeps, and is flaky for it); debouncing in the view's code-behind (untestable, and Enter would need a second path); debounce without cancellation (the race is rarer, not gone); showing a cancelled search as an error (every fast typist sees a failure) |
| D47 | **Erasure is composed from outside the session service, because putting it inside would close a cycle the container refuses to build** | The obvious home for "erase this account" is `IAuthenticationService`: an erasure ends a session, and the session is that service's to end. It cannot go there. The account route runs on the *authenticated* client, whose `BearerTokenHandler` depends on `IAuthenticationService`, so a session service that needed the account client back would make the graph unbuildable — the same shape D14 keeps out of `/auth`, arriving from the other side. `IAccountService` therefore holds both and calls them in order: the route first, and the sign-out **only if it succeeded**. That condition is the substance rather than an implementation detail — signing out is a local truth the server is merely told about, and forgetting the session after a refusal would leave somebody signed out of an account that still exists, unable to read the reason they were given. A DI test asserts the graph resolves rather than leaving the reasoning in a comment. | Erasure on `IAuthenticationService` (a cycle: the container refuses to build the graph at all); a `Func<IAccountApi>` or a service locator (hides the cycle instead of removing it, and §2 bans the locator); the view model calling the API and then signing out (the ordering rule lives at every call site, and the next one gets it wrong) |
| D48 | **A destructive prompt says what *survives*, not only what goes — and a game deletion names `draft` as the reversible thing the publisher probably meant** | D43 established that the sentence is the safety and the button is not, and both new prompts are the case where that matters most, because each carries a fact the user cannot deduce. The account prompt says published games **stay online under a deleted name**: the server anonymises rather than deletes, so that the people who installed a game can still update it, and somebody who wanted their games gone has to delete them first — telling them afterwards is telling them too late. There are two wordings, chosen on `game.publish` rather than on a count of games, because asking the server how many somebody published would be a request made for a sentence. The game prompt says other people's libraries do not stop the deletion and their updates stop with it, and then names `draft`, which is what somebody who wants a title merely hidden actually wants. Both wordings are asserted on in tests, so the half that protects a person is covered rather than the half that is easy to check. | "Are you sure?" (says nothing about what is at stake); one wording for players and publishers (either noise about games they never had, or silence about games they did); counting the publisher's games first (a round trip for a sentence); a dialog behind a service (untestable wording — D32 and D43) |
| D49 | **A crash report is redacted where it is written, not where it is sent, and the file on disk *is* the request body** | The two halves are one decision. The file is the body, so redacting at upload time would leave the unredacted copy sitting in the log directory of a machine whose owner asked for the opposite — and would mean the thing somebody could review was not the thing that travelled. And because it is the body, there is one definition of the document rather than a written format plus a parser, which is a pair that drifts the first time a field is added. `CrashReportRedactor` replaces this machine's profile, data and install directories, plus a narrower backstop for a home directory that is not this machine's; only the prefix goes, so the report still says which file it was. It is a reduction of risk and not a guarantee — a message can carry anything — which is exactly why the **server** records no account against a report either: two measures that fail differently rather than one that is trusted. | Redacting at upload (the unredacted copy stays on disk, and the reviewed thing is not the sent thing); a human-readable file plus a parser (two definitions of one document); sending the exception unmodified (a person's name in their own home directory is the likeliest way a crash report carries a person) |
| D50 | **The uploader runs once at startup, deletes what it cannot or must not send, and never throws** | A crash report is written by a process that is dying, so the run that can send it is always the next one: there is no queue and no retry timer, and a report that could not be sent is simply still there next time — the same mechanism that put it there. What it does with a file it will not send is the substance. Consent withdrawn **deletes** them, because somebody who said no should not have a growing pile of unsent reports about them on their own disk; a server that does not accept them deletes them, because carrying them forever for a server that will never take them is carrying them for nothing; a refusal deletes, because it will be refused identically forever; and only unreachable-or-throttled keeps, which is the one case where trying again is worth anything. Five per start, oldest first, because a launcher that crashed thirty times overnight has one bug and burning the server's rate limit on the tail would delay the report that matters. And it swallows everything: a launcher that failed to start because it could not report a previous failure would be the worst possible outcome of this feature. | A retry timer (a queue, for something that arrives at most once per crash); keeping a report the user has withdrawn consent for (a pile about somebody who said no); keeping a refused one (it will be refused forever); sending all of them at once (spends the rate limit on duplicates of one bug); letting it throw (a crash reporter that stops the launcher) |
| D51 | **Explore grows by scrolling, and the two entry points are separate: one replaces the list, the other appends to it** | A scroll handler fires on every scroll event, so a single "load" that decided for itself whether it was replacing or appending would be one flag away from clearing the grid under somebody's cursor. `LoadAsync` and `LoadMoreAsync` say which they are at the call site instead. Three rules hang off that: only one request is ever in flight, because one flick of a wheel would otherwise be several requests for the same page and several copies of it in the list; the page number advances only on an answer that arrived, so a failure or a superseded search leaves the next scroll retrying the page nobody saw rather than stepping over it; and the end of the list is a state read from the server's own count — asking until an empty page comes back means one wasted request every time anybody reaches the bottom. The scroll gesture itself is an attached behaviour holding no policy, because a rule in a code-behind is a rule no test can press (D32, D46), and the scroll offset returns to the top on a *replacement* only, which is what makes an append not jump. | One `LoadAsync` with a boolean (the clear-under-the-cursor bug is one wrong argument away); no in-flight guard (duplicate pages, and duplicate cards); inferring the end from an empty response (a wasted round trip at the bottom of every list, every time); a `ScrollChanged` handler in the code-behind (untestable, and the policy ends up next to the geometry); virtualisation now (a bigger change, and the catalogs this targets fit) |
| D52 | **The registration answer says whether the message went out, and the screen says two different things** | The server does not undo a registration because its relay was down, so "the account exists" and "the link is on its way" stopped being the same fact. The client reads `verificationEmailSent` and tells the person either to check their inbox or to ask for the link again — because watching an inbox nothing was sent to is a wait with no end, and it looks exactly like the launcher having lost the message. The field defaults to **false**, which is how a server too old to send it is read: "ask again" is harmless against a server that did send, and "wait" is not. The `dev*` token fields are gone from `RegistrationResult` and `RequestPasswordResetAsync` returns nothing at all — the link is delivered by mail and the page it lands on is the server's, so there is nothing left for a client to hold. | One sentence for both outcomes (the failure case reads as a broken launcher); defaulting the field to true (an older server silently tells everybody to wait); keeping the dev fields for "development convenience" (the affordance the server just removed, re-implemented here) |
| D53 | **The launcher asks for the two links and never finishes either flow; the affordance appears only where it means something, and a refusal is not an error** | Three dead ends opened the moment the server started sending mail, and all three are on the sign-in screen: no way to ask for a password reset, no way to ask for the verification link again, and — the one nobody had written down — a sign-in refused for an unconfirmed address reading *"Your account is not allowed to do that"*, because the server answers 403 for that and for a disabled account with the same code. The launcher now **triggers** both messages and lets the browser finish on the server's own pages: putting the two password fields here would be a second place for a product rule that only the server enforces, which is D40's reasoning on a surface where the client protects nothing by copying, and it would mean teaching people to paste a token out of their mail client. The resend is offered after **any** registration that needs verifying — sent or not, because a message that was filtered leaves somebody exactly where one that never left does — and after a 403, where the client says the cause a person can act on and offers the button; for a disabled account that button is harmless, since the route answers identically and sends nothing. Every success sentence is **conditional**, so a client cannot become the enumeration oracle the server refuses to be. A 429 is shown on the info line rather than in red, with the wait named only at two minutes or more so that no resx has to decline "1 minutes"; and a **successful** request disarms its own button until the address changes, while a refused one does not, because waiting and pressing again is the answer to a 429. | A screen per flow inside the launcher (a second definition of the password rules, and a token pasted by hand); a custom URI scheme (an OS-level protocol registration on three platforms, for a project with no installer); the resend always visible (an invitation to spend a rate limit nobody needs); a countdown from `Retry-After` (it is per IP, so it locks out somebody who pressed nothing, and a restart clears it — a promise the launcher cannot keep); showing the 429 as an error (the person most likely to see it is the one whose message never arrived); leaving the 403 wording alone (a resend button under a sentence that explains neither cause) |
| D54 | **The release signing key is a constant in the binary and empty by default; the channel is a shipped configuration field read leniently** | The two halves of "what a fork changes" pull in opposite directions and both answers are forced. The **key** cannot live in `launcher.config.json` because *the file the updater overwrites must not be the file that authorizes the update* — that file ships inside the directory a swap replaces, so a key kept there would be replaced by whatever the update brought with it, while a constant lives inside the artifact whose replacement the signature already protects. It is therefore the **one thing a fork changes in code**, and `configuration-and-localization.md` says so rather than keeping its "no code changes" promise by omission. Empty is the default and means the launcher **asks for nothing at all**, which is the honest state for a fork that has not set up signing and the same answer the server reaches from the other end. The **channel** is the opposite: it is configuration, because which stream a launcher follows is the distributor's choice — a player who could move themselves onto a stream nobody published for them could replace their launcher with a build that does not open, and the launcher is the program that has to start in order to fix anything. And an unrecognised channel is read as `stable` instead of failing `Validate()`: `apiBaseUrl` is worth a hard refusal because a launcher pointed at nothing is useless anyway, while a typo in a channel name would destroy a working launcher over a spelling mistake. | The key in `launcher.config.json` (an update can rewrite the thing that authorizes updates); a key fetched from the server (the server would be vouching for itself); no key at all being an error (a fork that does not sign releases could not run); the channel as a user setting (a one-way trip onto a stream nobody meant them to have); a channel typo failing validation (a launcher that will not open because of a spelling mistake) |
| D55 | **The check verifies the arrived bytes before parsing, refuses a document that is not for this launcher, and is silent about every failure** | The order is the rule: `ReleaseSignature.Verify` runs on the UTF-8 bytes of the document string exactly as the route served them, and `ReleaseDocument.TryParse` only ever sees bytes that already verified — D19's manifest rule applied to the thing that replaces the launcher itself. Nothing rebuilds a canonical form to compare against, because that would be a second definition of a wire contract in a second language; the server does that check once, at publish time. Two refusals go beyond the five rules the backend states. The document must **say** it is for this channel, platform and architecture, which is where signing the document rather than the artifact pays off — a server holding genuine signed releases cannot hand a Windows launcher the Linux one — and the artifact URL must be `http` or `https`, the refusal `CachingImageLoader` already applies to a host the server named (D35). Everything else is `Undetermined` and a log line: an unreachable server, a 404, a body that is not JSON, a signature that does not verify. A **404 is read as up to date**, because no key on that server, nothing published, and nothing for this platform are one situation from here — which is why the server answers 404 to all three. `ECDsa` does the whole thing with no new package, and the algorithm is pinned rather than read from the key, so a deployment given an RSA key is a launcher that checks nothing instead of one verifying with an algorithm nobody chose. | Parsing before verifying (the wrong document is already described by then); re-canonicalising client-side (two definitions of one contract, which drift); trusting the query parameters instead of the signed document (a signed release served as one it is not); showing a failed check to the user (an error nobody can act on, on a launcher that works); treating a 404 as a failure (one red line per start on every deployment that publishes no releases); a managed Ed25519 or a native binding (a crypto dependency across four RIDs, in a repository that refused one over thirty lines of test code) |
| D56 | **An update is announced and downloaded, never installed on its own — and the banner's sentences are rebuilt when the language changes** | A swap requires this process to exit, so a silent update is an application closing under the hands of somebody using it: one line, the release notes, a button, and a way to put it away until the next start. **The second half of this row is superseded by D57 as of 2026-08-07**: while the swap did not exist, what a successful download said was *where the verified archive is*, because promising a restart the launcher could not perform would have been the one kind of lie this feature cannot afford. The button now says "Update and restart" and means it. What survives unchanged is the first half, which is the decision: it is a button and never a timer. The download is in this piece rather than the next one because it is what makes the content-address refusal real: a rule nothing exercises is a rule nobody has checked. The second half was found by driving the real window: the banner's strings are built in code, like `WelcomeMessage`, so they do not re-evaluate the way a `{loc:Tr}` binding does — the headline stayed French under a line that had become Italian. `RefreshLocalizedText` now rebuilds both, with the status kept as a `Func<string>` so an outcome sentence and an error sentence re-render the same way. | Installing silently (an application that closes under somebody's hands); a button that says "Update and restart" before the updater moves files (a promise the launcher cannot keep); leaving the download to the swap commit (the hash refusal would ship untested against a real server); building the sentences once (a banner stuck in the language it first appeared in) |
| D57 | **The swap's decision is a pure function of (exit code, elapsed time), and the old installation is renamed rather than deleted** | The hardest thing in this repository to test is a process replacing the files of the process that started it, and every design that would have made it *observable* — a marker file, an IPC channel, a watchdog outliving its purpose — needs two processes to agree on a protocol while one of them is the thing under suspicion. Reduced to two numbers it is `RelaunchWatch.Judge`, and a test substitutes the one interface that produces them. The rename is the other half: a sibling directory is on the same filesystem, so putting a self-contained build aside is one atomic operation needing no second copy — the reasoning the download's staging tree already follows — and it is a rename rather than a delete because *a rollback with nothing to roll back to is not a rollback*. A `previous` left by an attempt that never resolved is discarded, and that is safe by proof rather than assumption: the updater only runs because a launcher asked it to, so what is installed right now works, and keeping the older copy would make the *next* rollback restore a version two updates behind. **The hole is declared**: a launcher that starts, survives thirty seconds and then crashes is not rolled back, because from here that is indistinguishable from somebody opening the new version and closing it. Nothing remembers a failed release either — the same one is offered again next start. | A marker file or IPC (a protocol between a process and the one it suspects, and untestable); deleting the old installation (nothing to roll back to); keeping a stale `previous` (the next rollback goes two versions back); watching until the launcher exits however long that takes (a helper that lives as long as the application it started); remembering failed releases (state the update process writes about itself, and then a rule for when it stops applying) |
| D58 | **The launcher unpacks the archive and copies the updater out of the installation; the updater is published self-contained inside it** | Three forced answers to one question — what has to happen between a verified zip and a running helper. **The launcher unpacks**, because a zip can carry names that escape the directory it is opened into and the hash proves nothing about names: an archive *correctly signed and hostile in its entry names* is a real and separate case, and the rules that refuse it (`ManifestPathRules`, `PathSafety`, behind `UpdateArchiveRules`) already live in Core for D24's reason and already cover every file of every build. A second copy of a security rule in the updater is a rule that eventually disagrees with itself — and the updater is the one program with nothing behind it to fix its bugs, so it stays small. **The copy out is not a convenience**: on Windows a running executable can be neither renamed nor deleted, so a helper left inside the directory it is about to rename makes that rename fail for a reason nothing reports. It goes to `<user data>/updates/<version>/updater/` rather than the system temp directory, because that directory is known writable and the existing one-version-at-a-time sweep is what eventually removes it — the helper cannot delete its own running image. **Self-contained** because a machine running a self-contained launcher may have no .NET at all; trimmed and invariant-globalized, it costs about 19 MB inside every installation. | Unpacking in the updater (a second implementation of a path rule, in the program that cannot afford bugs); running the updater from the installation (the rename fails, invisibly); the system temp directory (a path outside `IPathProvider`, and nothing sweeps it); a framework-dependent updater (missing exactly when the launcher is self-contained, which is always); the updater deleting its own copy (impossible while it is running) |
---

## 4. Download and install model

The server stores build files in content-addressed storage: each file is a blob keyed by its
SHA-256, and a build's manifest lists `(relative path → blob hash, size, executable bit)`.
The client mirrors that model:

1. **Plan** — `POST /builds/{id}/download` names the build currently installed, and the server
   computes the difference between the two manifests. The client never diffs anything: the
   plan says what to fetch, what is already correct, what to delete and what it will cost,
   and the server falls back to a full download when a delta stops being worth it. Only a
   *finished* install is ever named as the source (D22).
2. **Space check** — before anything is written, which is the only moment it is worth
   anything. Staging and the install directory are checked separately and summed only when
   they share a volume.
3. **Fetch** — blobs, not files: two paths with identical content are one transfer. Four at a
   time, into a content-addressed staging tree, with HTTP `Range` so an interruption resumes
   where it stopped. Each blob is written to `.part` and hash-verified, and only then renamed
   to its content address — **nothing takes that name until its bytes hash to it**.
4. **Apply** — files are copied out of staging, hashed in the same pass (D23), then the plan's
   `remove` paths go and the directories they emptied with them. `copyFrom` entries come off
   the local disk instead, and the server only ever offers a path the update keeps unchanged,
   so the order the plan is applied in cannot matter.
5. **Record** — the row flips to `Installed` last, and staging is swept only after it does
   (D22). **An interrupted update leaves an installation the launcher knows is unfinished,
   never one it will run.**

Verification is a separate, on-demand step: `POST /builds/{id}/verify` compares what is on
disk against the manifest, and an install the server calls broken is recorded as such — which
is what turns the next install into a repair rather than a delta from a build that is not
really there. Files the manifest never mentioned are reported but do not make an install
broken; an install directory legitimately accumulates saves and logs.

Uninstalling deletes the install directory and its row, and reports freed space.

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

`./scripts/dev.ps1` runs the launcher against a local server and adds the two things the bare
`dotnet run` leaves to be discovered the slow way: it says whether the API is actually
answering *before* the window opens — the client works offline by design (D29), so a stopped
backend produces a sign-in screen that refuses every password rather than an error — and
`-Reset` clears the per-user state, which is the only way back to a first-run launcher.

```powershell
./scripts/dev.ps1           # check the server, then start
./scripts/dev.ps1 -Reset    # start as if the launcher had never run on this machine
```

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

### Pressing a real button, from PowerShell

There are no UI-automation tests (§8) and there will not be, so the window is exercised by hand.
It does not have to be exercised by *hand-eye*: UI Automation drives the running launcher from a
script, which is how the sign-in screen's two recovery affordances were verified.

```powershell
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$p = Get-Process -Name GameLauncher
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

# Every text box, in visual order
$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
  [System.Windows.Automation.ControlType]::Edit)
$edits = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
$edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("a@b.c")

# A button, by the text of its Content (see the gotcha about Panel-content buttons)
$c = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::NameProperty, "Password dimenticata?")
$b = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
$b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
```

Setting a `TextBox` through `ValuePattern` updates the binding, so `CanExecute` re-evaluates
exactly as it does under a real keyboard. To look at the result, capture the window with
`PrintWindow($hwnd, $hdc, 2)` rather than `CopyFromScreen` — see §7.

### Exercising the whole client against a real server, without the UI

`AddLauncherInfrastructure()` builds the entire graph on its own, so a console project that
references `GameLauncher.Core` and `GameLauncher.Infrastructure` and resolves
`IAuthenticationService`, `ICatalogApi` and `IInstallationService` drives every layer against
the running stack. This is how M6 and M7 were verified, and it reaches the one thing no test
can: nginx serving a signed URL.

Two things make it safe and repeatable:

- Register an `IPathProvider` **after** `AddLauncherInfrastructure()` — last registration wins —
  pointing `userDataDirectory` at a temporary directory, or the run writes into the
  maintainer's real `%LOCALAPPDATA%\CustomGameLauncher`.
- Copy `launcher.config.json` next to the executable; the configuration provider reads it from
  `IPathProvider.ApplicationDirectory`.

Seeding the server needs a publisher account, a public game and a `ready` build — the sequence
is in `HANDOFF.md`, and the backend's `CLAUDE.md` §7 has the devlist grant.

### Exercising the update check against a real release

The same harness reaches this with no account at all, since the route takes no token. What it
needs is a key pair and one published release; `openssl` is on this machine through Git Bash
(§7), so none of it has to happen inside a container:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out release-signing.key
openssl ec -in release-signing.key -pubout -outform DER | openssl base64 -A
#   -> LAUNCHER_RELEASE_PUBLIC_KEY in the backend's .env, then restart the api
#   -> and the same string in LauncherReleaseKey.PublicKeyBase64 to exercise the shipped path

# The document must have NO trailing newline: use printf, never echo.
printf '%s' '{"schema":1,"channel":"stable","version":"0.4.0","platform":"windows","arch":"x64","sha256":"<64 hex>","size":<bytes>,"releasedAt":"2026-08-07T14:00:00Z","notes":"..."}' > release.json
openssl dgst -sha256 -sign release-signing.key release.json | openssl base64 -A > release.json.sig
```

Then `docker compose cp` the three files and `--publish-release` them (backend `CLAUDE.md` §7).
To see the artifact refusal, change the stored bytes **after** publishing, which leaves the
document and its signature intact:

```bash
docker compose exec api sh -c "printf 'Z' | dd of=/data/launcher/ab/cd/<sha>.zip bs=1 seek=100 conv=notrunc"
```

A `UpdateChecker` can be constructed directly with an `UpdateSettings` of the harness's choosing,
which is how one run covers a launcher on 0.1.0, one already on 0.4.0, one on 0.9.0, one holding
a different key and one holding none.

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
| **Windows PowerShell 5.1 mangles double quotes when it passes an argument to a native executable** | A commit message containing `"like this"` is split into several arguments, and `git commit` reports them as pathspecs that do not match — which reads like a staging problem rather than a quoting one. Write the message to a file and use `git commit -F <file>`; a single-quoted here-string is **not** enough |
| **`ActivatorUtilities` refuses to choose between two public constructors** | A typed `HttpClient` registration fails at resolution with "multiple constructors accepting all given argument types", not at startup — so a DI test catches it and the app finds out when the page opens. `BlobFetcher` keeps a second constructor for its retry policy and marks the real one `[ActivatorUtilitiesConstructor]` |
| **`Microsoft.Data.Sqlite` 10.x needs .NET 10** | The 9.0.x line is the one that matches the pinned SDK, and 9.0.18 lines up with the `Microsoft.Extensions.*` versions already in `Directory.Packages.props` |
| **`Microsoft.Data.Sqlite` pools connections, so the file stays open after the store is disposed** | A temporary directory holding a test database can refuse to delete. Harmless for the suite, which ignores the failure, but do not write a test that asserts the file is gone without `SqliteConnection.ClearAllPools()` |
| **`Order()` on strings is culture-aware** | `"data/pak"` sorts *before* `"Game.exe"` under a culture comparison and after it under an ordinal one. An assertion on a sorted list of paths must say `Order(StringComparer.Ordinal)`, or it passes or fails depending on the machine's locale |
| **NSubstitute's last stub wins** | A test that arranges a return *before* calling a factory which stubs the same call gets the factory's answer, and the failure looks like the production code ignoring its input. Arrange after the object is built, or move the arrangement into the factory |
| **A clock the test advances by hand cannot measure something that ends on another thread** | `ProcessGameLauncher` reads the clock at launch and again when the runtime reports the exit. A test that advanced a fake clock between those two readings passed locally and failed on a faster CI runner, where the process had already exited. The fake clock therefore has a `Step` that moves it on every *reading*, which makes the measured duration independent of who wins the race |
| **A control character pasted into a source file is invisible** | A tool that writes a bell or a newline escape as the character itself produces a file that compiles, tests the wrong thing, and shows nothing in the diff. Build such a string from its code point instead — `"name" + (char)7` — so the intent is on the page |
| **NSubstitute's last stub wins — and a test *factory* is a place that stubs** | Recorded again because it cost a second cycle in a different shape: `CreateViewModel()` arranged an empty devlog, so a test that arranged a devlog *before* building the model got the factory's empty one and the failure read as the view model ignoring the server. Arrange a shared default in the test class's **constructor**, which runs before the test body, and leave the factory to building objects |
| **A `Task<T>`-returning member of an unconfigured substitute yields `default(T)`, not an empty document** | `GetPatchNotesAsync` returned a null `PagedResult`, so every existing detail test crashed with a `NullReferenceException` inside the view model the moment the page started loading a devlog. Any new call a shared view model makes on every load has to be arranged in every test class that builds it |
| **`Bitmap` cannot be constructed without an initialised Avalonia** | Which is why decoding sits behind `IImageProvider` (D37). A test can substitute the interface and assert *which URL was asked for*; it cannot assert on a decoded picture, and trying to construct one is how a view-model test starts needing a UI toolkit |
| **`ReadOnlySpan<byte>.SequenceEqual` needs the other side to be a span, not a collection literal** | `bytes[..8].SequenceEqual([0x89, …])` binds to the `int` overload and fails to compile with a message about `Span<int>`. Put the signature in a `static ReadOnlySpan<byte> X => [...]` property and compare against that |
| **NSubstitute's `Arg.Is<T>` lambda parameter is nullable, and `TreatWarningsAsErrors` is on** | `Arg.Is<GameChanges>(changes => changes.Summary == "…")` fails the build with CS8602, not with a test failure — so a whole test file stops compiling over an assertion that is correct. Write `changes!.Summary` on the **first** dereference in the lambda, as the existing `Arg.Is<GameQuery>(query => query!.Search …)` does. A blanket search-and-replace misses the multi-line form, where the parameter and the first use are on different lines |
| **A `[RelayCommand]` on a `partial void On…Changed` path runs without a `SynchronizationContext` in a test** | `OnSelectedGameChanged` starts its load with a bare `_ =`, so in a test the continuation only runs when the thread is yielded. A test that sets `SelectedGame` and asserts immediately sees the state from *before* the load. `await Task.Yield()` after the assignment is what makes it deterministic — this is the same class of problem as the `Progress<T>` row below, from the other direction |
| **A resx written from PowerShell needs LF and no BOM, and the three files must be edited in one pass** | Two convention tests enforce that English, Italian and French carry the same keys, and `dotnet format` fails a file with a BOM (`error CHARSET`) or CRLF. Adding ~50 keys by hand across three files is how one language ends up short; generate all three from one table with `[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))` after replacing `` `r`n `` with `` `n `` |
| **A `TimeProvider` fake that only overrides `GetUtcNow()` cannot drive anything that uses a timer** | The base class's `CreateTimer` returns a **real** timer wired to the system clock, so `Advance` moves the fake's clock and the code under test carries on waiting for wall time. Nothing fails; the test simply hangs or passes by accident. `FakeTimeProvider` in `GameLauncher.App.Tests` now overrides `CreateTimer` with a queue `Advance` fires — the other two projects' fakes still do not, and adding a timer to something they cover means porting it |
| **Cancel a `CancellationTokenSource` before disposing it, never the other way round** | A superseded request holds the *token*, not the source. Cancelling first leaves the token permanently cancelled, so everything it touches afterwards — including a fresh `Register` — behaves correctly on a disposed source. Disposing first makes the in-flight call see a live token that will never be cancelled, which is the race the cancellation existed to remove. In `ExploreViewModel` each call owns exactly one source and disposes it in its own `finally`; the newer call only cancels |
| **A typed `HttpClient` whose handler needs a service makes that service unable to depend on the client** | `BearerTokenHandler` takes `IAuthenticationService`, so anything on the authenticated client is downstream of it. Putting the erasure call on `AuthenticationService` compiled and then failed at *resolution* with a circular-dependency message naming three types — which reads like a DI bug rather than a design one. Compose from a third service instead (D47), and add the new interfaces to the DI test in `ServiceCollectionExtensionsTests`, which is what turns this into a failing test rather than a blank window |
| **A `.resx` written by hand from PowerShell loses its accents** | Windows PowerShell 5.1 reads a UTF-8 script with no BOM as ANSI, so `più` and `déjà` in the *script* arrive mangled in the file. Generate the three resx files from one table with Python, or with a BOM'd script; and check with `grep -c` that the accented characters survived before trusting `dotnet format`, which is happy either way |
| **`System.Text.Json` escapes `<`, `>`, `&` and `+` by default** | A redaction placeholder of `<redacted>` was written into every stored crash report as `<redacted>`: correct on the wire, and unreadable in the file — which is half of what that file is for. Nothing failed; the unit tests assert on the record and never on the serialised bytes, and it took driving the real thing to see it. The placeholder is `[redacted]` now. Relaxing the encoder globally would have been the other fix and a worse one: it is the same options object every API body uses |
| **Awaiting `IAsyncRelayCommand.ExecuteAsync` twice while the first call is still running hangs the test host** | The second `await` never returns, and nothing prints: `dotnet test` sits at "1 test file matches" until it is killed, which reads like a broken build rather than one deadlocked test. It cost a cycle on the Explore scroll tests. When the point of a test is that a *second* call does nothing, assert on that directly — `Assert.True(model.LoadMoreAsync(token).IsCompleted)` — which cannot hang and says what is meant. Call the command itself only where the call completes |
| **`Progress<T>` posts its callback to a captured context** | With no `SynchronizationContext` — which is every test — that means the thread pool, so a test that asserts on what a progress callback recorded is asserting on whether the pool has caught up. Both test projects have a synchronous `IProgress<T>` for this; the view model funnels every report through one property so the same path is exercised either way |
| **The Fluent theme paints a disabled button's background whatever the class says** | `Button.link` sets `Background=Transparent`, and a *disabled* link still came out as a grey box, because the theme styles the templated `ContentPresenter` and that wins. A link waiting on an empty field therefore looked like a broken button. The fix is a selector that reaches into the template — `Button.link:disabled /template/ ContentPresenter#PART_ContentPresenter` — and nothing in the suite could have caught it: it was found by looking at the window |
| **The real window can be driven from PowerShell with UI Automation, and a button's name is its `Content`** | `[AutomationElement]::FromHandle($p.MainWindowHandle)` plus `ValuePattern` and `InvokePattern` types text into the boxes and presses the buttons of the running launcher — which is the only way to press a real button, since there are no UI tests. One trap: the automation name comes from `Content`, so `Content="{loc:Tr Auth.ForgotPassword}"` is findable by its text, while a button whose content is a `Panel` of two `TextBlock`s is named **`Avalonia.Controls.Panel`**. Searching for the visible text then finds the *TextBlock*, and invoking it fails with "Pattern non supportato" — which reads like UIA being broken rather than the wrong element |
| **`openssl` *is* on this machine, through Git for Windows** | `/mingw64/bin/openssl` (3.2.4), on `PATH` inside Git Bash. The backend's notes say it is absent and to run it inside the toolchain image, which is one more container round trip than generating a key pair or signing a document needs. It is not on PowerShell's `PATH`, which is presumably where that note came from |
| **A named EC curve comes back in different halves of an `Oid` on different platforms** | `ECDsa.ExportParameters(false).Curve.Oid` populates `Value` on some runtimes and only `FriendlyName` (`nistP256`, `ECDSA_P256`, …) on others, so a P-256 check written against one of them passes locally and rejects every key on another platform — with the launcher then silently checking for no updates. `ReleaseSignature.IsP256` accepts either, and the suite covers it by importing keys it generated itself |
| **A `TryParse` with an `out T?` fails the build at its *caller*** | `if (TryParse(bytes, out ReleaseDocument? doc, out _)) { doc.Version … }` is CS8602 under `TreatWarningsAsErrors` unless the parameter carries `[NotNullWhen(true)]`. The error points at the caller, so it reads like the caller being wrong rather than the signature being under-annotated |
| **A running launcher locks `GameLauncher.exe`, and `dotnet test` then fails as MSB3027** | Driving the window by hand and running the suite in the same session do not mix: the App project's post-build copy cannot replace a running executable, and the failure ("il file è bloccato da: GameLauncher (pid)") reads like a corrupted build. Stop the process first |
| **An indexer binding is invalidated by `PropertyChanged("Item")` — not by WPF's `"Item[]"`, and not by `null`** | This is what kept every `{loc:Tr}` label in the language it first rendered in from milestone 1 until 2026-08-07, while `WelcomeMessage` and the update banner followed, because a view model rebuilds those in code. `"Item[]"` is `Binding.IndexerName` in WPF and Avalonia's indexer node ignores it; so, surprisingly, does `null`, which almost every other binding system reads as "every property changed". Only the indexer's own CLR property name works. Measured rather than guessed, against Avalonia 12.1.1: `"Item[]"` STUCK, `"Item"` FOLLOWS, `null` STUCK, `""` STUCK. `TrExtensionTests` pins it by driving a real Avalonia binding — a test asserting only *which* name is raised would have passed against the broken code just as happily |
| **A binding on a plain `AvaloniaObject` needs no initialised Avalonia** | Which is what makes the language switch testable at all: `AvaloniaProperty.Register` + `Bind(property, binding)` + `GetValue` exercises the real binding machinery with no toolkit start-up, no window and no dispatcher. Contrast `Bitmap` two rows up, which cannot be constructed without one. Anything expressible as "does this binding re-evaluate?" belongs in a test rather than in a session driving the window |
| **A sentence a view model builds itself is not a binding and never follows a language change** | `WelcomeMessage` and the update banner's three lines are `Translate` calls with arguments, so they are ordinary properties: fixing the `{loc:Tr}` half above does nothing for them, and `RefreshLocalizedText` in `MainWindowViewModel` stays exactly as necessary as it was. The rule to keep: **anything built with `Translate(key, args)` must be rebuilt on `LanguageChanged`** |
| **An `AfterTargets="Publish"` MSBuild target gets an *absolute* `PublishDir` when `-o` is used** | `GameLauncher.App.csproj` publishes the updater into `$(PublishDir)updater\`, and combining that with `$(MSBuildProjectDirectory)` produced `…\src\GameLauncher.App\C:\Users\…\dist\new\updater\` and an MSB3191 that reads like a broken path in the SDK. `$([System.IO.Path]::GetFullPath('$(PublishDir)updater', '$(MSBuildProjectDirectory)'))` is right for both shapes, because that overload leaves a rooted path alone. Plain `dotnet publish` with no `-o` never shows it |
| **`Compress-Archive` in Windows PowerShell 5.1 can write `\` in zip entry names** | The launcher refuses those — `ManifestPathRules` bans a backslash, which is the same rule that stops `..\` — so an archive built that way is a release nobody can install, refused for a reason that sounds like an attack. It runs on .NET Framework, where `ZipFile` predates the fix. Build a release archive with Python's `zipfile` (or .NET Core's `ZipFile`), and normalise the entry names to `/` yourself |
| **`Graphics.CopyFromScreen` photographs whatever is on top of that rectangle** | It captured an unrelated window sitting over the launcher, so the screenshot showed something else entirely. `PrintWindow($hwnd, $hdc, 2)` renders the target window itself, works when it is occluded, and does **not** steal focus from whatever the maintainer is doing. Also: `Add-Type -MemberDefinition` already emits `using System.Runtime.InteropServices;`, so passing `-UsingNamespace` for it fails the compile as a warning-as-error |

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
- **CI runs on `main`**, not on `dev`, across `windows-latest` / `ubuntu-latest` /
  `macos-latest`. Since `main` is merged by hand by the repository owner, no run is triggered by
  anything this repository's work does — which makes the local check the real gate.

### The gate before a push is local, and it is not optional

Nothing on GitHub will catch a red suite on `dev` any more, so **both of these have to pass
before `git push`**, every time:

```bash
dotnet test GameLauncher.sln
dotnet format GameLauncher.sln --verify-no-changes
```

A push made without running them is a push made on hope. The one thing they cannot cover is the
macOS leg and the four self-contained publishes, which only exist on a runner (§7) — so a change
that touches platform-specific code or the publish configuration is worth saying out loud as
unverified rather than quietly assuming.

### Finishing a milestone

Pushing `dev` at the end of a milestone is **not** something to ask permission for — run the two
commands above, then push. Mid-milestone pushes remain the maintainer's call.

```bash
git push origin dev
# gh lives in "C:\Program Files\GitHub CLI" and is not on an already-open shell's PATH
gh run list --branch main --limit 3         # only after the owner has merged into main
gh run view <id> --log-failed               # only what failed, not the whole log
```

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
- ✅ xUnit test projects, GitHub Actions CI matrix (on `dev` then; on `main` since 2026-08-07)

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

### Milestone 7 — The download engine ✅
- ✅ `IDownloadApi`: the plan, the manifest verified against its published hash (D19), and the
  integrity check; `DownloadPlan` / `PlannedFile` / `IntegrityReport` mirroring `DownloadJson`
- ✅ `SqliteInstallStore`: what is installed on this machine, one row per game, versioned
  schema, WAL (D21) — the local state D4 named at project start and nothing had needed yet
- ✅ `BlobFetcher`: `Range` resume, hash verification before a blob takes its content address,
  a server that ignores `Range` handled, 410 apart from 403 (D25), on a third `HttpClient`
  that carries no token (D20)
- ✅ `InstallationService`: plan → space check → parallel fetch → verified copy → apply →
  record, with `copyFrom` taken off local disk, `remove` and its emptied directories swept,
  and the row flipped last (D22, D23)
- ✅ Manifest paths re-validated against the install root (D24)
- ✅ Uninstall with freed bytes; on-demand verify that records a broken install as broken
- ✅ The game page installs, updates, repairs, verifies and removes, with a phase, a bar, a
  speed and an estimate (D26); `ByteSize` shared so a size reads the same everywhere
- ✅ 27 new resource keys in English, Italian and French

### Verified on 2026-08-03
- 321/321 tests green (120 Core, 128 Infrastructure, 73 App)
- `dotnet format --verify-no-changes` clean
- **End to end against the real stack**, driven by a ten-line console project over the client's
  own DI graph (see §6): a seeded publisher, one game, two builds. The first install was a full
  download **through nginx over signed URLs**, landed all three files, recorded the manifest
  hash the catalog advertised and swept staging. Verification called it intact. The update to
  0.2.0 was a delta that moved **47 bytes — only the changed executable** — copied the moved
  asset off local disk without fetching it, left the copy's source in place, deleted the
  dropped library and pruned the directory it emptied. A tampered install was reported with the
  corrupt file, the missing file and the save file as *unexpected*, was recorded as broken, and
  reinstalling repaired it as a full download while leaving the save file alone. Installing an
  up-to-date build moved nothing, and uninstalling removed the directory and the row

### Milestone 8 — Launching, publishing and offline ✅
- ✅ `LaunchPlanner` + `ProcessGameLauncher`: start an installed game, know when it exits,
  refuse the four cases that cannot work (D27); per-game launch options in their own column (D28)
- ✅ The library binds to a card that carries both what the account owns and what this disk
  has, and plays it — open debt 8, closed
- ✅ Offline: an unreachable server keeps the session, and the library falls back to the
  install store with a banner; a refusal is still an error (D29)
- ✅ `IPublishingApi` + `PublishingApiClient`: games, versions, builds, blob negotiation,
  resumable upload sessions, manifest submission (D30)
- ✅ `DirectoryBuildPackager`: a directory hashed once, with the server's path rules applied
  before anything travels
- ✅ `BuildPublisher`: package → negotiate → upload → finalize, resuming at whatever offset
  the server names (D31)
- ✅ The developer dashboard: own games including drafts, versions, and publishing a build
  from a chosen directory with progress (D32)
- ✅ 48 new resource keys in English, Italian and French

### Verified on 2026-08-04
- 420/420 tests green (150 Core, 166 Infrastructure, 105 App)
- `dotnet format --verify-no-changes` clean
- **End to end against the real stack**: the client created a game, published a three-file
  build from a directory, then published a second build that changed one file — **one blob
  travelled, 49 bytes, and the unchanged asset was recognised as already stored**. The same
  client then installed the first build as a full download, verified it against the manifest,
  updated to the second **as a delta of exactly those 49 bytes**, and dropped the file the new
  build no longer had. Launching the published "executable" — a text file — was refused with
  `StartFailed` rather than swallowed, and the install row was readable with no server involved

### Closing what milestone 8 left open — verified on 2026-08-04
- ✅ A settings page, and `UserSettings.InstallDirectory` finally read (D33) — core feature 7
  of the plan had been declared since milestone 1 and never implemented
- ✅ Startup recovery: an install left mid-apply is recorded as damaged, abandoned staging is
  swept by age (D34) — open debts 8 and 9, closed
- ✅ 436/436 tests green (150 Core, 173 Infrastructure, 114 App), `dotnet format` clean

### Artwork and the devlog — verified on 2026-08-05

Not a milestone: open debt 2 of `HANDOFF.md`, the largest user-facing gap left. The server had
been serving `coverUrl`, `media` and `GET /games/{id}/patch-notes` since the debt session of
2026-08-04 with no client reading any of it, so core feature 1 of the plan — "images and
screenshots, a news/patch-notes section with clickable devlog cards" — was declared and
unimplemented.

- ✅ `GameMedia`, `PatchNote` and `Game.CoverUrl` on the catalog contract; `GameDetail.Media`
  with `Artwork(kind)` and a `Screenshots` gallery sorted by the publisher's order
- ✅ `ICatalogApi.GetPatchNotesAsync`: the devlog on its own paged route
- ✅ `IImageLoader` / `CachingImageLoader`: a fourth `HttpClient` with no bearer token, a disk
  cache keyed by URL, a size cap, and format decided by the leading bytes (D35, D36)
- ✅ `IImageProvider` in the App layer, decoding once per URL for the life of the process (D37)
- ✅ Covers on every Explore and library card, with a framed placeholder while they arrive
- ✅ The game page: a banner-or-cover hero, a screenshot gallery with a thumbnail strip, and a
  paged devlog whose failures stay out of the page's (D38)
- ✅ 6 new resource keys in English, Italian and French
- ✅ 470/470 tests green (150 Core, 191 Infrastructure, 129 App), `dotnet format` clean

Still not done on this surface, and deliberately: **the dashboard cannot upload artwork or
write a devlog entry**. The four media routes and the four patch-note routes exist server-side
and no publishing screen calls them, which is the publisher half of the same debt and belongs
with open debt 10 (a dashboard that creates but never modifies).

### Capabilities — verified on 2026-08-05

Open debt 9 of `HANDOFF.md`, the client half, and the numeric part of debt 12.

- ✅ `ServerCapabilities` with a conservative `Fallback`, `ICapabilitiesApi` on the tokenless
  client, and `CachedServerCapabilityProvider` (D39)
- ✅ `BuildPublisher` sends the chunk size the server announced; `DirectoryBuildPackager` uses
  the server's `maxBlobBytes`, `maxFiles` and `maxPathLength` (D40)
- ✅ 487/487 tests green (150 Core, 208 Infrastructure, 129 App), `dotnet format` clean

### `Documentation/` per module — verified on 2026-08-05

Open debt 4 of `HANDOFF.md`. This repository had only `architecture.md` from day one, and it is
the repository a new contributor orients themselves in most: it is the one that explains what
the user sees, and it is open source.

- ✅ Eight documents, matching the backend's granularity, indexed in the README:
  `architecture` (rewritten), `authentication-and-session`, `catalog-and-artwork`,
  `downloads-and-installs`, `launching-games`, `publishing`, `configuration-and-localization`,
  `logging-and-local-state`
- ✅ Each states what is **deliberately not implemented** rather than leaving it to be inferred
- ✅ Two stale README claims corrected: the status line predated authentication, and the feature
  list advertised self-updating as if it worked

### The dashboard modifies what it publishes — verified on 2026-08-05

Open debt 2 in *writing* and the whole of open debt 10. Eight server routes had no caller: a
publisher uploaded a cover with `curl`, and a game created as a draft was published from
outside the launcher.

- ✅ Eight methods on `IPublishingApi`, never on `ICatalogApi` (D30): media upload / edit /
  delete, patch-note create / edit / delete, `DELETE` on builds and versions
- ✅ `MediaUploadRules` validates against `MediaCapabilities` with **no constant of its own** —
  the last piece of open debt 9 (D42). The limits are on the page *before* a file is chosen
- ✅ `ImageFormats` moved to Core and is shared by the loader and the uploader; the client
  sniffs to refuse early and never to vouch, and declares no content type (D41)
- ✅ Three child view models under one page, shown as tabs (D44): `GameEditorViewModel`,
  `GameMediaViewModel`, `GameDevlogViewModel`
- ✅ Gallery reordering as two arrows, each swap two explicit `PATCH`es
- ✅ `PendingDeletion`: a deletion is armed with a sentence that says what disappears, and the
  wording is asserted on (D43)
- ✅ `IFilePicker` joins `IFolderPicker` as the only untestable step; it returns bytes, capped
- ✅ 57 new resource keys in English, Italian and French
- ✅ 575 tests green (167 Core, 223 Infrastructure, 185 App), `dotnet format` clean

### Covers offline, and a search that waits — verified on 2026-08-05

Open debt 11, and the debounce half of open debt 7. Two small things the user touches daily,
in two atomic commits.

- ✅ `InstalledGame.CoverUrl` and a `cover_url` column added by **appending** a migration; a
  database written before it existed migrates and its rows take the empty default (D45)
- ✅ `InstallationService` rewrites the cover on an update but never replaces one with nothing —
  a response without a cover is not a publisher who removed one
- ✅ `LibraryViewModel` builds the offline card with the row's URL, so the artwork cache — which
  is keyed by URL and needs no server — answers from disk
- ✅ `ExploreViewModel` takes a `TimeProvider`, debounces the search box at 300 ms and cancels
  the request a new search replaces; Enter searches at once and drops the pending debounce; a
  cancelled search never becomes an error message (D46)
- ✅ `FakeTimeProvider` in `GameLauncher.App.Tests` implements `CreateTimer`, so the debounce is
  advanced by hand rather than waited out
- ✅ 584 tests green (167 Core, 227 Infrastructure, 190 App), `dotnet format` clean
- ✅ No new resource keys: neither change says anything new to the user

**Infinite paging in Explore is still not done**, and was deliberately left out of this work: it
is the other half of open debt 7 and a different piece of work, and mixing the two would produce
a commit that cannot be reverted by halves.

### Deleting an account, and deleting a game — verified on 2026-08-05

The client half of the server's M10 erasure work, and of open debt 15. Both routes had shipped
server-side the same day with nothing calling them.

- ✅ `IAccountApi` on the **authenticated** client — its own interface rather than a method on
  `IAuthApi`, which is the tokenless one (D14) — and `POST /api/v1/me/deletion`, a POST because
  the password needs a body
- ✅ `IAccountService` composes the erasure out of the route and the sign-out, in that order and
  conditionally, because putting it on `IAuthenticationService` would close a cycle through
  `BearerTokenHandler` that the container refuses to build (D47). A DI test asserts it
- ✅ The Settings page arms the erasure as a `PendingDeletion` and asks for the password again;
  the prompt says published games **survive** the account, in one of two wordings (D48)
- ✅ `IPublishingApi.DeleteGameAsync`, and a delete button at the bottom of the dashboard's
  Details tab whose prompt names other people's libraries and offers `draft` instead
- ✅ Deleting the selected game clears the selection rather than letting the list fall through:
  four tabs showing a game that no longer exists is worse than showing nothing
- ✅ 12 new resource keys in English, Italian and French
- ✅ 611 tests green on Windows (172 Core, 235 Infrastructure, 204 App), `dotnet format` clean

Installed games are deliberately **not** removed when an account is erased: the files belong to
the machine rather than the account, and the server never knew about them.

### Crash reports, sent at last — verified on 2026-08-06

The client half of the last part of M10, and open debt 13. `SendCrashReports` had been in
`UserSettings` since milestone 1 with nothing reading it, which is why the Settings page had no
checkbox: an inert one would have been a promise the launcher did not keep.

- ✅ The crash file is now the **request body** — one JSON document in the shape the server
  accepts — and is redacted at the moment it is written rather than when it is sent (D49)
- ✅ `CrashReportUploader` runs once at startup, before the session is restored, because a report
  is sent without one and the crashes worth having happen on the sign-in screen (D50)
- ✅ Withdrawing consent **deletes** the pending reports rather than merely not sending them
- ✅ `ICrashReportApi` on the tokenless client, a third one to join `/auth` and `/capabilities`
- ✅ `ServerCapabilities.CrashReports.Enabled` defaults to **false**, unlike every other
  capability: the rest are limits on something the launcher was going to do anyway, and this is
  permission to send something about the user
- ✅ 3 new resource keys in English, Italian and French
- ✅ 643 tests green on Windows (182 Core, 253 Infrastructure, 208 App), `dotnet format` clean

### Explore scrolls — verified on 2026-08-06

The other half of open debt 7, and with it the debt is closed. The Previous/Next pair is gone.

- ✅ `LoadAsync` replaces the list and `LoadMoreAsync` appends to it, as two entry points rather
  than one with a flag (D51); `HasMore` comes from the server's count and `HasEnded` says so on
  screen
- ✅ One request in flight at a time, the page number advanced only by an answer that arrived,
  and the list emptied only by a replacement — a new search or a new sort order
- ✅ `Views/InfiniteScroll`, an attached behaviour that fires the command near the bottom and
  returns the offset to the top when the list is replaced. It holds no policy, so what a test
  cannot press is only the geometry
- ✅ Covers are fetched for the appended cards alone, not for the whole grid again
- ✅ 3 new resource keys in English, Italian and French; `Explore.PageOf` retired with the pager
- ✅ 649 tests green on Windows (182 Core, 253 Infrastructure, 214 App), `dotnet format` clean

**Verified against the real stack** with a throwaway console harness over the client's own DI
graph: 63 public games walked page by page — appended and never substituted, no duplicate across
four pages, the whole catalog reachable, the end announced rather than discovered, a scroll at
the end asking nothing, and a search emptying the list and starting again. **11 checks of 11
green.** What that cannot reach is the gesture itself: the threshold and the return to the top
need a window and were not driven by a test.

### Mail delivery, the client half — verified on 2026-08-06

Open debt 1 of `HANDOFF.md`, whose client half was exactly one thing: stop reading fields that
no longer exist. The server now sends the verification and reset links itself and serves the
pages they land on, so nothing here had to grow a screen for either flow.

- ✅ `RegistrationResult.DevEmailVerificationToken` and the reset token are gone;
  `RequestPasswordResetAsync` returns nothing (D52)
- ✅ `LoginViewModel` reads `verificationEmailSent` and says one of two things, both asserted on
- ✅ 1 new resource key in English, Italian and French
- ✅ 651 tests green on Windows (182 Core, 254 Infrastructure, 215 App), `dotnet format` clean

Still deliberately absent, and worth saying because it is what somebody will look for: the
launcher has **no screen for confirming an address or resetting a password**, and
`VerifyEmailAsync` / `ConfirmPasswordResetAsync` on `IAuthApi` still have no caller. Both flows
are finished in a browser, on pages the server serves. `POST /auth/verify-email/resend` has no
client method either — the sign-in screen can say the message did not go out, and cannot yet
offer a button that asks for another.

### Account recovery, the half a person can reach — verified on 2026-08-06

Open debt 23. The server had been able to send both links since the morning, and nothing in the
launcher asked for either: on a real deployment somebody who forgot their password stayed out,
and somebody who registered while the relay was down could not even register again, because the
address was taken and the route answers 409.

- ✅ `IAuthApi.ResendVerificationEmailAsync` — the one route that had no client method at all —
  and two pass-throughs on `IAuthenticationService`, on the precedent of `RegisterAsync` (D53)
- ✅ "Forgotten your password?" on the sign-in form, and "Send the confirmation link again" in
  the state where it means something: after any registration that needs verifying, and after a
  sign-in refused with 403
- ✅ A 403 on sign-in now says **confirm your address** instead of "your account is not allowed
  to do that" — the server gives an unconfirmed address and a disabled account the same code,
  and this says the one that can be acted on
- ✅ Every success sentence is conditional, and a test asserts an address that does not exist is
  answered identically — the client does not undo the server's refusal to be an enumeration
  oracle
- ✅ 429 on the info line with the wait named, 404 read as "this server sends no email", and a
  successful request that disarms its own button until the address changes. **No countdown**
- ✅ 8 new resource keys in English, Italian and French
- ✅ 672 tests green on Windows (185 Core, 257 Infrastructure, 230 App), `dotnet format` clean

Both flows still **end in a browser**, and `VerifyEmailAsync` / `ConfirmPasswordResetAsync`
still have no caller — now as a decision with its reasons written down (D53) rather than as the
thing nobody got to.

### Self-update, the check — verified on 2026-08-07

The first of the two client pieces the server's release surface was waiting for. The launcher
now finds out that a newer launcher exists, proves it, and says so; installing it is the second
piece and is still a stub.

- ✅ `Core/Updates`: `ReleaseDocument` (parsed only after its signature verifies),
  `ReleaseVersion` (numeric, strictly newer), `ReleaseSignature` (ECDsa P-256/SHA-256, pinned,
  never throwing), `ReleaseTargets`, and `UpdateChecker` — which holds the backend's five rules
  and two refusals beyond them (D55)
- ✅ `LauncherReleaseKey.PublicKeyBase64`, empty in this repository: **the one thing a fork
  changes in code**, and an empty key means the launcher asks for nothing (D54)
- ✅ `updates.channel` in `launcher.config.json`, `stable` or `beta`, with anything else read as
  `stable` rather than failing validation (D54)
- ✅ `ILauncherReleaseApi` on the tokenless client — a fourth one, beside `/auth`,
  `/capabilities` and the crash reports — and `ILauncherUpdateDownloader` on a client shaped
  like the file server's, which refuses bytes that are not the ones the signed document named
- ✅ One line in the shell: the version, the notes, a button that downloads and verifies, and a
  way to put it away for this run. Nothing installs itself (D56)
- ✅ [Documentation/self-update.md](Documentation/self-update.md), the ninth page, which states
  what is **not** implemented as plainly as what is
- ✅ 5 new resource keys in English, Italian and French
- ✅ 785 tests green on Windows (271 Core, 276 Infrastructure, 238 App), `dotnet format` clean

**Verified by hand against the real stack**, because the suite has no file server and cannot
hold a private key the way a person does: a key pair generated offline, a release published with
`--publish-release`, and the client's own DI graph asking the route with **no token**. A launcher
declaring 0.1.0 was offered 0.4.0 and fetched an artifact that hashed to what was signed; the
same live release was refused by a launcher already on 0.4.0, by one on 0.9.0 (the replay), by
one holding another key, and asked for nothing at all by one with no key. Then the artifact was
**edited on the file server** with the document and signature untouched, twice — one byte longer,
and one byte changed — and both were refused, the second at the hash with a log line naming the
release. Finally the real window, driven by UI Automation: the banner appeared on the sign-in
screen, the button downloaded and verified, and the line said where the archive is rather than
claiming a restart.

**One bug found and fixed by looking at the window** (D56), and one found and *not* fixed: the
`{loc:Tr}` bindings do not follow a language change at all. See §7 and `HANDOFF.md`.

### The language switch, which had never switched — verified on 2026-08-07

Open debt 28, and the promise D3 has been making since milestone 1. Nothing in the suite could
see it, and nothing in the window could miss it: the selector changed `WelcomeMessage` and the
update banner and left "Accedi", "Indirizzo email" and every other label alone.

- ✅ `LocalizationSource` raises `PropertyChanged("Item")`. `"Item[]"` is WPF's convention and
  Avalonia's indexer node ignores it — as it also ignores `null`, which was the obvious first
  fix and does not work either (§7 has the measurement)
- ✅ `TrExtensionTests` binds a real `AvaloniaObject` property to what `TrExtension` produces and
  asserts the value follows a language change. Two of its three tests fail against the old
  notification name, so it is a regression test rather than a restatement of the code
- ✅ `RefreshLocalizedText` is untouched and still necessary: the banner's lines are
  `Translate(key, args)` calls, not bindings
- ✅ 788 tests green on Windows (271 Core, 276 Infrastructure, 241 App), `dotnet format` clean
- ✅ No new resource keys: nothing new is said to the user

**Verified in the running window**, driven by UI Automation, because there are no UI tests. Every
`ControlType.Text` and `ControlType.Button` was read before and after the switch: Italian →
English turned "Accedi/Indirizzo email/Password dimenticata?/Non hai un account? Creane uno" into
"Sign in/Email address/Forgotten your password?/No account yet? Create one", and English → French
turned all of them again — **the whole window at the first attempt, not only the line at the
bottom**. Restarting reopened it entirely in the saved language, which is also the end of a
window that could come up half in one language and half in another: start-up sets the language
after some labels have rendered, and those labels now re-read. Then the same switch with the
**update banner** on screen and an artifact already downloaded: the headline, the release notes,
the outcome sentence naming the archive's directory, and both banner buttons all changed once
and stayed changed.

### Self-update, the swap — verified on 2026-08-07

Open debt 24, and with it **the last declared-and-unimplemented feature in either repository**.
`GameLauncher.Updater` exited with 3 and moved no files; it now replaces the installation and
puts the old one back when the new one does not start.

- ✅ `Core/Updates`: `RelaunchWatch` (the verdict as a pure function of exit code and elapsed
  time), `UpdateSwapRequest` (one definition of a command line two processes share, round-tripped
  by a test), `UpdateSwapPaths` (`<install>.previous`, a sibling so the rename stays atomic) and
  `UpdateArchiveRules` — which is `ManifestPathRules` + `PathSafety`, not a second copy (D57, D58)
- ✅ `IUpdateInstaller` / `UpdateInstaller`: unpack into `updates/<version>/staged/` refusing an
  entry that would land outside it, copy `<install>/updater/` out of the directory about to be
  renamed, start the helper. Core stays free of file I/O, which it has always been
- ✅ `GameLauncher.Updater`: `SwapRunner` plus two substituted interfaces and `Main`. `--rollback`
  is a real flag now, and the exit codes distinguish *nothing happened* (2) from *the old one is
  back* (4) from *this needs a hand* (5)
- ✅ The updater is published **self-contained** into `<install>/updater/` by a target in
  `GameLauncher.App.csproj`, trimmed and invariant-globalized: 19 MB rather than 75
- ✅ `IApplicationShutdown` in the App layer, so a test can press the button without ending the
  test host — the budget D32 spends on the file picker, spent on the one other untestable step
- ✅ The banner's button is **"Update and restart"**; `Update.Ready` is retired and
  `Update.Restarting` replaces it, in English, Italian and French
- ✅ `tests/GameLauncher.Updater.Tests`, new and in the solution: the swap against real
  directories with the launcher substituted — the happy path, the rollback with the old launcher
  started again, the declared hole, a `--target` that does not exist, a launcher still running,
  a `previous` left by an earlier attempt, and `--rollback` with nothing to roll back to
- ✅ 831 tests green on Windows (296 Core, 282 Infrastructure, 11 Updater, 242 App),
  `dotnet format` clean

**Verified on real Windows**, which is the platform that executable exists for and the one thing
no test reaches. Two self-contained publishes: 0.6.0 signed, hashed and published through
`--publish-release`, and 0.1.0 installed. The 0.1.0 launcher offered 0.6.0, the button was
pressed, and **1.5 seconds later** the log reads `Updater 1772 started; this launcher must now
exit` and then `Launcher starting` — the installation directory now holds 0.6.0, `previous` is
gone, and the window is up. Then the case that matters more: a **0.7.0 whose artifact is a
launcher that returns 1 on start-up**, signed exactly like a real one. The updater put it in
place, watched it fail, restored the installation to 0.6.0 and started it again; the window came
back offering 0.7.0 once more, which is the honest consequence of a design that remembers
nothing about a failed release.

**No bug found in the swap.** Two things cost time and are in §7: an `AfterTargets="Publish"`
target that mis-joins an absolute `PublishDir`, and PowerShell 5.1's `Compress-Archive`, whose
backslash entry names the launcher correctly refuses.

### Next up

The numbering is shared with the backend repository. Self-update is not a numbered milestone —
it was part of M8 in the original plan and came out of it because it cannot be built here alone.

**Nothing.** As of 2026-08-07 there is no feature this repository declares and does not
implement, and the same is true of the backend. What is left is written down as debts and
deliberate absences rather than as work in progress: no virtualisation in Explore (open debt 21),
`UserSettings.LaunchMinimized` still inert (open debt 13), no data export (17), the detail page
still needing a server (11), and no automated test that touches nginx or a real swap (8, 22).
Each of those is a choice with its reasons recorded, not a gap somebody forgot.

- ✅ ~~**The self-update swap**~~ — done on 2026-08-07, above
- ✅ ~~**The language switch that does not switch**~~ — done on 2026-08-07, above

---

## Session protocol

### Every finished task ends with a recap of what is left

**Not optional, and not only at the end of a milestone.** Whenever a task is finished — a
feature, a fix, a piece of documentation — the last thing said is a short written recap of
**what remains to be done**, in the conversation itself rather than only in a file.

It says three things and stops:

1. what was delivered, in a line;
2. **what is left in the piece just touched**, including anything deliberately left out and why;
3. what is next, and anything that is now blocked or newly known — a bug found in passing, a
   claim elsewhere that this work has just made false.

The reason is that this repository's memory lives in files a later session has to *choose* to
read, while the person deciding what to do next is reading the conversation. A task that ends
with "done" leaves them to reconstruct the remainder from a diff. It is also the moment a
half-finished thing is most honestly describable: an hour later it looks finished.

Keep it short. If the recap needs more than a screen, the work needed a `HANDOFF.md` entry too.

### At the end of every working session, update:

1. **§10 Progress** — move items between ✅/🚧/⬜, add what is genuinely next.
2. **§3 Technical decisions** — append any new decision *with its rationale and the
   alternatives rejected*. Never delete a row; if a decision is reversed, add a new row that
   supersedes it and say why.
3. **§6 Commands** — add any command a future session would otherwise have to rediscover.
4. **§7 Environment gotchas** — record anything that cost time to figure out.

Keep it accurate over optimistic: a wrong progress table is worse than no progress table.

### At the end of a milestone, additionally

5. **Run the suite and the formatter locally, then push `dev`** — see §9. Not something to ask
   about. CI runs on `main`, which only the owner merges, so there is no run to watch.
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
