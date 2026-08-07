# Client architecture

The shape of the application: which project may reference which, how a view model reaches a
server, what runs before the window opens, and the two or three rules everything else in this
repository is a consequence of.

Every other document in `Documentation/` describes one module. This one describes what they
have in common, and it is the one to read first.

---

## Three projects and one direction

```
GameLauncher.App  ──►  GameLauncher.Infrastructure  ──►  GameLauncher.Core
       │                                                        ▲
       └────────────────────────────────────────────────────────┘

GameLauncher.Updater  ──►  GameLauncher.Core
```

| Project | Holds | May reference |
|---|---|---|
| `GameLauncher.Core` | Domain models, service interfaces, configuration contracts, localization contracts, the pure logic — `LaunchPlanner`, `ManifestPathRules`, `PathSafety`, `TransferRateEstimator`, `ByteSize` | nothing |
| `GameLauncher.Infrastructure` | The implementations: API clients, download engine, SQLite store, token store, image cache, platform paths, logging | Core |
| `GameLauncher.App` | Avalonia views, view models, `App.axaml.cs` — the composition root | Core and Infrastructure |
| `GameLauncher.Updater` | A standalone executable that swaps launcher files while the launcher is closed | Core |

**Core references nothing.** No Avalonia, no `HttpClient`, no `System.Data.Sqlite`, no file
system. That is not tidiness: it is what makes the interesting logic testable in milliseconds
without a UI toolkit, a server or a disk, and the test counts show it — `GameLauncher.Core.Tests`
is the fastest of the three projects and covers the rules that decide what the launcher does.

The rule has a mechanical consequence worth stating, because it is the one a new contributor
runs into first: **if a type needs `HttpClient`, Avalonia or the file system, it does not go in
Core — an interface for it goes in Core and the type goes in Infrastructure or App.**
`IImageLoader` is in Core and `CachingImageLoader` is in Infrastructure. `IGameLauncher` is in
Core and `ProcessGameLauncher` is in Infrastructure. `IFolderPicker`, `IFilePicker` and
`IImageProvider` are in App rather than Core, because what they wrap — Avalonia's storage
provider, a decoded `Bitmap` — is an Avalonia type, and pushing them down would drag Avalonia
into Core to save one file.

### What breaks if you change it

Adding a package reference from Core to Avalonia compiles. What it costs shows up later: every
view-model test then needs an initialised Avalonia (see the `Bitmap` row in `CLAUDE.md` §7),
and the tests that need a UI toolkit to run are the tests that get skipped. The dependency
direction is enforced by the `.csproj` files and by nothing else, so it is a rule a reviewer
has to hold.

---

## MVVM, and why the navigation runs one way

- A view is a `.axaml` file plus a code-behind containing nothing but `InitializeComponent`.
- View models derive from `ViewModelBase` (`ObservableObject`) and use the CommunityToolkit
  source generators — `[ObservableProperty]`, `[RelayCommand]`. Nothing implements
  `INotifyPropertyChanged` by hand.
- Dependencies arrive through the **constructor**. There is no service locator and no static
  access from a view model; that is what makes them plain objects in a test.
- `ViewLocator` maps `…ViewModels.FooViewModel` to `…Views.Foo` by convention. Note that it
  strips the literal text `ViewModel`, so the view class is `Login`, **not** `LoginView` —
  naming it `LoginView` compiles and renders "View not found" at run time.

**Navigation is one-directional (D17):** the shell knows its children, and the children raise
events. `MainWindowViewModel` constructs the pages and subscribes; a page that wants to open
another one raises an event rather than holding a navigator.

The reason is testability, not elegance. A child holding a navigator that holds the child
cannot be constructed in a test without building the whole object graph — and a view model
that is expensive to construct is a view model whose tests get written last and skipped first.
An injected `INavigator` was rejected for exactly that cycle; the shell resolving pages from
`IServiceProvider` was rejected because it is a service locator with a different name.

### Marshalling to the UI thread

`ViewModelBase.OnUiThread` posts to the `SynchronizationContext` **captured where the view
model was built** (D32). In the running application that is the UI thread. In a test there is
no context at all, so the callback runs inline — which is precisely what makes an assertion on
a background event deterministic instead of a race against the thread pool.

Anything raised off the UI thread goes through it: `IGameLauncher.GameExited` documents that it
fires on someone else's thread, and a binding updated from there is a crash that only ever
happens on a user's machine.

---

## Start-up sequence

`Program.Main` runs **before** Avalonia, so a failure during initialisation still ends up in a
log file rather than in a silent exit:

