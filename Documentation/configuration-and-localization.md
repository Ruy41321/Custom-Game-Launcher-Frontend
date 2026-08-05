# Configuration, localization and forking

This is the document to read if you want to **rebrand this launcher for your own studio**. It is
also the document that explains why the two configuration files are two files and never one.

Implemented in `Core/Configuration/{LauncherConfiguration,UserSettings}.cs`,
`Core/Localization/*`, `Infrastructure/Configuration/*`, `App/Localization/*` and
`App/ViewModels/SettingsViewModel.cs`.

---

## Two files, one direction (D5)

| File | Written by | Holds |
|---|---|---|
| `launcher.config.json`, beside the executable | the packager, shipped **read-only** | app name, API endpoint, theme, branding, supported languages, default install directory |
| `launcher.settings.json`, under the platform app-data directory | the **user**, at run time | chosen language, chosen theme, install directory, crash-report opt-in |

Precedence is **user setting → shipped configuration → built-in default**.

They are never merged into one file, and the reason is the self-update that does not exist yet:
a launcher that replaces its own shipped configuration during an update must not touch anything
the user chose. One writable config makes every update a chance to clobber somebody's language.

That is a design constraint accepted **now**, before the update mechanism exists, because
retrofitting it afterwards would mean migrating a file that already carries both.

---

## `launcher.config.json` — the fork-and-rebrand surface

```json
{
  "appName": "My Studio Launcher",
  "apiBaseUrl": "https://games.mystudio.dev/api/v1/",
  "theme": { "variant": "dark", "accentColor": "#7C5CFF" },
  "branding": { "logoPath": "assets/logo.png", "windowIconPath": null },
  "localization": { "defaultLanguage": null, "supportedLanguages": ["en", "it", "fr"] },
  "defaultInstallDirectory": null
}
```

| Field | Null / absent means |
|---|---|
| `appName` | the built-in name |
| `apiBaseUrl` | `http://localhost:8080/api/v1/` |
| `theme.variant` | `dark` — the product default. `light` and `system` are the alternatives |
| `branding.logoPath` | the built-in asset; the path is relative to the application directory |
| `localization.defaultLanguage` | **follow the operating system's UI language** |
| `defaultInstallDirectory` | decide from the platform — the right answer on a machine the packager knows nothing about |

### Validation reports everything, and fails hard

`LauncherConfiguration.Validate()` returns **every** problem it found, not the first. Making a
packager fix one typo per run is a poor way to spend somebody's afternoon.

A missing file yields the defaults. A **malformed or invalid** one throws at start-up, and the
exception lists all the problems. Running with half-applied branding is worse than a clear
startup failure: the launcher would open under the wrong name, pointed at the wrong server, and
nothing would say so.

The checks are the ones whose failure is silent otherwise: `appName` not empty, `apiBaseUrl` an
absolute `http`/`https` URL, and `defaultLanguage` actually one of `supportedLanguages`.

### The trailing slash on `apiBaseUrl`

A base address **without** a trailing slash silently drops its last segment when a relative path
is resolved against it — `/api/v1` becomes `/api/`, and every call 404s in a way that looks like
a broken server. `ServiceCollectionExtensions` appends one rather than rejecting the URL,
because it is a typo with an unambiguous intent.

---

## `UserSettings`

```
Language           null → launcher.config.json → OS language
ThemeVariant       null → launcher.config.json
InstallDirectory   null → the platform default
SendCrashReports   opt-in, default false
LaunchMinimized
```

Read and written by `JsonUserSettingsStore` under `IPathProvider.UserDataDirectory`.

**`InstallDirectory` decides where the *next* game goes** (D33). Games already installed keep
their directory, and the Settings page says so rather than leaving it to be discovered — moving
somebody's gigabytes because a preference changed is a different action from choosing where the
next install lands. A configured directory that cannot be created falls back to the platform
default rather than refusing to install.

**`SendCrashReports` and `LaunchMinimized` are not shown in the Settings page**, deliberately.
Nothing reads them: crash-report upload is not implemented, and neither is starting minimised.
An inert checkbox is worse than an absent one, because it makes a promise the launcher does not
keep. They stay in the model because removing them would mean a settings-file migration for a
field that is coming back.

---

## Localization (D3)

`Strings.resx` is English and neutral; one satellite assembly per language
(`Strings.it.resx`, `Strings.fr.resx`). `ResourceManagerLocalizationService` resolves keys and
raises `LanguageChanged`.

**XAML never references a resource directly.** It goes through the markup extension:

```xml
<TextBlock Text="{loc:Tr Nav.Library}" />
```

`TrExtension` returns a **binding to an indexer** on `LocalizationSource`, not a string. When
the language changes, `LocalizationSource` raises `PropertyChanged` for the indexer and every
localized element re-reads its value — so **switching language needs no restart**.

`x:Static` on the generated resx class was rejected for exactly that: it resolves once, at load.

`LocalizationSource.Instance` is the single global in the application. A markup extension is
instantiated by the XAML loader and has no access to the DI container, so the instance is
published during start-up. It is the one place a static is the honest answer rather than a
shortcut.

### Adding a language

1. Copy `src/GameLauncher.Core/Localization/Strings.resx` to `Strings.<culture>.resx` and
   translate the values.
2. Add the culture to `SatelliteResourceLanguages` in `GameLauncher.Core.csproj`.
3. Add a `LanguageOption` to `ResourceManagerLocalizationService.SupportedLanguages`.
4. Add it to `localization.supportedLanguages` in `launcher.config.json` if the fork ships it.

No UI code changes.

### Two convention tests, and why they are not optional

- **A missing key fails the build.** One test compares every satellite against English and
  fails if any language is short a key. Adding a string in English only would otherwise ship a
  launcher that shows a raw key to Italian and French users, and nobody would notice until
  somebody complained in a language the maintainer does not test in.
- **A literal user-visible string in `.axaml` fails the build.** The other test scans every
  `.axaml` for literal `Text=` / `Content=` values.

Together they mean: **a new UI string is added in English, Italian and French, all three, in the
same commit.** There is no partial state that passes.

Because view-model tests use the **real** localization service rather than a stub, an assertion
on a user-facing message also proves the key exists in every language — a third layer of the
same guarantee, obtained for free.

---

## Theme

`IThemeSwitcher` applies the variant at start-up and when the setting changes. `dark` is the
product default; `light` and `system` are supported. The accent colour is a `#RRGGBB` or
`#AARRGGBB` string from the shipped configuration — a fork's brand colour, not a user setting.

---

## What a fork actually has to do

1. Edit `launcher.config.json`: name, `apiBaseUrl`, accent colour, logo.
2. Drop in the logo and window icon assets.
3. Optionally add or remove languages.
4. `dotnet publish -c Release -r <rid> --self-contained` for each runtime identifier.

No code changes, no rebuild of the resource assembly, no search-and-replace across the tree.
That is the property this whole document exists to keep true.

---

## What is not implemented

- **No settings sync.** Preferences are per machine.
- **No configuration reload at run time.** `launcher.config.json` is read once at start-up and
  cached for the process; changing it needs a restart.
- **No right-to-left language support** has been tested. Adding a resx would work; the layouts
  have never been exercised mirrored.
- **No per-fork feature flags.** Everything in the configuration is branding and endpoints.

## Related documents

- [architecture.md](architecture.md) — the start-up sequence that reads both files synchronously
- [logging-and-local-state.md](logging-and-local-state.md) — where the user's file actually lives
- [downloads-and-installs.md](downloads-and-installs.md) — what `InstallDirectory` changes and what it does not
