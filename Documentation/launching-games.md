# Launching games

The smallest module in the client and the only one that has to work with **no server at all**:
everything it needs is the local install row and the files on disk.

Implemented in `Core/Launching/{LaunchPlan,IGameLauncher}.cs` and
`Infrastructure/Launching/ProcessGameLauncher.cs`.

---

## A pure planner and a thin process wrapper (D27)

Every decision about how to start a game lives in `LaunchPlanner.PlanFor`, a pure function. It
takes the install row, the player's extra arguments and a `Func<string, bool> fileExists`, and
returns a `LaunchPlan` — executable, arguments, working directory — or throws with a reason.

Taking `fileExists` as a parameter rather than touching the disk is what makes the whole rule
set exercisable without a file system. And the rules are the part worth testing: what is left
after them is `Process.Start`, whose one interesting behaviour is noticing the exit, and that
gets a test which really starts the platform's own shell.

The alternative — one class that both decides and starts — leaves nothing testable without a
real executable, which in practice means the refusals are the code that never gets tested.

### The four refusals

| `LaunchFailure` | When |
|---|---|
| `NotInstalled` | nothing on this machine for that game |
| `NotPlayable` | the row is `Applying` or `Broken` — reinstalling repairs it |
| `EntrypointMissing` | the row says installed but the executable is not there, **or the entrypoint escapes the install directory** |
| `AlreadyRunning` | this launcher started it and has not seen it exit |
| `StartFailed` | the operating system refused to start the process |

Each is a different sentence to the user. A Play button that does nothing is the worst outcome
available here, so a refusal is always said out loud.

### The entrypoint gets the containment check too

The entrypoint is a manifest path like any other — except that it decides **what gets
executed** rather than merely where a byte lands. So it goes through the same
`PathSafety.ResolveInside` as every other manifest path (D24), and a path that escapes the
install directory is `EntrypointMissing` rather than a launch.

That rule lives in Core precisely because both the installer and the launcher apply it: one
rule with two implementations is one rule that will eventually disagree with itself.

---

## The working directory is the install root

Games resolve their assets relative to the working directory. Starting one from wherever the
launcher happens to live is how a game that works when double-clicked fails when launched from
a launcher — and the failure looks like a broken build rather than a wrong `ProcessStartInfo`.

---

## Arguments stay a command line (D28)

The publisher wrote a **string** into the manifest and the player writes a **string** into the
options. Neither is re-tokenised into `ProcessStartInfo.ArgumentList`.

Tokenising here would mean writing a second argument parser, and it would disagree with the one
the game itself uses — quoting and escaping rules differ per runtime, and the launcher has no
way to know which one it is starting.

**The player's arguments go after the build's.** Nearly every command-line parser lets the last
occurrence win, which is what makes an override an override.

### `LaunchOptions` is a column of its own

`InstalledGame.LaunchArgs` is what the manifest says. `InstalledGame.LaunchOptions` is what the
player set.

They are separate columns because **an update rewrites everything the manifest says**. Folding
them into one field means the next update silently discards a preference the player set — a
data loss with no error, noticed only when the game starts behaving differently.

---

## The game is a child, and it is not killed

`ProcessGameLauncher` starts the game as a **child** of the launcher so it can tell the process
is running and stop offering to start it twice. `IsRunning` and `Running` report that, and
`GameExited` fires when the runtime notices the exit.

The game is deliberately **not** killed when the launcher closes. A player who quits the
launcher has not asked to quit the game, and there is no reading of "close the window" that
means "terminate what I am playing."

A detached process was rejected for the opposite reason: it cannot be told whether it is
running, so the launcher could not stop offering to start a second copy.

**A game started outside the launcher is invisible to it.** That is honest rather than
incomplete: the launcher reports what it is in a position to know.

### `GameExited` fires on someone else's thread

It says so in its own doc comment. A view model subscribing to it **must** marshal through
`ViewModelBase.OnUiThread` (D32) — a binding updated off the UI thread is a crash that only
happens on a user's machine.

There is a related testing trap, recorded in `CLAUDE.md` §7 and worth repeating: the launcher
reads the clock at launch and again when the runtime reports the exit, so a test that advances
a fake clock between those two readings is racing the process. The fake clock therefore steps
on every *reading*, which makes the measured play duration independent of who wins the race.

---

## What is not implemented

- **No play-time tracking beyond the local row.** `LastPlayedAt` is recorded; nothing is sent
  to the server and there is no session history.
- **No overlay, no in-game anything.** The launcher starts a process and watches it exit.
- **No `LaunchMinimized`.** The field is in `UserSettings` and nothing reads it, which is why
  the Settings page does not offer it — an inert checkbox is worse than an absent one.
- **No per-game environment variables or compatibility layers** (Proton, Wine, Rosetta). The
  publisher names one executable per platform/architecture build.

## Related documents

- [downloads-and-installs.md](downloads-and-installs.md) — where the install row and its states come from
- [catalog-and-artwork.md](catalog-and-artwork.md) — the library page that offers Play
- [logging-and-local-state.md](logging-and-local-state.md) — the row that survives with no server