1. Build `PathProvider` and configure Serilog.
2. Install the global exception handlers (`AppDomain.UnhandledException`,
   `TaskScheduler.UnobservedTaskException`) — see [logging-and-local-state.md](logging-and-local-state.md).
3. Start Avalonia. `App.OnFrameworkInitializationCompleted` then:
   - builds the DI container with `AddLauncherInfrastructure()`,
   - loads `launcher.config.json` and the user settings,
   - applies theme and language,
   - publishes `LocalizationSource` for the XAML markup extension,
   - constructs the shell window and its view model,
   - restores the stored session and runs the startup recovery pass (D34).

Configuration and settings are read **synchronously** here on purpose: the shell cannot be
rendered before the application's name, theme and language are known, and an async gap would
mean showing a window that immediately restyles itself in front of the user.

---

## The composition root

`AddLauncherInfrastructure()` in
`src/GameLauncher.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` registers
every implementation behind its Core interface, and the App layer then knows nothing about
which concrete type it received. It is a single call, which is what makes the whole client
drivable from a twenty-line console program with no UI — see [Exercising the client](#exercising-the-client-without-a-ui).

### Four HTTP clients, and why they are four

This is the design decision most often mistaken for duplication, so it is worth having in one
table.

| Client | Bearer token | Base address | Timeout | Used by |
|---|---|---|---|---|
| auth | **no** | the API | 30 s | `IAuthApi` |
| capabilities | **no** | the API | 30 s | `ICapabilitiesApi` |
| crash reports | **no** | the API | 30 s | `ICrashReportApi` |
| launcher releases | **no** | the API | 30 s | `ILauncherReleaseApi` |
| API | yes | the API | 30 s | `ICatalogApi`, `ILibraryApi`, `IDownloadApi`, `IPublishingApi`, `IAccountApi` |
| file server | **no** | none (absolute signed URLs) | infinite | `IBlobFetcher` |
| artwork | **no** | none (absolute public URLs) | 30 s | `IImageLoader` |
| launcher artifact | **no** | none (absolute public URLs) | infinite | `ILauncherUpdateDownloader` |

Every row is its own registration; the tokenless ones against the API differ only in which
client they belong to, which is the point — the separation lives in the DI graph rather than in a
condition inside a handler.

- **The auth client carries no token (D14)** because refreshing a session has to work
  *precisely* when the access token has expired. One client whose handler obtained a token
  before every request would call `POST /auth/refresh` through that handler, which would try to
  obtain a token, which is the same call. Splitting the registration makes the cycle impossible
  to write rather than something a reviewer has to notice.
- **The capabilities client carries no token (D39)** because the limits document is what a
  launcher reads before it knows whether it can reach this server at all, and nothing in the
  document depends on who is asking.
- **The file-server client carries no token (D20)** because a signed URL carries its own
  authorization and *the API names the host it is on*. Attaching the launcher's credential
  would be handing it to a host the server chose. Its timeout is infinite because
  `HttpClient.Timeout` covers the response body as well as the headers and a build is
  arbitrarily large; a stalled transfer is bounded by the caller's cancellation.
- **The artwork client carries no token (D35)** for the same reason as the file server, and
  keeps the ordinary timeout for the opposite one: a cover is small, and one taking thirty
  seconds is one the page is better off without.
- **The launcher-release client carries no token** for the sharpest reason of the four: *the
  launcher that most needs an update is the one that cannot sign in* — pointed at a server it has
  never reached, holding an address nobody confirmed, or carrying the very bug the update fixes.
  The artifact it then fetches rides on a client shaped like the file server's, since a
  self-contained launcher is tens of megabytes served from a public URL. See
  [self-update.md](self-update.md).

**What breaks if you merge them:** the token leaves the API's origin. Not in a way any test
would catch — every request still succeeds — which is exactly why the separation lives in the
DI graph, where merging two registrations is a visible edit, rather than in a path comparison
inside a handler, where it is a condition somebody eventually gets wrong.

---

## Errors

`ApiTransport` is the one place that knows the API speaks HTTP. Every failure leaves it as an
`ApiException` carrying a typed `ApiErrorCode`: a refused connection and a client-side timeout
become `Network`, the server's RFC 7807 envelope becomes its own code, a body that is not JSON
(a proxy answering in HTML) becomes `Unknown`. No caller above it ever sees an
`HttpRequestException`.

`IApiErrorPresenter` then maps an `ApiException` to one localized sentence (D18). It is one
class rather than a method on each view model because *what the user is told* is a product
decision, and having it in one place is what stops each page inventing its own wording. It
takes an override for the single case where one code means two things: on the sign-in form a
401 means the password was wrong, not that a session aged out.

**Two rules the client must not break**, inherited from the server:

- **A 404 is shown as "not available", never as "you do not have permission."** The server
  answers 404 rather than 403 for anything the caller may not see — drafts, other publishers'
  builds, other people's upload sessions — specifically so that the existence of an unannounced
  title is not confirmed. A client that re-introduced the distinction in its wording would
  undo that server-side care with a string.
- **Client-side permission checks exist only so the UI does not offer an action that would be
  refused (D8).** Every one is enforced again server-side, and the client never assumes its own
  check was sufficient.

---

## Where the server's limits come from

Nothing in the client hard-codes a number the server owns. `GET /api/v1/capabilities` is read
at start-up through `CachedServerCapabilityProvider`, which caches for fifteen minutes and
**never throws**: an unreachable server, or one older than the route, yields
`ServerCapabilities.Fallback` (D39).

The split is deliberate and is stated again in [publishing.md](publishing.md): the *numbers*
come from the server, the *shape* rules stay client-side (D40). `maxPathLength` and `maxFiles`
are the server's to state; "no absolute path, no `..`, no backslash, no control character" is
what makes a path safe to resolve inside an install directory on **this** machine, so the
client would keep enforcing it against a server that stopped.

---

## Testing

| Project | Covers |
|---|---|
| `GameLauncher.Core.Tests` | domain and service logic with no I/O, localization, repository-wide conventions |
| `GameLauncher.Infrastructure.Tests` | API clients against a stub `HttpMessageHandler`, the download engine, the SQLite store, config loading, the image cache |
| `GameLauncher.App.Tests` | view models, exercised as plain objects |

Stack: **xUnit v3 + NSubstitute**, with xUnit's built-in `Assert` and no fluent-assertion
library (D11). Async tests take `TestContext.Current.CancellationToken`.

View-model tests use the **real** `ResourceManagerLocalizationService` rather than a stub, so
an assertion on a user-facing message also proves the resource key exists in every language.

Two convention tests exist and must keep passing:

- one fails when any language is missing a key English has;
- one scans every `.axaml` and fails on a literal user-visible attribute value.

There are no UI-automation tests. On three operating systems the fragility would cost more than
the coverage is worth, and the layering above is what makes that an acceptable trade rather
than a gap.

---

## Exercising the client without a UI

`AddLauncherInfrastructure()` builds the whole graph on its own, so a console project that
references Core and Infrastructure and resolves `IAuthenticationService`, `ICatalogApi`,
`IInstallationService` and `IBuildPublisher` drives every layer against a running stack. This
is how milestones 6, 7 and 8 were verified, and it reaches the one thing no test does: nginx
serving a real signed URL.

Two things make it safe and repeatable:

- register an `IPathProvider` **after** `AddLauncherInfrastructure()` — last registration wins —
  pointing at a temporary directory, or the run writes into the maintainer's real
  `%LOCALAPPDATA%\CustomGameLauncher`;
- copy `launcher.config.json` next to the executable; the configuration provider reads it from
  `IPathProvider.ApplicationDirectory`.

---

## What is not implemented

Stated explicitly, because the alternative is a reader inferring it from silence:

- **The self-update swap.** The **check** is implemented as of 2026-08-07 and has a page of its
  own: [self-update.md](self-update.md). The launcher reads
  `GET /api/v1/launcher/releases/latest`, verifies the ECDSA P-256 signature over the document's
  bytes as they arrived, refuses anything not strictly newer, and fetches an artifact only if its
  bytes hash to the content address inside the signed document.
  What is **not** implemented is the swap: `GameLauncher.Updater` still moves no files, so a
  verified download is announced with where it is rather than installed. Its command line is
  designed and its process boundary is real — a running executable cannot overwrite its own
  binaries on Windows (D7). Self-update is still **not** a numbered milestone.
- **`UserSettings.LaunchMinimized`** exists in the model and is read by nothing, which is why
  the Settings page does not show it: an inert checkbox is worse than an absent one.
  `SendCrashReports` used to be in this position and no longer is.

## Related documents

- [authentication-and-session.md](authentication-and-session.md) — the session, rotation, and working offline
- [catalog-and-artwork.md](catalog-and-artwork.md) — Explore, the library, covers and the devlog
- [downloads-and-installs.md](downloads-and-installs.md) — the download engine and install states
- [launching-games.md](launching-games.md) — starting a game and per-game options
- [publishing.md](publishing.md) — packaging, resumable upload, capabilities
- [configuration-and-localization.md](configuration-and-localization.md) — the fork-and-rebrand surface
- [logging-and-local-state.md](logging-and-local-state.md) — what the launcher writes to disk
- [self-update.md](self-update.md) — checking for a newer launcher, and what is not implemented
