# Client architecture

Detailed per-module documents will be added alongside the modules themselves (auth, library,
downloads, self-update). This document covers what exists today and the rules everything
else has to follow.

## Project graph

```
GameLauncher.App  ──►  GameLauncher.Infrastructure  ──►  GameLauncher.Core
       │                                                        ▲
       └────────────────────────────────────────────────────────┘

GameLauncher.Updater  ──►  GameLauncher.Core
```

**Core references nothing.** No Avalonia, no `HttpClient`, no file system, no SQLite. If a
type needs any of those it belongs in Infrastructure, behind an interface declared in Core.
That constraint is what keeps the domain logic testable in milliseconds.

`GameLauncher.App` is the only project that sees both sides: it is the composition root.

## MVVM

- A view is a `.axaml` file plus a code-behind containing nothing but `InitializeComponent`.
- View models derive from `ViewModelBase` (`ObservableObject`) and use the CommunityToolkit
  source generators. Nothing implements `INotifyPropertyChanged` by hand.
- Dependencies arrive through the constructor. No service locator, no static access from a
  view model — that is what makes them plain objects in tests.
- `ViewLocator` maps `…ViewModels.FooViewModel` to `…Views.Foo` by convention, so views do
  not have to be registered one by one.

## Start-up sequence

`Program.Main` runs before Avalonia so that a failure during initialisation is still logged:

1. Build `PathProvider` and configure Serilog.
2. Install the global exception handlers (`AppDomain.UnhandledException`,
   `TaskScheduler.UnobservedTaskException`).
3. Start Avalonia. `App.OnFrameworkInitializationCompleted` then:
   - builds the DI container,
   - loads `launcher.config.json` and the user settings,
   - applies theme and language,
   - publishes `LocalizationSource` for the XAML markup extension,
   - constructs the shell window and its view model.

Configuration and settings are read synchronously here on purpose: the shell cannot be
rendered before the app's name, theme and language are known, and an async gap would mean
showing a window that immediately restyles itself.

## Configuration: two files, one direction

| File | Written by | Contains |
|---|---|---|
| `launcher.config.json` | the packager, shipped read-only | app name, API endpoint, theme, branding, supported languages |
| `launcher.settings.json` in app-data | the user, at runtime | chosen language, theme, install directory, crash-report opt-in |

They are never merged into one file: a self-update replaces the shipped configuration, and
that must not touch anything the user chose. Precedence is
**user setting → shipped configuration → built-in default**.

An invalid `launcher.config.json` throws at start-up rather than being partially applied,
and the exception lists *every* problem found, not the first.

## Localization

`Strings.resx` (English, neutral) plus one satellite per language.
`ResourceManagerLocalizationService` resolves keys and raises `LanguageChanged`.

XAML never references resources directly. It goes through the markup extension:

```xml
<TextBlock Text="{loc:Tr Nav.Library}" />
```

`TrExtension` returns a *binding* to an indexer on `LocalizationSource`, not a string. When
the language changes, `LocalizationSource` raises `PropertyChanged` for the indexer and
every localized element re-reads its value — so switching language needs no restart.

`LocalizationSource.Instance` is the single global in the application. A markup extension is
instantiated by the XAML loader and has no access to the DI container, so the instance is
published during start-up.

Two tests enforce the rules: one fails if any language is missing a key English has, another
scans every `.axaml` for literal user-visible attribute values.

## Downloads and installs (design, milestone 7)

The server stores build files content-addressed: each file is a blob keyed by its SHA-256,
and a manifest maps relative paths to blob hashes. The client mirrors that:

1. **Plan** — diff the target manifest against the installed one. Fresh install = diff
   against an empty manifest. The server may advise a full download when the delta is too
   large a fraction of the whole.
2. **Space check** — compare required bytes against free space *before* touching anything.
3. **Fetch** — download into a staging directory with HTTP `Range`, so an interruption
   resumes. Each blob is written to `.part` and hash-verified before being accepted.
4. **Apply** — only once every blob is present and verified, move files into place and
   delete removed paths. An interrupted download must never leave a broken installation.
5. **Verify** — re-hash the installed tree against the manifest.

Uninstall deletes the install directory and its local database rows, and reports freed space.

## Local state

SQLite under the user-data directory holds installed games, cached manifests and download
progress. It is transactional and survives a crash mid-write, which a plain JSON file does
not — and download progress is exactly the state that gets written when the process dies.

## Logging and crash reporting

Serilog writes rolling daily files under the platform log directory, capped at 20 MB each
and 14 days. Timestamps use the invariant culture so logs do not change shape with the
machine's locale.

Crash reports are written to disk only. Nothing is ever transmitted unless the user has
opted in through `SendCrashReports`, and even then uploading is a separate explicit step.

## Security posture

Client-side permission checks exist purely so the UI does not offer actions that will fail.
**The server is the only authority.** Any check the client makes is made again server-side,
and the client never assumes its own check was sufficient.

## Testing

| Project | Covers |
|---|---|
| `GameLauncher.Core.Tests` | domain and service logic, localization, repository-wide conventions |
| `GameLauncher.Infrastructure.Tests` | configuration loading, settings persistence, platform paths, API client |

View models are tested as plain objects. There are no UI-automation tests: the value they
would add does not justify their fragility on three operating systems.
