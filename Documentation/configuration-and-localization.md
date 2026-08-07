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

They are never merged into one file, and the reason is the self-update: a launcher that replaces
its own shipped configuration during an update must not touch anything the user chose. One
writable config makes every update a chance to clobber somebody's language.

That constraint was accepted from the start, before any of the update mechanism existed, and it
is the reason none of this has to be retrofitted now that the check does — see
[self-update.md](self-update.md). It also decides where the **signing key** does *not* go: the
file the updater overwrites cannot be the file that authorizes the update, so the key is a
constant in the binary rather than a field here.

---

## `launcher.config.json` — the fork-and-rebrand surface

```json
{
  "appName": "My Studio Launcher",
  "apiBaseUrl": "https://games.mystudio.dev/api/v1/",
  "theme": { "variant": "dark", "accentColor": "#7C5CFF" },
  "branding": { "logoPath": "assets/logo.png", "windowIconPath": null },
  "localization": { "defaultLanguage": null, "supportedLanguages": ["en", "it", "fr"] },
  "updates": { "channel": "stable" },
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
| `updates.channel` | `stable`. `beta` is the other one, and anything unrecognised is read as `stable` |
| `defaultInstallDirectory` | decide from the platform — the right answer on a machine the packager knows nothing about |

### Why the channel is here and not in the user's settings

Which release stream a launcher follows is the choice of **whoever distributes it**. A player who
could move themselves onto a stream their distributor never published to would be a player who
can replace their own launcher with a build nobody meant them to have, and the launcher is the
program that has to still start in order to fix anything.

It is also the one field whose invalid value does **not** fail validation: an unrecognised
channel is read as `stable`. `apiBaseUrl` is refused because a launcher pointed at nothing is
useless anyway, while a launcher that will not open because of a spelling mistake in a channel
name is a working launcher destroyed by a typo.

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

**`LaunchMinimized` is not shown in the Settings page**, deliberately: nothing reads it, and an
inert checkbox is worse than an absent one because it makes a promise the launcher does not keep.
It stays in the model because removing it would mean a settings-file migration for a field that
is coming back. `SendCrashReports` used to be in the same position and is now on the page,
because the uploader that reads it exists — see
[logging-and-local-state.md](logging-and-local-state.md).

There is deliberately **no update setting here at all**: the channel belongs to the packager
(above), and whether to install an offered update is a button rather than a preference.

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

**The name of that notification is the whole mechanism, and it is easy to get wrong.** Avalonia
invalidates an indexer binding on `PropertyChanged("Item")` — the indexer's own CLR property
name. It ignores `"Item[]"`, which is WPF's `Binding.IndexerName` and was what this class raised
until 2026-08-07, and it ignores `null` and `""`, which almost every other binding system reads
as "every property changed". The failure is silent and total: the strings resolve correctly the
first time and then never change again, so the launcher renders whatever language start-up got
to first. `TrExtensionTests` drives a real Avalonia binding rather than asserting the name, since
a test on the name would have been satisfied by the broken value.

### What a `{loc:Tr}` binding cannot do for you

A string a view model **builds** — `Translate(key, argument)` with a version number or a
directory in it — is an ordinary property and not a binding into the resources, so it does not
re-evaluate. `MainWindowViewModel.RefreshLocalizedText` subscribes to `LanguageChanged` and
rebuilds those by hand; the welcome line and the update banner's three sentences are the ones
that exist today. Adding another composed sentence means adding it there too, and the way this
is noticed is by looking at the window.

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

1. Edit `launcher.config.json`: name, `apiBaseUrl`, accent colour, logo, release channel.
2. Drop in the logo and window icon assets.
3. Optionally add or remove languages.
4. **If the fork publishes launcher releases**, put its signing key's public half in
   `LauncherReleaseKey.PublicKeyBase64` — one line in
   `src/GameLauncher.Core/Updates/LauncherReleaseKey.cs`.
5. `dotnet publish -c Release -r <rid> --self-contained` for each runtime identifier.

**Step 4 is the only code change a fork makes, and it is deliberate.** Everything else is
configuration; the key is not, because *the file the updater overwrites must not be the file that
authorizes the update* — `launcher.config.json` ships inside the directory a swap replaces. The
reasoning in full is in [self-update.md](self-update.md).

A fork that does not sign releases skips it: the key is empty by default, and an empty key means
the launcher checks for no updates at all rather than checking and trusting whoever answers.

Apart from that one line: no rebuild of the resource assembly, no search-and-replace across the
tree. That is the property this whole document exists to keep true.

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
- [self-update.md](self-update.md) — the signing key, the channel, and why they live where they do
