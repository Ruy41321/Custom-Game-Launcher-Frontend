# Custom Game Launcher

An open-source, self-hostable game launcher in the style of the Epic Games Store, for indie
and hobbyist developers who need to get demos and in-development builds to a handful of
testers — without zip files on Discord or hand-managed Drive links.

Built with [Avalonia](https://avaloniaui.net/) and .NET 9. **Windows, Linux and macOS.**
Fork it, edit one JSON file, point it at your own server.

The server side lives in
[Custom-Game-Launcher-Backend](https://github.com/Ruy41321/Custom-Game-Launcher-Backend).

> **Status:** in development. Authentication, Explore, the library, delta downloads and
> installs, launching, offline mode, artwork and the devlog, and publishing a build from the
> client all work. Self-update is a stub, and the developer dashboard cannot yet upload
> artwork or write devlog entries. See [CLAUDE.md](CLAUDE.md#10-progress) for the current
> state and `Documentation/` for what each module does and does not do.

## Features

- **Library** — only what you installed through the launcher, searchable by name, sorted by
  release date, with per-game patch notes as devlog cards
- **Explore** — discover games not in your library yet
- **Delta updates** — only changed files are downloaded, resumable after an interruption
  without ever corrupting an installation, with post-update integrity verification
- **Offline mode** — installed games launch with no network at all
- **Per-game launch options** — pick the executable and pass command-line arguments
- **Self-updating** — *planned.* The separate updater process exists as a stub; it needs a
  launcher-release surface on the server before it can be finished
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

Replace the runtime identifier with `linux-x64`, `osx-x64` or `osx-arm64` as needed. CI
builds all four on every push.

## Documentation

One document per module, in `Documentation/`. Together they describe what the launcher does
and, more usefully, why each part works the way it does — including what breaks if you change
it, and what is deliberately not implemented yet.

| Document | What it covers |
|---|---|
| [architecture.md](Documentation/architecture.md) | The three projects, the dependency rule, MVVM, start-up, the four HTTP clients |
| [authentication-and-session.md](Documentation/authentication-and-session.md) | Sign-in, token rotation, where the session is stored, working offline |
| [catalog-and-artwork.md](Documentation/catalog-and-artwork.md) | Explore, the library, the game page, covers, the image cache, the devlog |
| [downloads-and-installs.md](Documentation/downloads-and-installs.md) | Plan, staging, apply, verification, install states, startup recovery |
| [launching-games.md](Documentation/launching-games.md) | Starting a game, the four refusals, per-game launch options |
| [publishing.md](Documentation/publishing.md) | Packaging, resumable upload, server capabilities, the developer dashboard |
| [configuration-and-localization.md](Documentation/configuration-and-localization.md) | `launcher.config.json`, user settings, `.resx`, forking and rebranding |
| [logging-and-local-state.md](Documentation/logging-and-local-state.md) | What the launcher writes to disk, on all three platforms |

## Project layout

| Path | Contents |
|---|---|
| `src/GameLauncher.Core` | Domain models, service interfaces, configuration, localization. No UI, no I/O. |
| `src/GameLauncher.Infrastructure` | API client, download engine, local storage, logging |
| `src/GameLauncher.App` | Avalonia views, view models, composition root |
| `src/GameLauncher.Updater` | Standalone self-update helper |
| `tests/` | xUnit test projects |
| `Documentation/` | One document per module — see the table above |

Architecture, conventions and the running list of technical decisions live in
[CLAUDE.md](CLAUDE.md).

## Contributing

Development happens on `dev`; `main` is merged by the maintainer once work is validated.
Commits are atomic and use conventional prefixes (`feat:`, `fix:`, `test:`, `docs:`, …).
Code, comments and commit messages are in English — only the translation resources are not.
CI runs formatting, the full test suite on all three operating systems, and a publish check
for every runtime identifier.

## Licence

[MIT](LICENSE) © 2026 Luigi Pennisi
