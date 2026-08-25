# Custom Game Launcher

An open-source, self-hostable game launcher in the style of the Epic Games Store, for indie
and hobbyist developers who need to get demos and in-development builds to a handful of
testers — without zip files on Discord or hand-managed Drive links.

Built with [Avalonia](https://avaloniaui.net/) and .NET 9. **Windows and Linux.**
Fork it, edit one JSON file, point it at your own server.

The server side lives in
[Custom-Game-Launcher-Backend](https://github.com/Ruy41321/Custom-Game-Launcher-Backend).

> **Status:** everything this repository declares is implemented. Authentication, Explore with
> infinite scrolling, the library, delta downloads and installs, launching, offline mode,
> artwork and the devlog, settings, account deletion, opt-in crash reports, a developer
> dashboard that publishes builds and uploads artwork and writes the devlog, and **self-update**
> — verified signature, refusal of anything not strictly newer, a hash-checked download, and a
> swap that replaces the installation and rolls back a version that will not start.
>
> Two things are finished **in a browser**, on pages the server serves, and deliberately have no
> screen here: confirming an address and resetting a password.
>
> [CLAUDE.md](CLAUDE.md#10-progress) has the current state, `Documentation/` says what each
> module does *and does not* do, and [CONTRIBUTING.md](CONTRIBUTING.md) is where to start if you
> intend to change something.

## Features

- **Library** — only what you installed through the launcher, searchable by name, sorted by
  release date, with per-game patch notes as devlog cards
- **Explore** — discover games not in your library yet
- **Delta updates** — only changed files are downloaded, resumable after an interruption
  without ever corrupting an installation, with post-update integrity verification
- **Offline mode** — installed games launch with no network at all, covers included
- **Per-game launch options** — pick the executable and pass command-line arguments
- **A developer dashboard** — create a game, publish a build from a directory, upload covers
  and screenshots, write the devlog, and delete any of it
- **Crash reports** — off until you turn them on, redacted before they are written to disk
- **Self-updating** — the launcher tells you when a newer signed release exists, fetches it
  (refusing anything not strictly newer and any bytes the signature did not cover), replaces
  itself and restarts. A new version that fails to start is rolled back to the old one. It is
  always a button and never a timer
- **Dark theme by default**, configurable, with Italian / English / French from day one

## Requirements

- [.NET SDK 9](https://dotnet.microsoft.com/download) to build
- A running instance of the backend to connect to

## Quick start

```bash
dotnet restore GameLauncher.sln
```

```bash
dotnet run --project src/GameLauncher.App
```

Run the tests:

```bash
dotnet test GameLauncher.sln
```

## Making it yours

Everything a fork needs to rebrand lives in [`launcher.config.json`](launcher.config.json),
which ships read-only beside the executable:

```json
{
  "appName": "My Studio Launcher",
  "apiBaseUrl": "https://games.mystudio.dev/api/v1/",
  "theme": { "variant": "dark", "accentColor": "#7C5CFF" },
  "branding": { "logoPath": "assets/logo.png" },
  "localization": { "defaultLanguage": null, "supportedLanguages": ["en", "it", "fr"] }
}
```

User preferences — chosen language, theme, install directory — are stored separately under
the platform's app-data directory, so updating the launcher never overwrites them.

## Adding a language

1. Copy `src/GameLauncher.Core/Localization/Strings.resx` to `Strings.<culture>.resx` and
   translate the values.
2. Add the culture to `SatelliteResourceLanguages` in `GameLauncher.Core.csproj`.
3. Add a `LanguageOption` to `ResourceManagerLocalizationService.SupportedLanguages`.

No UI code changes. A test fails if any language is missing a key that English has.

## Publishing

```bash
dotnet publish src/GameLauncher.App -c Release -r win-x64 --self-contained
```

Replace the runtime identifier with `linux-x64` as needed; those two are the targets (D59). The
output carries `updater/` beside the executable — the helper that performs a self-update — and
CI publishes both RIDs on every run.

**If you are distributing your own launcher rather than developing this one**, read
[DISTRIBUTING.md](DISTRIBUTING.md): it walks the whole cycle, from editing one JSON file to
running a server and cutting a signed release.

## Documentation

Two entry points, depending on what you are here for:

| | For |
|---|---|
| [CONTRIBUTING.md](CONTRIBUTING.md) | Changing the launcher: the system in five minutes, the layout, the testing policy, the traps |
| [DISTRIBUTING.md](DISTRIBUTING.md) | Shipping **your own** launcher: branding, a server, a signing key, releases |

Then one document per module, in `Documentation/`. Together they describe what the launcher does
and, more usefully, why each part works the way it does — including what breaks if you change
it, and what is deliberately not implemented.

| Document | What it covers |
|---|---|
| [architecture.md](Documentation/architecture.md) | The three projects, the dependency rule, MVVM, start-up, the HTTP clients |
| [authentication-and-session.md](Documentation/authentication-and-session.md) | Sign-in, token rotation, where the session is stored, working offline |
| [catalog-and-artwork.md](Documentation/catalog-and-artwork.md) | Explore, the library, the game page, covers, the image cache, the devlog |
| [downloads-and-installs.md](Documentation/downloads-and-installs.md) | Plan, staging, apply, verification, install states, startup recovery |
| [launching-games.md](Documentation/launching-games.md) | Starting a game, the four refusals, per-game launch options |
| [publishing.md](Documentation/publishing.md) | Packaging, resumable upload, server capabilities, the developer dashboard |
| [configuration-and-localization.md](Documentation/configuration-and-localization.md) | `launcher.config.json`, user settings, `.resx`, forking and rebranding |
| [logging-and-local-state.md](Documentation/logging-and-local-state.md) | What the launcher writes to disk, on Windows and Linux |
| [self-update.md](Documentation/self-update.md) | Checking for a newer launcher, the five rules it holds, and the swap that replaces it |
| [service-discovery.md](Documentation/service-discovery.md) | Asking a registry where the API is, so a moved backend does not need a new release |
| [guided-deployment.md](Documentation/guided-deployment.md) | Addressed to an AI assistant helping somebody deploy: the order, the two silent failures, what never to touch |

## Project layout

| Path | Contents |
|---|---|
| `src/GameLauncher.Core` | Domain models, service interfaces, configuration, localization. No UI, no I/O. |
| `src/GameLauncher.Infrastructure` | API client, download engine, local storage, logging |
| `src/GameLauncher.App` | Avalonia views, view models, composition root |
| `src/GameLauncher.Updater` | Standalone self-update helper: replaces the installation and rolls it back |
| `tests/` | xUnit test projects |
| `Documentation/` | One document per module — see the table above |

Architecture, conventions and the running list of technical decisions live in
[CLAUDE.md](CLAUDE.md).

## Contributing

Start with [CONTRIBUTING.md](CONTRIBUTING.md): what the whole system is, how to get both halves
running, how the client is laid out, and the rules that would otherwise cost you an afternoon
each.

Development happens on `dev`; `main` is merged by the maintainer once work is validated.
Commits are atomic and use conventional prefixes (`feat:`, `fix:`, `test:`, `docs:`, …).
Code, comments and commit messages are in English — only the translation resources are not.
**CI runs on `main`**, so the gate before a push is the local one: `dotnet test` and
`dotnet format --verify-no-changes`, every time.

## Licence

[MIT](LICENSE) © 2026 Luigi Pennisi
