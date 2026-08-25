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
constant in the binary rather than a field here. The **service registry's** verification key is
in the binary for the same reason, while its URL is a field here — pointing a launcher at a
registry that cannot produce an acceptable signature achieves nothing, so only one of the two
has to be out of reach.

---

## `launcher.config.json` — the fork-and-rebrand surface

```json
{
  "appName": "My Studio Launcher",
  "apiBaseUrl": "https://games.mystudio.dev/api/v1/",
  "serviceRegistry": { "url": null, "serviceKey": "game-launcher-api", "environment": "production" },
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
| `apiBaseUrl` | `http://localhost:8080/api/v1/`. With a registry configured this becomes the **fallback** — see [service-discovery.md](service-discovery.md) |
| `serviceRegistry.url` | no registry: the address above is used as it always was |
| `theme.variant` | `dark` — the product default. `light` and `system` are the alternatives |
| `theme.accentColor` | nothing. **It is read and not applied** — see below |
| `branding.logoPath` | no logo beside the app name. The path is relative to the application directory and is **case-sensitive on Linux** |
| `branding.windowIconPath` | the toolkit's default window icon. A PNG is fine here, unlike the executable's own icon |
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

It cannot be a `StringFormat` either, and that one cost a whole page. `{loc:Tr}` returns a
`Binding`, so

```xml
<!-- Do not: this throws when the view is constructed. -->
<TextBlock Text="{Binding DefaultInstallDirectory, StringFormat={loc:Tr Settings.InstallDirectoryDefault}}" />
```

fails with `Unable to cast object of type 'Avalonia.Data.Binding' to type 'System.String'` —
**at construction, for the whole view**. The shell's `ContentControl` swallows it and renders an
empty rectangle, so the Settings page showed *nothing at all* while the launcher ran, the suite
stayed green, and the maintainer reported the settings as missing. Compose the sentence in the
view model instead (`SettingsViewModel.DefaultInstallDirectoryNotice`), which is what the page
already did next door. `tests/GameLauncher.Views.Tests` now builds every view so a page that
cannot be constructed fails a test instead of appearing blank (D68).

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
- **A view that cannot be constructed fails the build**, in `tests/GameLauncher.Views.Tests`,
  which is the one project with a running Avalonia (headless). It answers one question per
  page — does this build at all — because the answer was no for Settings and nothing said so.

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

1. Edit `launcher.config.json`: name, `apiBaseUrl`, logo, window icon, release channel, and the
   registry's URL if it has one.
2. Drop in the logo and window icon assets — and `assets/icon.ico` if the fork wants Windows to
   put its icon on the executable itself, which is a compiled-in Win32 resource rather than
   anything this file can describe.
3. Optionally add or remove languages.
4. **If the fork publishes launcher releases**, put its signing key's public half in
   `LauncherReleaseKey.PublicKeyBase64` —
   `src/GameLauncher.Core/Updates/LauncherReleaseKey.cs`.
5. **If the fork runs a service registry**, put *its* public half in
   `ServiceRegistryKey.PublicKeyBase64` —
   `src/GameLauncher.Core/Discovery/ServiceRegistryKey.cs`.
6. `dotnet publish -c Release -r <rid> --self-contained` for each runtime identifier.

**Steps 4 and 5 are the only code changes a fork makes, and both are deliberate.** Everything
else is configuration; a key is not, because *the file the updater overwrites must not be the
file that authorizes the update* — `launcher.config.json` ships inside the directory a swap
replaces. The reasoning in full is in [self-update.md](self-update.md) and
[service-discovery.md](service-discovery.md).

Note the asymmetry with the registry's **URL**, which *is* configuration: pointing a launcher at
a hostile registry gains an attacker nothing, because the answer will not verify. What
authorizes is code; what is merely pointed at is not.

A fork that does neither skips both: each key is empty by default, and an empty key means the
launcher asks nothing at all rather than asking and trusting whoever answers.

Apart from those two lines: no rebuild of the resource assembly, no search-and-replace across
the tree. That is the property this whole document exists to keep true.

---

## What is not implemented

- **`theme.accentColor` is read and applied to nothing.** It is deserialized, it is validated,
  and no code consults it: `ApplyTheme` reads `variant` alone, and the four views that use an
  accent bind to `SystemAccentColor`, which is the toolkit's — the operating system's — and not
  this file's. Setting it changes nothing on screen. It is documented here rather than quietly
  removed because the field has shipped since milestone 1 and forks have it in their files
  already; wiring it up means overriding that resource and its Fluent variants at start-up, and
  answering a malformed colour with the default rather than a failed launch. Until then, the
  honest entry is this one. (`theme.variant` **is** applied, and always has been.)
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
