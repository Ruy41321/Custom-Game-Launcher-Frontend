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

This repository is the **client**: an Avalonia application for **Windows and Linux** (D59).
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
| D1 | **Avalonia 11 + CommunityToolkit.Mvvm** | One XAML codebase across Windows and Linux with a mature MVVM toolkit and source-generated boilerplate. | MAUI (weak Linux story); Electron (runtime weight) |
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
| D12 | **LF line endings, enforced by `.gitattributes`** | The solution is built on two operating systems and `dotnet format` checks line endings; mixed endings would fail CI on some legs and not others. | Platform-default endings |
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
| D59 | **The client targets Windows and Linux; the server targets Linux. macOS was dropped on 2026-08-07** | It was never a *supported* platform, only a green CI leg: §7 has said since day one that no macOS machine exists here, so every osx claim in this repository was a claim that a runner compiled something — never that it started, never that anybody looked at a window on it. Two things made keeping it dishonest rather than merely optimistic. A `.app` needs an Apple Developer ID to be signed and notarized or Gatekeeper refuses it, and nothing in this repository does that, so the artifact CI produced could not be installed by anybody. And the **self-update cannot be verified there at all**: replacing an installation and restarting it is exactly the kind of thing that behaves differently per platform, and §7's rule is that a piece like that is verified on real hardware. Two RIDs that somebody can actually run beat four where two are decoration. The server was already Linux-only in practice — it ships as a `docker compose` stack — and now says so. What this does **not** change: `GamePlatform` still carries `MacOS`, because a *publisher* may distribute a macOS build of their game through this launcher, and the platform of a game is a different question from the platform of the launcher. | Keeping macOS as a target (a claim nobody can check, and an unsigned artifact Gatekeeper refuses); deleting the macOS branches from `PathProvider` and `RuntimePlatform` (a fork that wants to add it back would have to rediscover them, for no gain today); dropping `MacOS` from `GamePlatform` (a migration, and it would stop publishers distributing what they like) |
| D60 | **The devlog is Markdown after all, parsed in Core by something that produces text and nothing else — D38's second half is superseded as of 2026-08-17** | D38 refused to render Markdown on the grounds that rendering remote Markdown is rendering remote markup, and that reasoning is sound *about a general renderer*: the danger in Markdown is the parts that reach outside the text — embedded HTML, remote images, and links that navigate. `MarkdownParser` produces none of them. HTML is text, the image syntax stays as typed, and a link becomes `label [url]` rather than something pressable, because navigating to an address a publisher wrote is a capability and this is a text renderer. What is left — headings, emphasis, lists, code — is what the publisher was writing anyway and what arrived on screen with the asterisks still in it, which is the bug. It is hand-written rather than a dependency for the reason D11 refused a fluent-assertion library, and it sits in Core with no Avalonia near it so the meaning is unit-tested and only the *look* needs a toolkit (D37). The one shape it does not reach is emphasis whose runs end together (`**bold *and italic***`), which CommonMark solves with delimiter runs — a parser several times this size for a case a devlog can write the other way round; the limitation is a comment on `Markers` and a test asserts the shapes that do work. | A Markdown package (a dependency, and every one of them renders links and images because that is what Markdown is); leaving the raw text (the complaint this fixes); rendering links as clickable (a capability, granted to whatever a publisher typed); a full CommonMark implementation by hand (weeks, for a devlog) |
| D61 | **The game page hides a button rather than disabling it, and says where it went — and two of them now depend on the install** | Three rules, one shape. **Play disappears while an update is pending**, because an update is not optional: a player who starts an old build talks to a server, saves in a format or joins a session the new one changed, and every one of those failures arrives later looking like a broken game rather than a skipped update. **Remove-from-library disappears while the game is installed**, because leaving the library with the files still here leaves an install the account no longer owns — it cannot be updated and cannot be repaired, and nothing on the page would explain why; uninstalling first is the order that works, and the same rule is on the library card, which knows whether the game is here even though it deliberately knows nothing about updates. And **a game is added to the library when its install finishes**, after rather than before, so a download that never completed leaves no entitlement nobody asked for — with the failure of *that* call reported without taking the install's success away, because the files really are on the disk either way. A disabled button with no explanation is the same dead end with worse manners, so the sentence that replaces Play is part of the change and is asserted on. | Disabling instead of hiding (a grey button that never says why); letting an old build launch (failures that arrive later and blame the game); adding to the library before the download (an entitlement for a game that never arrived); removing from the library with files on disk (an unmanageable install); a badge saying "update available" and Play still there (the badge is advice, and the failure is not) |
| D62 | **A directory chosen for one install is a *root*, and cancelling the question cancels the install** | `InstallRequest` gained `InstallRoot` beside `InstallDirectory`, and the difference is a safety property rather than a convenience: uninstalling deletes the install directory **recursively**, so a build unpacked loose into a folder somebody picked in a dialog would make removing that game a recursive delete of that folder and of everything else they keep in it. The launcher therefore names the game's own directory inside whatever root it is given, by the same `InstallPaths.DefaultInstallDirectory` the default root goes through — one naming rule, in Infrastructure, where the view model cannot reinvent it. The question is asked only for a game that is not here yet, because an update goes where the game already lives (D33) and asking again would invite an answer the first install is not in. And a cancelled dialog aborts: falling back to the default would install the game somewhere the player has just declined to confirm. `UserSettings.AskWhereToInstall` is off by default — a folder dialog in front of every install is one people turn off after the second game, and `InstallDirectory` already covers wanting games somewhere specific. | Passing the picked path as `InstallDirectory` (uninstall becomes a recursive delete of a folder the user chose); the view model composing the directory name (a second copy of the naming rule, in the layer least able to hold it); asking on every install including updates (an answer that contradicts where the game is); cancelling falling back to the default (installing somewhere just declined); the setting on by default (a dialog between every player and every game) |
| D63 | **The install directory has a button, behind `IFileBrowser`** | Saves, screenshots, logs and mods are all in the install directory and none of them is anywhere the launcher would think to look, so the way there is worth a button rather than a support answer. It is an interface for the reason every other shell-out is (D27, D32) — `Process.Start` is not something a view-model test can be made to do, while deciding *whether* to start one is exactly what a test should press — and it returns a bool rather than throwing, because a page that stopped working because a folder would not open would be worse than the folder not opening. The two platforms are spelled out rather than left to `UseShellExecute`, which resolves differently on each and to nothing on a machine with no desktop session. | `Process.Start` at the use site (untestable, and the DI test cannot catch it); throwing on failure (a page broken by a folder); `UseShellExecute = true` for both (one line, and a different meaning per platform) |
| D64 | **A validation failure is the one refusal that names its rule and the one that drops the request id** | Two halves of the same observation: `invalid_input` is the only refusal the person reading can fix themselves. So it is the only one worth *naming* — the server now sends `rule` and `ruleArgs` (its D60) and `ValidationKeyFor` turns them into `Error.Validation.*` with the limit filled in, which is how "some of what you entered was not accepted" becomes "the password must be at least 8 characters" — with the number coming off the wire, so lowering the server's minimum needed no client release. The mapping is an explicit switch rather than `"Error.Validation." + rule` because the switch **is** the list of what this launcher can say: a missing resource key shows up as a fallback instead of `!Error.Validation.foo!` on somebody's screen, and a test can walk every arm in all three languages. And it is the only one where the request id is **noise**: that reference exists so an operator can find the request in a log, and there is nothing in the log to find, because the server behaved correctly — somebody who typed a short password needs the number, not a UUID. Every other failure keeps it. Two fallbacks make the wire contract additive: a rule this launcher has never heard of, and a sentence whose placeholder arrived without its argument, both give the generic sentence, so a newer server improves messages and can never break one. | Matching on the server's English `detail` (rewording a server message breaks the client); deriving the key from the rule name (a typo becomes `!Error.Validation.x!` in the UI, and nothing enumerates what the client knows); showing `detail` when no key is known (English in the middle of an Italian screen); keeping the reference on validation failures (the complaint this fixes); reading the limits from `/capabilities` instead of the refusal (an async lookup inside a synchronous `Describe`, and the client would have to know which limit each rule meant) |
| D65 | **The version list gets a row view model, and Publish is a button rather than a checkbox** | The row has to say two things a `GameVersion` cannot. **Which builds hang off it** — the server sends versions and builds as two flat lists, so joining them is the page's job, and doing it in a view model is what lets a test read the join instead of inferring it from a template; a build shows its name and falls back to platform and architecture when it has none, which is the same information the list below carries but is the only thing that tells two rows of "0.3.0 beta published" apart. And **whether it is published, as something that changes**, which is the new part: the row is now the thing a button acts on, so it has to be able to say so without the list being rebuilt under the cursor. `VersionRowViewModel` costs one type and rippled into `SelectedVersion`; a converter or a `MultiBinding` would have put the same join in XAML where nothing can press it (D32, D46, D51 all spend this budget the same way). Publishing is a **command, not a checkbox**, because it is a request the server can refuse and a checkbox that springs back is a UI lying about state it does not own — and there is no confirmation prompt, because D43 is about deletions and this one is undone by pressing the other button. | A converter over two collections (the join lands in XAML, untestable); rebuilding the list after each publish (drops the selection somebody is using); a checkbox bound two-way (a refused request leaves the tick where the server did not put it); a confirmation prompt (nothing is destroyed, and D43's budget is for the things that are); putting the build names on `GameVersion` (a wire record inventing a field the server does not send) |
| D66 | **One `MediaCardViewModel` for both screens, and the dashboard's way to the game page is a navigation event, not a page of its own** | Two halves of "the publisher cannot see what they made". The artwork tab listed **alt text and nothing else**, so somebody reordering their own screenshots was reading descriptions they had typed and remembering which picture each belonged to — and the fix was not new machinery, because `ScreenshotViewModel` on the game page was already exactly "a `GameMedia` and its decoded `Bitmap`". Making it *one* type rather than adding a second is the decision: two view models with that same meaning is one shape maintained in two places, which is the argument the server's D30 makes about `mayViewGame`, and the cost is one property (`Media`, which the dashboard's commands act on and the game page never reads). The **preview is fetched after the rows exist**, in the order they are shown, so the list appears at once and fills in; a picture that never arrives leaves its row and an empty frame, because `IImageLoader` reports every failure as null (D36) and a gallery that lost a thumbnail is not a page that failed. The other half is the button to the game's own page, which is a `GameSelected` event the shell listens to exactly as it listens to Explore and the library (D17) — the dashboard does not know the game page exists, and "back" already works because showing the dashboard records it as the list to return to. It is offered **for a draft**, which is not an assumption: `CatalogService::gameDetail` serves a game to whoever may edit it whatever its visibility, checked against the running server before this was built. | A second card type for the dashboard (the same shape in two places, and two ways for a picture to fail); a converter from `GameMedia` to `Bitmap` in XAML (decoding in the layer nothing can test, and no way to express "not yet"); awaiting every picture before showing the list (a page that waits on a dozen downloads to show text it already has); a preview *page* inside the dashboard (a second renderer of the game page, guaranteed to drift from the real one); a command on the shell instead of an event (the child would have to know the shell, which is the one direction D17 forbids) |
| D67 | **The request id leaves every user-facing sentence and goes to the launcher's log instead — half of D64 is superseded as of 2026-08-17** | D64 dropped the reference from validation failures on the grounds that there was nothing in a log to find, and kept it everywhere else on the grounds that an operator needs it. The second half was wrong about *who is reading*. The reference is for somebody with access to the server's logs, and the person in front of the message is the one person on the machine who cannot use it — what they saw was «Email e password non corrispondono a nessun account. (riferimento c832048e…)», where a UUID is noise attached to the one sentence that had already said everything actionable. The need behind it is real, so it is **moved rather than deleted**: `ApiErrorPresenter` writes a warning naming the code, the status and the reference through `ILogger`, so an operator handed a launcher log still finds the request. It logs where it used to append, which keeps the swap exact — a validation failure is still neither shown nor logged, because the server behaved correctly and there is nothing to correlate. `Error.WithReference` is gone from the three resx files, since no code path can produce it and leaving it invites its return. | Keeping it in the sentence (the complaint this fixes); deleting the id outright (throws away the only thing that finds a request in a server log, to fix a presentation problem); logging in `ApiTransport` where the exception is built (it would log the failures this client handles silently by design — a 404 read as "you are up to date" (D55), a capabilities lookup that falls back — turning deliberate silence into warnings); showing it behind a "details" affordance (a second surface, for a string that helps nobody who can see it) |
| D68 | **One test project has a running Avalonia, and it asks every view a single question: does it build?** | The Settings page rendered **nothing** — not a broken control, the whole page — because `Settings.axaml` passed a `{loc:Tr}`, which is a `Binding`, as a `StringFormat`, which is a `string`. Constructing the view threw `InvalidCastException`, the shell's `ContentControl` swallowed it, and what a person saw was an empty rectangle where the install directory, the theme, the crash-report consent and the account deletion all live. Nothing anywhere said so: the launcher ran, 926 tests were green, and the maintainer reported the install-path settings as missing when they had been implemented and documented (D62) for a day. That failure mode — a view that cannot be constructed — is invisible to every test this repository had, because D37 deliberately keeps Avalonia out of view-model tests and it is right to. So it gets its own project rather than a package added to `GameLauncher.App.Tests`: `Avalonia.Headless` sets a process-wide platform up, and letting it into the project that holds 271 view-model tests would put a UI thread underneath the tests written specifically not to need one. Seven views, seven assertions, one dependency, in a project nothing else references — and it fails against the bug, checked by reverting the line. | A package added to `GameLauncher.App.Tests` (a running Avalonia under every view-model test, which is what D37 spends effort avoiding); asserting on the XAML as text (a parser of a parser, and it would not have caught this — the file is valid XAML); leaving it to the window (the status quo: found by a person, a day later, described as a missing feature); a full UI test framework (a headless renderer and a driver, to answer a question a constructor answers) |
| D69 | **The library card asks whether an update is pending — one request per *installed* game, after the list is drawn, and a question it could not ask leaves Play alone. Half of D61 is superseded as of 2026-08-18** | D61 hid Play on the game page and said in as many words that the library card knows the game is here while knowing nothing about updates. That was the bug the maintainer found: the page refused to start a stale build and the list two clicks away offered it. Teaching the card was a change of decision, and the part to design was the cost. **One call per installed game**, not per row and not one bulk call: a library is everything an account has ever been given and grows without bound, while what is installed is bounded by the disk, and a card with nothing on this machine has no Play button to take away. It runs where the covers already run — after the list is on screen, in the order the cards are shown — so the page never waits for it, and it compares exactly what the game page compares, `BuildFor(platform, architecture)` against the install's `BuildId`. The rule that is copied word for word is the shape: the button **disappears** and `Detail.UpdateBeforePlaying` takes its place, the same sentence because it is the same rule, and `PlayAsync` refuses as well, because the check can land between a press and the click. The one place this is deliberately *not* the game page is the unanswered question: **offline no check is made, a refused check leaves its card untouched, and Play stays** — refusing to start a game already on this disk because no server could be reached is what D29's offline library exists to prevent, and the price is a card that can offer Play for the length of one request and then withdraw it. | Gating Play on the check having *answered* (measured against the suite: it makes the offline library unplayable, which is the rule D29 spends the most effort on); one bulk call (no route serves it, and inventing one puts a server release in front of a client bug); a check deferred to the press (the button stays, so the rule that a hidden button is an explained one cannot be kept); a badge saying "update available" with Play still there (D61's own rejected alternative: the badge is advice and the failure is not); one call per row (the same answer at library-sized cost, for cards with nothing to play) |
| D70 | **What a page keeps across a change of account is one rule for every page, and it is keyed on *who*, not on the session event** | The dashboard showed the previous account's game after a sign-out and a sign-in, and `DeveloperViewModel.ClearSelection` already existed with nothing calling it. Fixing that page alone would have left the same bug in four others, so the rule is written once: every page implements `IAccountScopedPage.ResetForAccountChange()`, the shell holds them in `Pages`, and a reflection test over the shell's page properties fails when one is missing from that list — a page absent from it is a page that keeps somebody else's data. Two halves make the rule. **It is the account that is compared, not the event that is trusted**: `SessionChanged` also fires on every token rotation, several times an hour, with the same person behind it, so `_accountId` is compared and a rotation changes nothing on screen. And **the line is the account's data, not the page's state**: what an account's token fetched goes — the library, the search and its results, the game page, the publisher's list, selection and forms, the password in the account-deletion box, the address left in the sign-in form — while what belongs to this machine stays, because the install directory, the theme, the language, the crash-report consent and the install rows on disk do not change because a different person signed in on the same computer. Three consequences worth naming: the dashboard's artwork and devlog tabs are cleared by `ClearSelection` itself, which also fixes the game *deletion* path that left a deleted game's pictures on screen; Explore's query is emptied under a suppression flag, because both of its setters mean "somebody changed the query" and one of them would start a request with nobody signed in; and a download in flight on the game page is cancelled, because it is running with credentials that no longer exist. | Resetting on every `SessionChanged` (empties the library on a token rotation — the trap this is shaped around); a fix on `DeveloperViewModel` alone (the page it was noticed on, leaving four pages with the same bug); rebuilding the pages from the DI container on sign-in (throws away event subscriptions the shell made in its constructor, for state a method can clear); a virtual no-op `Reset` on `ViewModelBase` (a new page inherits the wrong answer silently, which is exactly what the convention test is there to catch); clearing the machine's preferences too (a sign-out that forgets where games install, for a privacy problem that does not exist between two people at one computer) |
| D71 | **A build under an unpublished version is not a candidate for anybody, its publisher included — which supersedes half of D69 and half of D61 as of 2026-08-18** | `BuildFor` filtered on status and platform and knew nothing about the version a build belongs to, which was invisible because the server hides an unpublished version from everybody *except* its publisher (the backend's D62 seen from this side). So the one account it could bite was the one that owns the game: their own untested build was the newest thing in the document, the library card decided an update was pending, and Play disappeared from a game they could play — permanently, since the way to make it come back was to publish. Found by looking at the window, which is the only place it shows: every test fixture happened to describe a published version. The rule is now one rule rather than two, because `BuildFor` answers both *what to install* and *what would replace what is installed*, and splitting them would offer an install whose completion the update check then reads as another update pending — `HasUpdate` compares build ids for inequality, not recency, so installing the draft would flag an 'update' back to the released build. A publisher who wants to try a build therefore publishes its version, which is the same gate every other account is already behind. A build whose version is missing from the listing counts as unpublished, deliberately the same direction the server's `versionPublished` defaults in: a document that forgot to carry a version withholds a build rather than offering one nobody may download. | Filtering only in the update check and not in the install path (two rules for one question, and an install that immediately reads as needing an update); filtering server-side for the publisher too (that would take away the dashboard's whole reason to serve a draft — D66); leaving it (a publisher whose own game is unplayable from the library, which is what the maintainer's machine showed) |
| D72 | **The launcher asks whether the server can send anything, and the way back in without it is a screen the shell will not let go of** | Three pieces of one answer to the deployment that sets `MAIL_TRANSPORT=none`. **`mail.enabled` off `/capabilities`** decides whether "forgotten your password?" is offered at all — `ReportMailFailure` already read the 404 as "this server sends no mail", which is the right sentence *after* a press nobody should have been invited to make; a sentence naming the administrator to ask replaces the button. Its fallback is **true**, the opposite of `crashReports.enabled`, and the asymmetry is the decision: that one is permission to send something about the user, so silence means no, while this one is a feature somebody needs — a server too old to carry the key does send mail, and reading its silence as "no mail" would hide the way back into an account on every deployment that predates the field. Guessing wrong here costs one refusal the screen already explains. **`ChangePasswordViewModel` is one page for both cases**, forced and ordinary, because the flow is identical and the only thing the forced case adds is that there is no way out — `CanCancel` is `!IsForced`, and a "later" button would lead to a launcher where every request answers 403 and nothing says why. The password rules are deliberately **not** copied into it: the server owns them and names the rule it refused (D64), so the only local check is that the two new passwords match, which is the one mistake no server can see because only one of them is sent. And **the shell routes on the session, not on a refusal**: `passwordChangeRequired` rides on the session document, so `AfterSignInAsync` — one method both the start-up restore and the sign-in button go through — sends somebody to that screen instead of to a library whose first request comes back 403. The tabs go with it (`CanNavigate`), because a tab that only ever produces a refusal is the dead end D61 removes from buttons. | Inferring the deployment from the 404 (the offer is made, then fails, on the screen somebody is already stuck on); a `mail.enabled` fallback of false (hides the reset link on every server older than the field); a separate page for the forced and the ordinary change (one flow, two implementations, and the forced one is the untested one); copying the password policy into the page (a second definition of a rule the server states, which D64 exists to end); reacting to the first 403 instead of reading the session (a page fails, and then the launcher explains itself); leaving the tabs visible (three buttons that answer 403) |
| D73 | **The shell applies a change of session on the UI thread, because the event does not arrive on one** | Found by signing in through the real window, and it is the whole feature D70 shipped: `AuthenticationService.SignInAsync` awaits its token store with `ConfigureAwait(false)`, so `Publish` — and therefore `SessionChanged`, and therefore `ResetForAccountChange` on every page — runs on the thread pool. `LoginViewModel.Email = ""` re-evaluates a bound command, `Button.get_Command()` calls `VerifyAccess`, and Avalonia throws "the calling thread cannot access this object" on a pool thread — which is not an error message, it is the process ending. **Every sign-in closed the launcher**, from the moment D70 landed until this was found, because the session on this machine was already restored and no session since had typed a password. The fix is one `OnUiThread` around the handler's body rather than one at each of the things it touches: the marshalling belongs where the thread changes, and a rule applied at the callees is a rule the next callee forgets. The general lesson is in §7: anything reached from an event a *service* raises is running wherever that service happened to be. | Marshalling inside each page's `ResetForAccountChange` (six places to remember, and the shell's own property writes still unprotected); `ConfigureAwait(true)` in `AuthenticationService` (Core deciding it runs under a UI framework, which is exactly what the layering forbids); catching it (the launcher would survive with pages half reset) |
| D74 | **A server that says nothing about video is a server with none, which is the opposite reading from `mail.enabled` — and the size limit is one the client must enforce itself** | `media.maxVideoBytes`, `maxVideosPerGame` and `videoContentTypes` are three new keys on `/capabilities`, and `SupportsVideo` demands **all three**: half a description is a server describing itself incompletely, and the safe reading of that is the same as silence. The fallback is *no video*, and the asymmetry with D72's `mail.enabled` is the whole decision. Mail is a feature every server older than its key still had, so reading silence as "no" there would hide the way back into an account; **video did not exist before these keys existed**, so reading silence as "yes" here would offer an upload that cannot succeed and a kind the server would refuse. The *conservative* answer is not a constant direction — it is whichever way makes an old server behave the way it actually behaves. And the size check is the one client-side rule in this repository that is not merely an optimisation: an oversized picture comes back as a 422 naming its limit, but an oversized **video** is refused by the server's web framework before any handler runs, as a bare 413 with **no RFC 7807 body at all** (measured against the running stack). If `MediaUploadRules` does not catch it, nothing downstream has anything to say. | A single `maxBytes` for both (a trailer refused at the picture limit, or a picture limit large enough to be a video one); `SupportsVideo` from any one key (a server that carries a limit and no format list would be believed); a fallback of "video works" (every server predating the keys offers an upload that 422s or 413s); relying on the server's refusal for size (the one refusal that carries no message); a constant in the client for the limit (D39, and this time the failure is unexplainable rather than merely unexplained) |
| D75 | **Playback is `IVideoPlayback`, one player for the launcher, and "this machine cannot play" is an ordinary answer rather than an error** | LibVLCSharp plus `VideoLAN.LibVLC.Windows` is the **first native dependency in this repository**, and two facts about it shape the design more than the API does. It is **~100 MB per RID** — measured, 102 MB for `win-x64` — which is the cost the maintainer accepted on 2026-08-17. And **there is no Linux package**: VideoLAN publishes native packages for Windows, Android, iOS, macOS and UWP and none for Linux, where libvlc comes from the distribution — so on one of this project's two supported platforms playback depends on whether VLC is installed. That is why `IsAvailable` exists, why it is *lazy* (a session that never opens a trailer never loads the library, and `ShowVideoUnavailable` short-circuits on `HasVideos` so a page without one never asks), and why every layer above treats false as a sentence rather than a failure — the same reasoning D29 applies to an offline library. `Player` is typed `object` because nothing above the view has business naming a LibVLCSharp type and a substitute has nothing to hand back otherwise. The interface is a **state machine and nothing else**, because playback is the one surface whose behaviour no view-model test reaches: what is tested is that a machine that cannot play is never asked, that a refusal produces a sentence, and that every way of leaving the page — Stop, Back, loading another game, losing the account — stops the sound. The picture itself is checked by hand, and was: an MP4 and a WebM played from the real server in the real window on 2026-08-18. | `Process.Start` on the URL (rejected by the maintainer on 2026-08-17: the system player is a different application, and the launcher would be handing a URL to whatever is registered); a player per page (two native surfaces, and a trailer still playing behind another one); exposing `MediaPlayer` in the view-model layer (a native type above the view, and untestable); eager initialisation (~100 MB loaded at start-up for every session, most of which never open a trailer); treating an unavailable library as an error (every Linux machine without VLC would report a broken launcher) |
| D76 | **The native surface is created only while something is playing, and `IsVisible` is not what does it** | `NativeControlHost` creates its child window when it is **attached to the visual tree**, and `IsVisible="False"` does not detach anything — so a `VideoView` sitting hidden on the game page created a native window on every visit, and on a manifest with no `supportedOS` list that is `InvalidOperationException: Unable to create child window for native control host` on the layout pass, which is **the launcher closing** rather than a missing picture. Two changes, and both are worth keeping. The manifest gained its compatibility block, which is the actual fix. And the view holds a `ContentControl` whose `Content` is `VideoPlayer` — null until `IsPlayingVideo` — with a `DataTemplate` that turns a player into a `VideoView`, so on a page nobody has pressed play on there is no native window, no libvlc, and nothing for a machine that cannot host one to fail at. Found by opening the window, which is the third bug in this project found that way and none of which a green suite could see: a headless view test constructs the control without ever attaching it to a real window. | `IsVisible` on the `VideoView` (what crashed); creating the player eagerly and hiding it (a native window per game page, on every platform, for a page that may have no video at all); fixing only the manifest (the crash goes, but every game page still pays for a native surface it is not using); a converter (the same lifetime problem with the decision hidden in XAML) |
| D77 | **One shared answer to "is the server there?", and no request is made against a server that was missing a moment ago** | D29 said an unreachable server keeps the session and the library falls back to disk, and that decision was right and unimplementable as written, because **nothing wrote the answer down**. Every caller discovered the dead server for itself and discovered it again on the next request: with the backend stopped, the maintainer's start-up spent 23 seconds on a token rotation that could not succeed — showing the *sign-in screen* the whole time, with a valid session on disk — then 23 more on `GET /library`, and one per cover and per update check after that. `IServerReachability` is that answer, as pure state over a `TimeProvider` in Core: a failure holds the circuit shut for 20 seconds, one request is then allowed through to find out, and its outcome decides the next window. It is reported by a `ReachabilityHandler` on **every API client** — the same reasoning as `BearerTokenHandler` (D14), so a resource client added tomorrow cannot forget it — and read in two places: `AuthenticationService.RotateAsync`, which refuses to spend a round trip on a rotation that cannot work, and the UI, which now has something true to say. Two properties rather than one, because they answer different questions: `IsOnline` is what the banner shows and stays false until something actually succeeds, while `AllowsRequests` half-opens on its own. `SignInAsync` and both Retry buttons call `RetryNow()`: the circuit exists to stop the launcher retrying by itself, never to answer for somebody who has just pressed a button. | Leaving each caller to find out (the bug: one connection timeout per request, forever); a shorter or longer window (a page of a dozen cards costs one failed attempt at 20s and a launcher left open recovers by itself; a minute would strand somebody whose network came back); one property for both questions (either the banner flickers off during the retry probe, or the probe never happens); polling the server on a timer (a request per interval on a machine that may be on a train, to learn something the next real request learns for free); putting the check in `ApiTransport` (it is constructed per client with `new`, so the dependency would thread through nine constructors) |
| D78 | **The deadline that catches a hung server applies only while the server is unproven — and the offline library is the account's, not the disk's** | Two halves of making D77 true against the failure people actually have. **A stopped backend is rarely a refused connection**: behind Docker's port forwarder, nginx, a load balancer or a captive portal it *accepts* in milliseconds and then says nothing — measured here as 0.2s to connect and 21s of silence — so `SocketsHttpHandler.ConnectTimeout` never fires and what a person watches is the client's own 30 seconds. `ReachabilityHandler` therefore gives a request 8 seconds to be answered, and gives up on the server rather than on the request. It applies **only while `IsProven` is false** — the first request of a run, and any after a failure — because once a server has answered, its slow routes deserve their time: the download plan diffs two manifests server-side, and refusing that on a deadline would break a working launcher in order to fix a broken one. A body over 1 MiB is exempt for the same reason, since there the wait is the upload. The second half is `ILibraryCache`: the last successful `GET /library`, one JSON document per account under the user's data directory, read when the server cannot be. Falling back to the install rows alone — which is all D29 ever did — showed an **empty library** to anybody who had not downloaded a game yet, and silently dropped every title an account owns and has not installed here, which for most people is most of them. Installs the stored answer does not mention are appended, so a game installed since the last successful load is still playable. | `ConnectTimeout` alone (does not fire against the failure that actually happens); a shorter client timeout for every API call (a 4 MiB upload chunk on a slow link refused as a network failure); the deadline on every request forever (the download plan, refused at 8 seconds, on a server that is working); the install rows as the offline library (the bug: an empty page for a new account, and owned games hidden); a table in the install database (a migration and a schema, for a copy of something the server can send again); naming the file after the account id (a directory listing of everybody who ever signed in on a shared machine) |
| D79 | **The sign-in screen offers a way in without signing in, and that visit is a state the shell holds rather than a session it invents** | D77 and D78 fixed the launcher for somebody who is already signed in; somebody who is not was still stopped at a form that cannot succeed, with games sitting on their disk. The offer is on the sign-in screen because that is where they are, and it is guarded by **both** halves of the only case where it means anything: the server is unreachable *and* this machine has a game on it. A door to an empty library is an invitation to look at nothing, and the offer has no business existing while the server answers, because then signing in works and is what somebody wants. What it produces is **not a session**: `MainWindowViewModel.IsOfflineGuest` is a shell state, `IsSignedIn` stays false, and so every account surface — Explore, publishing, settings that talk to a server — stays hidden, exactly as `CanNavigate` already hid them. The library it opens is the *installed* list and never the stored one, because a cached library belongs to an account and handing the last person's list to whoever opens the launcher next is not something an unreachable server excuses. Two smaller rules fall out of it: the header grows a **Sign in** button, because there is no session to sign out of and otherwise the visit has no exit; and the library card's **Details** button disappears while offline, since the game page is built from the catalog and — with nobody signed in — the error it produced was *"your session has expired"*, which is a lie about a session that never existed. | A fake or anonymous session (every `IsSignedIn` check in the shell silently becomes wrong, and the first request would carry no token anyway); offering it whatever the server is doing (a way to skip signing in, which is not what this is for); offering it with nothing installed (a button to an empty page); showing the cached library to a signed-out visitor (the previous account's game list, to whoever opens the launcher); leaving Details in place (a page that can only fail, with the wrong reason); a second, cut-down library page for the visit (two implementations of the page whose whole point is that it already works offline) |
| D80 | **The API's address is asked for at start-up and cached, the answer is signed with the curve the release check already uses, and the registry's URL is configuration while its key is code** | A launcher ships with its backend's address baked into `launcher.config.json`, so moving the backend means cutting a release for a string — and every copy already installed is broken until somebody installs it. The registry ends that, and each half of how is forced. **The scheme is ECDSA P-256/SHA-256** and not the Ed25519 the service was first built with: D55 already refused a managed Ed25519 or a native binding across four RIDs, `ReleaseSignature` is written and tested, and reusing it means one signature algorithm in this client rather than two. The service was changed to match, which cost an afternoon and no dependency. **The URL is configuration and the key is code**, which looks inconsistent and is not: the key is what *authorizes* an answer, so it cannot live in the file a self-update overwrites (D54's reasoning, unchanged), while pointing a launcher at a hostile registry gains an attacker nothing — the answer will not verify. A URL with no key asks nothing at all, so an unmodified build of this repository is exactly as it was. **The cache holds the signed envelope, not the address**, and reading re-verifies it: that file is writable by anything running as this user, and a cache trusted on sight would be the way around the signature rather than a use of it — driven for real, a tampered file is discarded and the registry asked again. A refresh will not replace a stored claim with an **older** one, because a genuine answer from before the address moved is a replay. And **the start-up path prefers speed over freshness**: a stored claim is used as it stands with no round trip, only a machine with nothing cached waits (3 seconds, once ever), and the refresh runs behind the window — so a moved backend is picked up at the *next* start. That last one is a real cost, stated rather than hidden: the first launch after a move fails to reach the server. The alternative puts a network round trip in front of every launch to fix the rarest case, which is precisely the trade D77 and D78 spent a session taking out of everything else. | Ed25519 with BouncyCastle (a crypto dependency and a second signature scheme, both refused once already); the key in `launcher.config.json` (an update rewrites the thing that authorizes updates); the whole thing in code (a fork recompiles to point at its own registry); caching the resolved URL (a text file that redirects the launcher, with the signature bypassed); asking the registry before the window on every start (a round trip in front of every launch, for the case a restart covers); re-binding every typed client at runtime (rebuilding the HTTP graph mid-session, to save one restart) |
| D81 | **A fork's branding is applied where Avalonia already is, and every way it can fail means "no logo"** | `BrandingConfiguration` had shipped since milestone 1, documented and deserialized and read by *nothing*, which is D33's complaint about `InstallDirectory` word for word — a setting that does nothing is worse than an absent one, and DISTRIBUTING.md §3.3 was promising it worked. Three answers were forced. **Resolution goes through `PathSafety.ResolveInside`** rather than a second containment check, so there is one implementation of "inside this directory" in the client (D58's reasoning about the updater's path rules); its refusal is a throw because it was written for paths off the network, and here it becomes a **null**, because these strings come out of a file somebody edited by hand and a typo must cost a fork its logo and never its program — the same trade D54 makes for an unrecognised channel. Two mistakes it catches are worth naming: an absolute path, which `Path.Combine` resolves to itself and is the likeliest way to write one of these wrong, and a `..` that climbs out. **Loading is in the composition root**, because a view model holding a `Bitmap` is a view model that stops being testable without a toolkit (D37), and the shell exposes it as `object` for the reason `IVideoPlayback.Player` is one. And **the executable's icon is a different thing entirely** — a compiled-in Win32 resource, `.ico` only, present at build time — so `<ApplicationIcon>` is conditional on `assets/icon.ico` and a clone without one builds as before. | Reading the config in a view model (untestable, and Core learning about images); a converter in XAML (the decision lands where nothing can press it); throwing on a bad path (a launcher that will not open because a logo was misspelled); one setting for both icons (a PNG cannot be a Win32 icon resource, and an `.ico` in the window is a worse picture) |

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
answering *before* the window opens — the client works offline by design (D29, D77), so a
stopped backend produces a library restored from disk, or a sign-in screen saying the server
cannot be reached, rather than an error — and
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

Self-contained publish (per RID: `win-x64`, `linux-x64` — see D59):

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

### Running the suite on Linux, without a Linux machine

CI is the only place this suite meets Linux, and §9 says as much — which in practice meant
finding out at a red `main`, twice in one afternoon: a swap-path test that had only ever passed
on Windows, and a branding path written with backslashes, which on Linux names one file that
does not exist rather than a file inside a directory. Docker closes that, and the three tests
that skip themselves here **run** there:

```bash
MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 bash -c "dotnet test GameLauncher.sln --nologo"
```

`MSYS_NO_PATHCONV=1` is not optional: without it Git Bash rewrites `-w /src` as
`C:/Program Files/Git/src` and Docker refuses the working directory. The first run pulls the
SDK image; later ones are quick. **1152 assertions there against 1149 here**, and the three are
the Unix file-mode ones.

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
| **macOS is not a target any more** (D59, 2026-08-07) | It used to be, verified only by a green CI leg — which proved a runner compiled it and nothing else, since no macOS machine exists here. The `osx-*` RIDs and the `macos-latest` leg are gone from the workflow. The branches in `PathProvider` and `RuntimePlatform` are deliberately left, so a fork that wants it back starts from something rather than nothing; nothing in this repository claims they work |
| **`Environment.ProcessPath` belongs on `IPathProvider`, not at the use site** | The self-update needs to know what to restart, and a caller reading it directly is a caller no test can point somewhere else — the same reason every other path is on that interface. `IPathProvider.ExecutablePath` exists for it. A test that substitutes the provider and forgets this property gets an empty string, and the failure shows up as a launcher name that does not match anything in the archive |
| **A constant array literal passed to a method fails the build** | `Assert.Equal(new[] { "12" }, actual)` is CA1861 — "prefer static readonly fields over constant array arguments" — and with `TreatWarningsAsErrors` (D9) that is an error, in a *test*, for an assertion that reads perfectly well. It is not a rule worth arguing with: `Assert.Equal("12", Assert.Single(actual))` says the same thing and is a better assertion, because it names the count as part of what is being checked |
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
| **The Fluent theme paints a button's templated presenter on hover and on press, not only when disabled** | The `Button.link:disabled` row below found half of this; a *cover* used as a button found the other half. A `Background="Transparent"` setter on the button itself is ignored for those states, so a picture used as a control gets a grey wash over it on hover. `Button.bare /template/ ContentPresenter#PART_ContentPresenter` is the fix, and application styles win over the theme's because they are applied after it — which is also why no `:pointerover` variant of the selector is needed |
| **A `Task<T>`-returning member of an unconfigured substitute yields `default(T)` — and `IUserSettingsStore.LoadAsync` is now on the install path** | Recorded again in a new shape, because it is the row that keeps costing cycles. `GameDetailViewModel` reads the preferences before every install, so every test class that builds it has to arrange `LoadAsync` or the view model dies on a null `UserSettings` inside a command — a crash rather than a failed assertion. Arranged in the **test class constructor**, which runs before the body, so a test wanting other settings arranges over the top |
| **`IFolderPicker.PickAsync` takes an optional `CancellationToken`, and xUnit1051 is an error here** | `_folders.PickAsync(Arg.Any<string>())` compiles, reads perfectly, and fails the build under `TreatWarningsAsErrors` with an analyzer message about test cancellation — on an NSubstitute *arrangement*, where a token means nothing. Pass `Arg.Any<CancellationToken>()` in stubs and `Received()` calls of anything whose real signature takes one |
| **`Graphics.CopyFromScreen` photographs whatever is on top of that rectangle** | It captured an unrelated window sitting over the launcher, so the screenshot showed something else entirely. `PrintWindow($hwnd, $hdc, 2)` renders the target window itself, works when it is occluded, and does **not** steal focus from whatever the maintainer is doing. Also: `Add-Type -MemberDefinition` already emits `using System.Runtime.InteropServices;`, so passing `-UsingNamespace` for it fails the compile as a warning-as-error |
| **A `Button` is aligned `Left` by the Fluent theme, so `HorizontalContentAlignment="Stretch"` on it stretches nothing** | The two content alignments on `Button.bare` were there and looked sufficient; the button itself was still only as wide as what it held. A cover frame with a picture *looks* right anyway, because `UniformToFill` asks for the full width — so the bug only appeared on the placeholder, as a 44px sliver of a 300px card, and on the devlog card whose whole header was supposed to be pressable and was clickable only across the text. Measured rather than reasoned about: a scratch project with `Avalonia.Headless` printed `button 44x150 ha=Left` as shipped and `268x150` with `HorizontalAlignment="Stretch"` added. That is the cheapest way to settle any layout question here, and it takes two minutes |
| **`{loc:Tr}` is a `Binding`, so it cannot be a `StringFormat` — and the failure takes the whole page** | `Text="{Binding X, StringFormat={loc:Tr Key}}"` compiles, passes `dotnet format`, and throws `InvalidCastException: Unable to cast object of type 'Avalonia.Data.Binding' to type 'System.String'` when the view is **constructed**. The shell's `ContentControl` swallows it and draws an empty rectangle, so the Settings page was blank for a day and was reported as a missing feature rather than a crash. Compose the sentence in the view model. `tests/GameLauncher.Views.Tests` now catches the shape (D68) |
| **A backing field of `[ObservableProperty]` cannot be written directly — the toolkit's analyzer makes it an error** | MVVMTK0034, and with `TreatWarningsAsErrors` (D9) that is a failed build, not a hint. It comes up whenever a reset has to change a property *without* running the side effect its setter exists for — Explore's `SearchText` arms the debounce, `SelectedSort` reloads at once. The idiom that works is the dashboard's: a `_suppressReload` flag held across the assignment and checked in the `partial void On…Changed`, which also keeps the reason on the page instead of hiding it in a field write |
| **A page that keeps state is a page that keeps the previous account's state** | The shell owns every page for the lifetime of the window, so signing out and back in as somebody else left the dashboard showing the first account's game (D70). Anything added to a page after this — a list, a form, a selection — has to answer `ResetForAccountChange`, and the reflection test in `MainWindowViewModelTests` only catches a whole *page* that was forgotten, never a field |
| **An event raised by a Core service runs wherever that service happened to be — and touching a bound command there ends the process** | `AuthenticationService` awaits its token store with `ConfigureAwait(false)`, so `SessionChanged` fires on the thread pool. The shell's handler reset every page, `LoginViewModel.Email = ""` re-evaluated a bound command, and `Button.get_Command()` threw `InvalidOperationException: the calling thread cannot access this object` — on a pool thread, so there is no error banner and no log line the user sees: the launcher **closes**. Every sign-in did this from D70 until 2026-08-18, and nothing in the suite could see it, because a test has no `SynchronizationContext` and `OnUiThread` therefore runs inline. The rule: anything reached from a *service's* event marshals through `OnUiThread` before it touches a bound property (D73). A test can pin it by installing a recording `SynchronizationContext` **before constructing** the view model — `ViewModelBase` captures it in a field initialiser — and then raising the event with none current |
| **A `NativeControlHost` attaches when it enters the visual tree, and `IsVisible="False"` does not keep it out — and without a `supportedOS` list in the manifest that is a crash** | The first `VideoView` on the game page closed the launcher on every visit to any game, playing or not: `InvalidOperationException: Unable to create child window for native control host. Application manifest with supported OS list might be required.`, thrown inside a layout pass where nothing catches it. `app.manifest` now carries the `compatibility` block (Windows 7 through 11), which is the fix; and the view creates the surface only while something plays, through a `ContentControl` bound to a null-until-playing property (D76). **A headless view test cannot see this**: `GameLauncher.Views.Tests` constructs the control and never attaches it to a real window, so all eight views passed against a launcher that could not open a game page |
| **There is no `VideoLAN.LibVLC.Linux` package, and the Windows one is 102 MB per RID** | Both measured on 2026-08-18 rather than assumed. VideoLAN publishes native packages for Windows, Android, iOS, macOS and UWP — Linux is expected to get libvlc from its distribution, so `PackageReference` is conditioned on the RID and a Linux launcher plays video only where VLC is installed. The size is the maintainer's accepted ~100 MB: `win-x64` is 102 MB, of which 98 MB is `plugins\`. A `dotnet build` on Windows copies **all three** Windows RIDs (283 MB in `bin\Debug`); a self-contained publish for one RID carries one |
| **`LibVLCSharp.Avalonia` 3.10.1 declares `Avalonia [11.3.13, )` and does work under Avalonia 12.1.1** | Worth recording because the open-ended range is not evidence of anything — a package compiled against 11 can still break on 12. Measured with a throwaway project before any of this was built on: headless Avalonia 12.1.1 starts, `new VideoView()` constructs, `Core.Initialize()` loads libvlc 3.0.23, and a `MediaPlayer` attaches. That two-minute project is the same technique §7 already recommends for layout questions |
| **`Core.Initialize()` does not compile in this repository: `Core` resolves to the `GameLauncher.Core` namespace** | `LibVLCSharp.Shared.Core.Initialize()` in full, or the compiler reports a namespace that "does not exist" in `GameLauncher.Core` — which reads like a missing project reference rather than a name collision |
| **"The server is offline" usually means a proxy accepting the connection and saying nothing** | Measured against this machine's stopped stack: `curl` connected to `localhost:8080` in **0.2s** — Docker Desktop's forwarder is still listening with the container down — and then waited **21 seconds** for a reply that never came. Every design that bounds a *connection* (`ConnectTimeout`, a reachability ping) is useless against it, and `HttpClient.Timeout` is what a person ends up watching. Bound the answer, not the connection (D78) |
| **A hung backend makes a green suite and a broken launcher look identical** | The offline paths were all present and correct — D29, the install-row fallback, the `ApiErrorCode.Network` catch — and the launcher was still unusable with the server down, because each of them waited 20+ seconds to be reached. Nothing in 1032 tests could see it: a substitute fails instantly. The question a test cannot ask is *how long the wrong answer takes to arrive* |
| **Killing the launcher by name while somebody is using the machine loses whichever session was last written** | `session.json` is rewritten on every rotation, so `Stop-Process -Name GameLauncher` plus moving that file aside is a race with whatever the maintainer is doing in their own window. Copy it, do the run, and put the copy back — and check *whose* session came back afterwards, because it may not be the one that was set aside |
| **A launcher started from a stale `bin/Debug` looks exactly like a bug in the code you just wrote** | Half an hour went into "the shell ignores `MustChangePassword`" when the window was running the previous build: `Start-Process` on the exe does not build anything, and the symptoms were a perfectly plausible ordering bug. Build immediately before starting, in the same command, and if a window disagrees with a passing test check `LastWriteTime` on `GameLauncher.dll` before believing the window |
| **`ecdsa.VerifyASN1` in Go takes `(pub, hash, sig)` — the signature is the *last* argument** | Passing `(pub, sig, hash)` compiles, because both are `[]byte`, and every signature fails to verify. On the registry side that showed up as a whole test file going red at once with "signature does not match payload", which reads like a broken signing step rather than two swapped arguments. Worth remembering because .NET's `VerifyData(data, signature, …)` puts them the other way round, and this project now writes both |
| **A P-256 signature is *not* deterministic, so an ETag derived from one never matches** | ECDSA draws a fresh nonce per signature, so two responses with identical content carry different signatures. The registry's entity tag was derived from the signature while it was signing with Ed25519 — which is deterministic — and switching curve silently turned it into a cache that never hits. It hashes the payload now. Anything derived from a signature is worth checking against this |
| **Docker Desktop's port forwarder holds a stale network after a failed `compose up`** | The backend's `api` container came up unable to resolve `db` — `Temporary failure in name resolution` on every migration attempt, retrying forever — after one `up` had failed on a port collision. Nothing is wrong with the compose file; `docker compose down` followed by `up` recreates the network and it works. Also: the service registry publishes 8080 and 9090 by default, which are the backend's API and admin ports, so running both stacks at once needs `HOST_PUBLIC_PORT` / `HOST_ADMIN_PORT` in the registry's `.env` |
| **`ValuePattern.SetValue` leaves whatever was already in the box if the launcher put it there** | An address arrived as `aawlocked-out@example.test` — the field was not empty when the script set it. Read the value back through the same pattern after setting it, or set `""` first; asserting on a sign-in that silently used the wrong address reads as a server refusing correct credentials |
| **A `dotnet test` on the solution prints one summary line per project, and a project that failed to *compile* prints none** | Filtering the output with `grep -E "Superato!|Passed!"` therefore shows a green suite while two of the five projects never ran — which is how a commit that did not build was pushed to `dev`. Count the summary lines: five, or something is wrong. The compile error itself is one line further up, filtered out by the same grep |
| **A backslash written into a file by a tool can arrive as a control character** | `\a` became 0x07 inside `GameLauncher.App.csproj`, producing `..\..ssets\**` — a glob that silently matches nothing, in a file nothing validates. The row about invisible control characters already covers the idea; this is the shape it takes when a *path* is being written. Build the separator from `chr(92)`, and check the result with `cat -A` rather than reading it back |
| **`MSYS_NO_PATHCONV=1` is what makes Git Bash stop rewriting container paths** | `docker run -w /src` becomes `C:/Program Files/Git/src` and Docker refuses it; `docker compose exec api /app/launcher-api` becomes a Windows path inside the container and "no such file or directory". Both read like the container being broken. PowerShell does not do this |
| **A public repository and a distribution branch want *opposite* assertions about the same constant** | Two tests assert `LauncherReleaseKey.PublicKeyBase64` is **empty**, which is what keeps a real key out of this repository by accident. On a branch whose whole purpose is to carry one they fail, and inverting them there is not a workaround: a launcher built with no key checks for no updates at all and looks perfectly healthy while doing it, so "the key is present and is this one" is the invariant worth having on that side |
| **`chmod +x` does not stick on an ELF binary under MSYS** | It works on `install.sh`, because MSYS decides executability by looking at the file and a `#!` line is something it recognises; a Linux binary is not. So a tarball built on Windows carries `-rw-r--r--` on the launcher, which looks like a broken release in `tar tvzf` and is not: `install.sh` and the updater both force the bit. `scripts/package-linux.sh` says so now |

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
5a. `GameLauncher.Views.Tests` is the **only** project with a running Avalonia (headless), and
   it asks one question per page: can this view be constructed at all (D68). Keep it that way —
   view-model tests that need a UI thread are view-model tests that stop being written.
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
- **CI runs on `main`**, not on `dev`, across `windows-latest` and `ubuntu-latest` (D59). Since
  `main` is merged by hand by the repository owner, no run is triggered by anything this
  repository's work does — which makes the local check the real gate.

### The gate before a push is local, and it is not optional

Nothing on GitHub will catch a red suite on `dev` any more, so **both of these have to pass
before `git push`**, every time:

```bash
dotnet test GameLauncher.sln
dotnet format GameLauncher.sln --verify-no-changes
```

A push made without running them is a push made on hope. What they cannot cover is the **Linux
leg**: the suite runs here on Windows only, and three tests skip themselves where there is no
Unix file mode to look at. A change touching platform-specific code or the publish configuration
is worth saying out loud as unverified on Linux rather than quietly assuming.

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

### A pass over the maintainer's own notes — verified on 2026-08-17

The maintainer used the launcher end to end and wrote down eighteen things (`ClaudeContent/appunti.txt`,
outside version control). Nine are done here; the rest need the backend and are listed under
**Next up**. Delivered, grouped as they were built rather than as they were numbered:

- ✅ **A clean start** — `TextBox.Watermark` is obsolete in Avalonia 12 and produced twelve
  `AVLN5001` warnings on every run of `dev.ps1`. `PlaceholderText` everywhere; the build is
  silent again.
- ✅ **The sign-in form shows the password on request** — `TextBox.RevealPassword` behind a
  checkbox, on the one screen where somebody is typing a password they cannot see and has no
  other way to check it.
- ✅ **The cover is the way into the game** — in Explore and in the library. `Button.bare` is a
  button that paints nothing at all, which needed the template-reaching selector the disabled
  link already needed; the automation name is the title, because a button whose content is a
  picture is otherwise announced as the panel it is made of (§7).
- ✅ **Play, Remove and the install folder** (D61, D63) — Play is gone while an update is
  pending and a sentence says so; Remove-from-library is gone while the game is installed, on
  the game page and on the library card; installing adds the game to the library; and
  "Open folder" opens the install directory through the new `IFileBrowser`.
- ✅ **"Installed: 0.2.0" is "Installed version: 0.2.0"** — in all three languages.
- ✅ **Where a game is installed** (D62) — `UserSettings.AskWhereToInstall`, off by default,
  and `InstallRequest.InstallRoot`, which is a root and not a directory for a reason the row
  spells out.
- ✅ **The devlog is a list of cards that open, and its Markdown is rendered** (D60) — the
  newest one open, one line of prose under the rest. `MarkdownParser` is in Core with 22 tests;
  `MarkdownPresenter` is the Avalonia half and holds no rules.

869/869 green (318 Core, 286 Infrastructure, 254 App, 11 Updater — 3 skipped on Windows as
always), `dotnet format --verify-no-changes` clean. **Not verified by looking at the window**:
the devlog cards and the Markdown need a server with a published devlog, and this pass was
driven from the suite. That is the half worth checking first next session.

### Validation messages that say what to do — 2026-08-17

The first of the nine notes that needed the server (D64 here, D60 there). A weak password at
registration read *"Alcuni dei dati inseriti non sono stati accettati (riferimento 7fd59c6d…)"*;
it now reads *"La password deve contenere almeno 8 caratteri."* — the number comes from
`ruleArgs`, so it followed the server down when the minimum was lowered later the same day.

- ✅ `ApiProblem.Rule` / `ApiProblem.RuleArgs` and their pair on `ApiException`. The server sends
  a frozen rule name and the values its sentence needs — the limit, almost always
- ✅ `ApiErrorPresenter` maps them to `Error.Validation.*`, 29 keys in all three languages, with
  two fallbacks that keep the contract additive: an unknown rule and a placeholder that arrived
  without its argument both give the generic sentence
- ✅ **A validation failure no longer shows the request id.** Every other failure still does
- ✅ 907/907 green (354 Core, 288 Infrastructure, 254 App, 11 Updater — 3 skipped on Windows),
  `dotnet format --verify-no-changes` clean

**Verified against the running server** with the real DI graph, because the suite has no server
and the point of the change is what crosses between one: **7 of 7**, each sentence printed in
English, Italian and French, plus the check that a non-validation failure keeps its reference.
The server-side gap that mattered was found there and not in the code — a *blank* password is
refused by the body reader before the validator ever runs, so it was the one 422 on that form
with no rule on it.

**Not exercised against a live server**: the publisher-side rules — title, summary, slug,
version, devlog, alt text. They are covered by tests on both sides and travel the same
`ApiTransport` path as the account fields, but nobody has watched one appear in the dashboard.

### Publishing a version afterwards, and naming a build — 2026-08-17

The maintainer's notes 13 and 18, the second group that needed the server (D65 here, D61 there).
The answer to note 13 turned out to be that the button *could not* exist: there was no route.

- ✅ **Publish / Withdraw on each version row.** A version created without "publish it now" is
  no longer a dead end. Publishing twice is safe — the server keeps the original date — and
  withdrawing is offered as the reversible thing next to Delete
- ✅ **`VersionRowViewModel`** (D65), which is what lets a row say which builds are under it
- ✅ **A build can be named** when it is published, and the name is what the builds list leads
  with, because platform and architecture are what every row already says
- ✅ `Error.Validation.BuildNameTooLong`, the first rule added since D64's mechanism landed —
  which cost three resx entries and one switch arm, which is what "additive" was supposed to mean
- ✅ 918/918 green (355 Core, 290 Infrastructure, 259 App, 11 Updater — 3 skipped on Windows),
  `dotnet format --verify-no-changes` clean

**Verified against the running server** with the real DI graph signing in as a publisher —
**15 of 15**, including the line a version row renders:
`0.1.0 Release False — Nightly, demo levels · Linux X64`. The harness swaps `ITokenStore` for an
in-memory one so that signing in does not overwrite the session of the launcher installed on
this machine, which is worth knowing before writing the next one.

**Not verified by looking at the window**: the two new buttons on the version row and the build
name box. Every rule behind them is tested and the server half was driven for real, but nobody
has pressed them — and this repository's own history says that is where the bugs tests cannot
see have twice been found. It is the first thing to check next session, together with the devlog
cards from the previous pass.

### Previews for the publisher — 2026-08-17

The maintainer's notes 8 and 9, and the first group of the remaining six that needed **nothing**
from the server: the routes were all there, and one of them was serving something nobody had
checked. D66 has the reasoning.

- ✅ **The artwork tab shows the pictures.** Both lists hold `MediaCardViewModel` — which is
  `ScreenshotViewModel` renamed, moved to its own file and given the one property the dashboard
  needs, rather than a second type meaning the same thing. The game page uses it unchanged
- ✅ **A button that opens the game's own page**, as a `GameSelected` event the shell listens to
  beside Explore's and the library's (D17). "Back" returns to the dashboard with no new code:
  `ShowDeveloperAsync` already recorded it as the list page
- ✅ **The draft question was answered before anything was built.** `CatalogService::gameDetail`
  serves a game to whoever may edit it whatever its visibility — read in the code *and* driven
  against the running server, where a draft's detail was 200 to its owner and 401 to nobody
- ✅ `Publish.OpenGamePage` in the three resx files
- ✅ 926/926 green (355 Core, 290 Infrastructure, 267 App, 11 Updater — 3 skipped on Windows),
  `dotnet format --verify-no-changes` clean

**Verified against the running stack** as far as a headless check reaches: a draft created
through the API, three real PNGs uploaded to it, and every URL the dashboard will hand
`IImageProvider` fetched back **through nginx with no token** — 200, `image/png`, byte counts
matching what went up — with the draft and its `coverUrl` appearing in `GET /me/games`, which is
the list the dashboard reads.

**Not verified by looking at the window**, and it cannot be from here: `Bitmap` needs an
initialised Avalonia (§7), so nothing short of opening the launcher shows that those bytes decode
and that the templates bind. The probe game is left on the development server with its three
pictures, so the dashboard has something to show the moment somebody opens it.

### A blank page, a sliver of a card, and a UUID nobody could use — 2026-08-17

Three of the maintainer's eight findings from testing the previous sessions' work, and the
third turned out to be a page that never rendered rather than a feature that was never built.

- ✅ **Cards without a cover are the size of cards with one.** `Button.bare` now sets
  `HorizontalAlignment="Stretch"`: the Fluent theme aligns a button `Left`, so the two content
  alignments already on that class had nothing to spread across and the frame took the width of
  the placeholder letter — 44px inside a 300px card. It fixes the library card and, unlooked
  for, the devlog card whose whole header the comment promised was pressable and whose button
  ended where the text did. **Measured** with a scratch `Avalonia.Headless` project rather than
  reasoned about, because the XAML gave no clue which of two candidates was at fault (§7)
- ✅ **No user-facing message carries the request id any more** (D67), and `ApiErrorPresenter`
  writes it to the launcher's log instead — the need behind the reference is an operator's and
  it is moved, not deleted. This supersedes half of D64; `Error.WithReference` is gone from the
  three resx files
- ✅ **The Settings page was blank**, and had been since the install-directory work: a
  `{loc:Tr}` used as a `StringFormat` threw while the view was being constructed, and the
  shell's `ContentControl` drew nothing. Both settings the maintainer asked for — the default
  install directory and "ask where to install every game" — existed, were documented (D62), and
  could not be seen. The sentence is composed in `SettingsViewModel` now
- ✅ **`tests/GameLauncher.Views.Tests`** (D68), one project with a headless Avalonia, seven
  views, seven assertions. It fails against the bug it was written for, checked by reverting it
- ✅ 936/936 green (357 Core, 290 Infrastructure, 271 App, 11 Updater, **7 Views** — 3 skipped
  on Windows), `dotnet format --verify-no-changes` clean

**Verified by looking at the window**, which is the only place any of this is visible. Every
cover button in Explore measures 268x150 whether or not a picture arrived, with the placeholder
letter centred; the devlog card headers measure 788 wide instead of ending at the title. A 404
driven against the running server shows *"That is not available."* with no reference, and the
launcher log carries `reference dc74f705-…` on the same second. The Settings page renders in
full, and "ask where to install every game" survived a restart — ticked, launcher closed,
reopened, still ticked, then put back as it was found.

### A library card that knows about updates, and pages that forget an account (2026-08-18)

The next two of the maintainer's eight findings. Both are client-side, both wanted a decision
written before any code, and one of them wanted the *server* proved rather than asserted.

- ✅ **Play is gone from a library card whose install is not the newest build** (D69, which
  supersedes half of D61). One request per *installed* game, issued after the list and the
  covers are on screen; the button disappears and `Detail.UpdateBeforePlaying` takes its place,
  exactly as on the game page; and `PlayAsync` refuses as well, because the check can land
  between the press and the click. **Offline, and on a single refused check, Play stays** —
  D29's offline library is the rule that outranks it, and a test pins that
- ✅ **No page keeps the previous account's anything** (D70). `IAccountScopedPage` on all six
  pages, `MainWindowViewModel.Pages` as the list that gets reset, and the reset keyed on the
  **account id** rather than on `SessionChanged`, which also fires on every token rotation. A
  reflection test fails when a page property is missing from `Pages`. Two things fell out of
  it: `ClearSelection` now clears the dashboard's artwork and devlog tabs, which also fixes a
  *deleted* game leaving its pictures on screen, and an install in flight is cancelled when the
  credentials behind it stop existing
- ✅ **The server half of finding 7 was demonstrated, not asserted.** Two publishers were
  driven against the running stack and **sixteen write routes** of a game, a version, a build,
  the artwork and the devlog were tried from the account that owns none of them: all sixteen
  refused — 403 where the intruder can see the game, 404 for a build, whose existence is not
  confirmed (D26) — and the victim's game came out of it unchanged. Nine of those refusals had
  no test; they have one now, in the backend's §11 for 2026-08-18
- ✅ 949/949 green (357 Core, 290 Infrastructure, 284 App, 11 Updater, 7 Views — 3 skipped on
  Windows), `dotnet format --verify-no-changes` clean

**Not verified by looking at the window.** The launcher opens and draws its sign-in page, and
the saved session on this machine is dead — its refresh token is refused, so the library cannot
be reached without typing a password, which is not something this session did. D69 is the one
change here whose *appearance* no test reaches, so it is unverified in that sense and is worth
one minute with the window before it is trusted.

### Mail as an option, and a crash on every sign-in (2026-08-18)

The maintainer's note 14 — the fifth of the eight findings — plus two bugs the window found that
no test could. D72 and D73 have the reasoning; the server half is the backend's D63.

- ✅ **"Forgotten your password?" is gone where nothing can be sent**, and a sentence naming the
  administrator to ask takes its place. `mail.enabled` comes off `/capabilities`, read once per
  run on the sign-in screen and never able to fail it — the provider falls back rather than
  throwing (D39). The fallback is **true**, unlike `crashReports.enabled`, and the row says why
- ✅ **A screen that forces the change**, `ChangePasswordViewModel` + `Views/ChangePassword`,
  one page for the forced and the ordinary case. No cancel while forced, no copy of the server's
  password policy, and the one local check is that the two new passwords match — the mistake no
  server can see, because only one of them is sent
- ✅ **The shell routes on the session**, not on a refusal: `passwordChangeRequired` arrives on
  the session document, and `AfterSignInAsync` — the single method the start-up restore and the
  sign-in button both go through — sends somebody to that screen. `CanNavigate` hides the tabs
  while it is in force, because each of them would only produce a 403
- ✅ `IAccountApi.ChangePasswordAsync` answers with a **whole session**, and
  `IAuthenticationService.AdoptAsync` is the seam that takes it over — the route is on the
  authenticated client, so it cannot live on the session service at all (D47), and the composed
  `AccountService` adopts **only on success**, which is the opposite order from the erasure and
  for the opposite reason
- ✅ **Every sign-in was closing the launcher** (D73), from the moment D70 landed. The session
  change arrived on a thread-pool thread, resetting the pages touched a bound command, and
  Avalonia's thread check threw where nothing catches it. One `OnUiThread` in the shell; the
  test that pins it fails against the bug, checked by reverting the line
- ✅ **A publisher's own library card had lost Play for good** (D71): the server serves an owner
  their unpublished versions, and `BuildFor` knew nothing about which version a build belongs to.
  Found by opening the window to check D69, which is exactly what that check was for
- ✅ 10 new resource keys in English, Italian and French
- ✅ **980/980 green** (365 Core, 293 Infrastructure, 303 App, 11 Updater, 8 Views — 3 skipped on
  Windows), `dotnet format --verify-no-changes` clean

**Verified by looking at the window**, which is where two of the six items above came from. With
`MAIL_TRANSPORT=none`: the sign-in screen showed the sentence and no reset button. An operator
issued a one-time password; signing in with it landed on the change screen with **no tabs and no
cancel**; two different new passwords were refused locally with no request; re-entering the
temporary one was refused by the server and shown as *"The new password has to be different from
the current one."* with no reference code; choosing one brought the tabs back and loaded the
library. Then the stack was put back to `smtp` and the probe accounts deleted.

### Videos in the game page — verified on 2026-08-18

The maintainer's note 11, and the last of the eight findings from the 2026-08-17 testing. Both
open questions were his and were already answered: **uploaded files, not external links**, and
**LibVLC in-app**. The server half is the backend's D64; this side is **D74**, **D75** and
**D76**, and the pages are
[Documentation/catalog-and-artwork.md](Documentation/catalog-and-artwork.md) and
[Documentation/publishing.md](Documentation/publishing.md).

- ✅ **A fifth `MediaKind`**, its own list on `GameDetail`, and `MediaCardViewModel.IsVideo` —
  which is what stops a container being handed to an image decoder, in one place rather than at
  every call site
- ✅ **Three new capability keys**, and a fallback of *no video* whose asymmetry with
  `mail.enabled` is written out in D74. The video size limit is enforced **before** the upload
  because the server's refusal for an oversized body is a bare 413 with no problem document in it
- ✅ **`VideoFormats`**, the client half of the server's container sniff: the ISO base media
  brand rather than the `ftyp` box alone, and the EBML DocType from the first 64 bytes, so a
  photograph is not offered as a trailer and Matroska is refused before it travels
- ✅ **`IVideoPlayback`** and a game page that plays, stops, and stops again on Back, on another
  game and on a change of account
- ✅ **52 new tests** — the sniffer, the video upload rules, the two galleries, the dashboard,
  and the whole state machine around playback. Client **1032/1032** (396 Core, 293
  Infrastructure, 324 App, 11 Updater, 8 Views; 3 skipped on Windows)

**Looked at, not inferred.** An MP4 and a WebM uploaded to the real server played in the real
window, one replacing the other, with Stop and Back both silencing it — and the window found a
bug no test could: a hidden `VideoView` created a native child window on **every** game page and
crashed the launcher, because `IsVisible` does not detach a control and the manifest carried no
`supportedOS` list (D76, and two rows in §7). That is the third bug this project has found by
opening the window.

**Not verified in the window**: uploading a video *through the dashboard's file picker*. The
view model is covered by tests from the kind dropdown through to the request, and the picker is
the same `IFilePicker` the image upload has used since M8 — but nobody has watched a trailer
chosen in a file dialog reach the server. The two videos on the development stack were put there
with `curl`, and the dashboard's list of them was looked at.

### A launcher that works with the server down — verified on 2026-08-18

The maintainer's report: with the backend stopped, the launcher could not be signed into, and
somebody already signed in got no library. Both were true, and neither was a missing feature —
D29 had decided all of this in milestone 8. What was missing is that **nobody wrote the answer
down**, so every request rediscovered the dead server and paid for it. D77 and D78 have the
reasoning; the failure is reproduced in the log rather than described:

```
21:19:38  POST /auth/refresh          <- start-up, sign-in screen on display
21:20:01  "The server could not be reached"   (23 seconds)
21:20:01  GET /library                <- and another doomed refresh behind it
```

- ✅ **`IServerReachability`** in Core, pure state over a `TimeProvider`: a 20-second circuit,
  half-open on its own, plus `RetryNow()` for the one thing that always deserves a real
  attempt — a button somebody pressed. `IsOnline` (what the banner says) and `AllowsRequests`
  (whether to send) are separate on purpose
- ✅ **`ReachabilityHandler`** on every API client, beside `BearerTokenHandler` and for D14's
  reason. It reports every outcome, refuses to send while the circuit is open, and gives an
  **unproven** server 8 seconds to answer — because the real failure is a proxy that accepts
  and then says nothing (§7), which no connect timeout can catch
- ✅ **`AuthenticationService` no longer rotates against a server known to be missing**, which
  is what turned one dead backend into one timeout per card on a page
- ✅ **`ILibraryCache` / `FileLibraryCache`**: the last answer to `GET /library`, per account,
  so the offline library is the account's library rather than the corner of it that happens to
  be installed here. Installs the stored list does not mention are appended
- ✅ **The screens say it**: an offline banner with "Try again" on the library, the same notice
  **before a password is typed** on the sign-in screen, and a library that reloads itself when
  the server comes back. 2 new resource keys in English, Italian and French
- ✅ **1071/1071 green** (408 Core, 309 Infrastructure, 332 App, 11 Updater, 8 Views — 3 skipped
  on Windows), `dotnet format --verify-no-changes` clean

**Verified by looking at the window, with the real stack.** With the API stopped: the launcher
reaches the **library** in ~8 seconds instead of showing the sign-in screen for 45, with the
offline banner and Play working. With the API running, a second game was added to the account
and the cache written; the API was stopped and the launcher restarted, and the library showed
**both** games — the installed one playable, the owned-one not — from the stored answer, with
the summaries only a server has. "Try again" with the API back put the banner away and reloaded
the list. And with no session at all, the sign-in screen carries the notice and its button
instead of a form that refuses every password after a timeout.

**And the way in for somebody who is not signed in** (D79), added the same evening on the
maintainer's ask: the sign-in screen now says the server cannot be reached and offers
**Continue offline**, which opens the library with no session at all — the installed games,
playable, under a banner that says as much. It is offered only when the server is missing *and*
this computer has a game on it, the header grows a **Sign in** button because there is no
session to sign out of, and the card's **Details** button goes while offline: that page needs
the catalog, and what it used to say was "your session has expired" about a session that never
existed. 3 more resource keys in the three languages; **1082/1082 green**.

**Not covered**: `ILibraryCache.ClearAsync` has no caller — it is there for account erasure and
nothing calls it yet, so a library list outlives the account on a shared machine until it is
overwritten. Explore and the game page still show their ordinary network error offline rather
than anything cached; only the library is remembered, because it is the only page whose content
is the thing somebody paid for.

### The API's address comes from a registry — verified on 2026-08-19

The maintainer's own note, the one line in `ClaudeContent/appunti.txt`: *"Api da dove ottenere
l'api url aggiornato"*. Until today the address of the backend was a string in
`launcher.config.json`, so moving the server meant cutting a release — and every copy already
installed stayed broken until somebody installed it. D80 has the reasoning; the service is a
separate repository, [ServiceRegistry](https://github.com/Ruy41321/ServiceRegistry), built the
same day.

- ✅ **`Core/Discovery`**: `SignedEndpointReader` (verify, *then* parse — D19's order applied to
  the document that says where the server is), `EndpointClaim`, `EndpointResolver`, and
  `ServiceRegistryKey`, empty in this repository exactly as `LauncherReleaseKey` is
- ✅ **The signature is ECDSA P-256/SHA-256** and `ReleaseSignature` verifies it unchanged: the
  registry was moved off Ed25519 to the curve this client already checks, so no new dependency
  and one signature scheme in the launcher rather than two
- ✅ **A sixth `HttpClient`** — no bearer token, no base address, a 5-second timeout — because
  the one host it talks to is the one that says where the API is
- ✅ **`FileEndpointCache`** stores the signed envelope and reading re-verifies it, so editing
  the file redirects nothing
- ✅ **The DI graph resolves the endpoint before it builds any client**, which is the last
  moment at which one address can still be chosen, and logs which address it used and where it
  came from — the first question about any report
- ✅ [Documentation/service-discovery.md](Documentation/service-discovery.md), the tenth page
- ✅ **1140/1140 green** (444 Core, 332 Infrastructure, 345 App, 11 Updater, 8 Views — 3 skipped
  on Windows), `dotnet format --verify-no-changes` clean
- ✅ No new resource keys: nothing here is said to the user, on purpose

**Verified against the real thing, in the real window.** `launcher.config.json` was pointed at
`http://127.0.0.1:9/api/v1/` — an address nothing listens on — with the registry holding the
true one, so a launcher that worked could only have got it from the registry. Four runs: the
first resolved `from "Registry"` and then read `/capabilities` off the backend, with the window
up and the sign-in screen complete; the second answered `from "Cache"` in 0.6s with the registry
request happening *behind* the window; the third had its cache file edited to name
`evil.example.com` and logged *"the stored endpoint did not verify and was ignored"* before
asking the registry again; the fourth, with the registry stopped and no cache, fell back to the
shipped address in 3.5 seconds and still opened. The cross-language half is pinned in the suite
rather than left to the session: `RegistrySigningFixture.GoldenEnvelope` is an envelope the Go
service really produced, and a Core test verifies it with .NET's own `ECDsa`.

**Not done, deliberately**: a backend that moves is picked up at the *next* start, not the one
in progress — every typed client binds its base address when the container is built, and the
first launch after a move fails to reach the server. It is stated in D80 and in the document
rather than left to be discovered.

### Shipping it: installers, branding, and a first deployment — 2026-08-24

Not a milestone: the day the two backends went onto a real server and the launcher was built to
be handed to somebody. What changed *here* is what a fork needs and did not have.

- ✅ **An installer and a tarball** — `installer.iss` (Inno Setup) and `scripts/package-linux.sh`
  beside `install.sh`. Neither is part of the release loop: an update is still the signed archive
  the launcher unpacks itself, and neither is ever published to the server. Both refuse a payload
  with no `updater/`, both install **per-user**, and both check that the *parent* directory is
  writable — a swap renames the installation aside as the launcher exits, with nobody there to
  answer a UAC prompt, so an installation under Program Files would update exactly once: never
- ✅ **`BrandingConfiguration` is finally read** (D81), and `<ApplicationIcon>` is conditional on
  `assets/icon.ico`, which is the icon Windows puts on the file rather than in the window
- ✅ **Two Linux-only bugs**, both found at a red `main` and neither visible here: a swap-path test
  that had only ever passed on Windows, and a branding path with backslashes, which on Linux names
  one file that does not exist rather than a file inside a directory. §6 now has the container
  command that would have caught both
- ✅ **1149 green here, 1152 in the container** — the three that skip themselves on Windows are the
  Unix file-mode ones, and nobody had ever watched them pass

**Verified by opening the window**, which is where the branding is: the logo beside the name, the
icon in the title bar, and the log saying `Using API address https://api…, from "Registry"` — the
address off the signed registry answer rather than out of `launcher.config.json`, which is the
whole reason D80 exists, seen working against a real deployment for the first time.

**Not done here, and not this repository's to do**: the first signed release. The loop of §7 of
DISTRIBUTING.md — bump, build, zip with Python, sign with `printf`, `--publish-release` — has
never been run against a server anybody but the maintainer can reach.

### Next up

The numbering is shared with the backend repository. Self-update is not a numbered milestone —
it was part of M8 in the original plan and came out of it because it cannot be built here alone.

**Six of the maintainer's eighteen notes, three of them now closed.** The list said every one
needed the backend first, and item 3 turned out not to: the routes were all there and the only
open question — whether a draft's detail reaches its owner — was answered by reading the server
and driving it, not by changing it. The two that remain really do need the server. In the order
they are worth doing:

1. ✅ ~~**Precise validation messages** (note 1)~~ — done on 2026-08-17, above, D64.
2. ✅ ~~**The build's name, and publishing a version later** (notes 18 and 13)~~ — done on
   2026-08-17, above, D65. The route really did not exist.
3. ✅ ~~**Previews for the publisher** (notes 8 and 9)~~ — done on 2026-08-17, above, D66. The
   server needed nothing: it already served a draft's detail to its owner.
4. ✅ ~~**Videos in the game page** (note 11)~~ — done on 2026-08-18, above, D74/D75/D76 here
   and D64 there. What follows is what the entry said before it was done, kept because the three
   things it predicted all turned out to be the expensive ones. Both open questions were
   answered by the maintainer, on 2026-08-17: **uploaded files, not external links**, and
   **LibVLC in-app, not the system player**. So the client plays video inside the game page with
   `LibVLCSharp.Avalonia` plus a native `VideoLAN.LibVLC.*` package per platform, and the
   ~100 MB per RID inside the self-contained build is an accepted cost rather than an open
   trade-off. Three things follow and are worth writing down before the first line of code:
   the dependency is **native**, so it is the first thing in this repository that has to be
   verified per RID on real hardware (§7's rule about the self-update, met again); playback is
   the one surface whose *behaviour* no view-model test reaches, so what is tested is the state
   machine around it and what is checked by hand is the picture; and the server half comes
   first — a new `game_media_kind` value, its migration, a size limit of its own in
   `/capabilities`, and a decision about whether a video is sniffed the way an image is (D28
   says the answer cannot be the uploader's `Content-Type`).
5. ✅ ~~**Mail as an option** (note 14)~~ — done on 2026-08-18, above, D72 here and D63 there.
   `/capabilities` declares whether mail works, an operator hands out a one-time password on the
   loopback surface, `JwtAuthFilter` refuses everything but the change until it is replaced, and
   the launcher lands on the screen that ends it instead of on a page that answers 403.

**And three more from the maintainer's testing of 2026-08-17**, all three now closed — the two
that needed a decision written first got D69 and D70:

6. ✅ ~~**Play still appears on a library card when an update is pending**~~ — done on
   2026-08-18, above, D69. The cost is one request per *installed* game, after the list is
   drawn; offline and on a refused check the button stays, because D29 outranks it.
7. ✅ ~~**The dashboard shows the previous account's game after a sign-out and a sign-in**~~ —
   done on 2026-08-18, above, D70, and the backend half was demonstrated against the running
   stack rather than asserted: sixteen write routes, two publishers, all sixteen refused. The
   nine that had no test have one.
8. ✅ ~~**Where a game installs, in Settings**~~ — the settings were there; the page was blank.
   Done on 2026-08-17, above, D68.

The older debts and deliberate absences are unchanged: no virtualisation in Explore (open debt
21), `UserSettings.LaunchMinimized` still inert (13), no data export (17), the detail page still
needing a server (11), and no automated test that touches nginx or a real swap (8, 22). Each of
those is a choice with its reasons recorded, not a gap somebody forgot.

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
