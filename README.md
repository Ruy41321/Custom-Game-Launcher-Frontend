# Custom Game Launcher

An open-source, self-hostable game launcher in the style of the Epic Games Store, for indie
and hobbyist developers who need to get demos and in-development builds to a handful of
testers — without zip files on Discord or hand-managed Drive links.

Built with [Avalonia](https://avaloniaui.net/) and .NET 9. **Windows, Linux and macOS.**
Fork it, edit one JSON file, point it at your own server.

The server side lives in
[Custom-Game-Launcher-Backend](https://github.com/Ruy41321/Custom-Game-Launcher-Backend).

> **Status:** early development. The application shell, configuration, localization and
> logging are in place; login, library and downloads are next. See
> [CLAUDE.md](CLAUDE.md#10-progress) for the current state.

## Features

- **Library** — only what you installed through the launcher, searchable by name, sorted by
  release date, with per-game patch notes as devlog cards
- **Explore** — discover games not in your library yet
- **Delta updates** — only changed files are downloaded, resumable after an interruption
  without ever corrupting an installation, with post-update integrity verification
- **Offline mode** — installed games launch with no network at all
- **Per-game launch options** — pick the executable and pass command-line arguments
- **Self-updating** — the launcher updates itself, no manual re-download
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

## Project layout

| Path | Contents |
|---|---|
| `src/GameLauncher.Core` | Domain models, service interfaces, configuration, localization. No UI, no I/O. |
| `src/GameLauncher.Infrastructure` | API client, download engine, local storage, logging |
| `src/GameLauncher.App` | Avalonia views, view models, composition root |
| `src/GameLauncher.Updater` | Standalone self-update helper |
| `tests/` | xUnit test projects |
| `Documentation/` | One document per module |

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
