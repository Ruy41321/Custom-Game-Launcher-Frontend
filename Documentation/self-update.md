# Self-update

How the launcher finds out that a newer launcher exists, what it checks before believing it, and
what it deliberately does not do with the answer.

Implemented in `Core/Updates/*`, `Core/Api/ILauncherReleaseApi.cs`,
`Infrastructure/Api/LauncherReleaseApiClient.cs`, `Infrastructure/Updates/LauncherUpdateDownloader.cs`,
`Infrastructure/Updates/UpdateInstaller.cs`, the whole of `GameLauncher.Updater`, and the update
line in `App/ViewModels/MainWindowViewModel.cs`.

The server half — the signed release surface this talks to — is described in the backend's
[launcher-releases.md](../../Custom-Game-Launcher-Backend/Documentation/launcher-releases.md),
whose last section states the five rules a client has to hold. This page is the answer to that
section.

---

## What is implemented

**All of it, since 2026-08-07.** The launcher asks the server for the newest release on its
channel, verifies the signature over the document as it arrived, refuses anything that is not
strictly newer, and tells the person. On a press it fetches the archive, refuses bytes that do
not hash to the content address inside the signed document, unpacks it, starts
`GameLauncher.Updater` and exits — and the updater replaces the installation and starts the new
launcher, putting the old one back if it fails.

**It is a button and never a timer.** A swap requires this process to exit, so a silent update
is an application closing under the hands of somebody using it. Nothing here happens on its own.

The one thing that is deliberately not covered is stated in [The declared
hole](#the-declared-hole) below, and it is not a gap in the implementation but a limit of what
a watching process can know.

---

## The one sentence that explains the whole design

**An automatic update is code a machine runs without anybody looking at it.** If the check is
weak, this is the worst attack surface in the project — a channel straight into every
installation that trusts it. Everything below follows from taking that seriously, and the
property it buys belongs to the server:

> Somebody who takes that server, its database and its disks can stop launchers from updating.
> They cannot make them update to anything.

The private key is not in either repository. It never was.

---

## The shape of it

```
 start-up, after the crash-report sweep and the session restore
 ────────────────────────────────────────────────────────────────
 IUpdateChecker.CheckAsync()
   · no usable key compiled in?          -> NotConfigured, nothing is asked
   · GET /api/v1/launcher/releases/latest?channel=&platform=&arch=   (no token)
   · verify the signature over the document's bytes, before parsing
   · parse; refuse a document this launcher cannot read
   · refuse a document that is not for this channel/platform/arch
   · refuse anything not strictly newer than the running version
   · refuse a url that is not http or https
                                         -> Available(document, url)

 a person presses "Update and restart"
 ────────────────────────────────────────────────────────────────
 ILauncherUpdateDownloader.DownloadAsync()
   · stream to <user data>/updates/<version>/<sha256>.zip.part
   · refuse a response longer than the size the document declares
   · hash it; refuse bytes that are not the ones the document named
   · rename into place — nothing takes that name until it verifies

 IUpdateInstaller.StartAsync()
   · unpack into <user data>/updates/<version>/staged/
   · refuse an entry name that would land outside it
   · copy <install>/updater/ to <user data>/updates/<version>/updater/
   · start it, then Shutdown() — the exit is what it is waiting for

 GameLauncher.Updater
   · wait for --wait-for-pid to be gone (refuse to touch anything if it is not)
   · rename <target> to <target>.previous          (atomic, same filesystem)
   · move <source> into <target>
   · start --relaunch, and watch it for ~30 seconds
   ·   non-zero exit inside the window -> restore .previous and start it
   ·   still alive, or exit 0          -> success, .previous goes away
```

---

## The five rules, and where each one lives

### 1. Verify the signature over the bytes as they arrived, before parsing

`UpdateChecker` takes `response.Document` — the route serves it as an opaque string — turns it
into UTF-8 bytes and hands those to `ReleaseSignature.Verify` **before** `ReleaseDocument.TryParse`
ever sees them. A document that is not the one that was published must never become the one that
gets installed.

Nothing here rebuilds a canonical form to compare against. That is the same discipline the
manifest follows (D19): reproducing a wire contract in a second language means two definitions of
one document, and the two drift. The server does that check once, at publish time, where an
operator can act on it.

The algorithm is **ECDSA P-256 with SHA-256, pinned** — never read out of whatever key is
configured. An algorithm taken from the key would let a deployment be given an RSA key that the
server verifies happily and the launcher cannot read at all: a launcher that stops updating for a
reason nothing reports. `System.Security.Cryptography.ECDsa` does all of it with **no new
package**, which is why the server chose P-256 over the otherwise-better Ed25519: .NET 9 has no
Ed25519 in its base class library, so it would have cost this repository a native binding across
four runtime identifiers.

### 2. Refuse anything not strictly newer

`ReleaseVersion.IsNewerThan` compares the three components **numerically**, because compared as
text `0.10.0` sorts before `0.9.0`. Equal is not newer.

This is the only defence against a correctly signed *old* document being replayed at a launcher,
which a signature cannot answer by itself. It is also why retiring a release on the server stands
a fleet still rather than rolling it backwards: the previous release becomes newest, and every
launcher declines it for not being newer than what it runs.

There is a sixth check the rules do not name and this client applies anyway: the document has to
say it is **for this channel, platform and architecture**. The signature vouches for what the
document *says*, so this is where signing the document instead of the artifact pays off — a
server holding real signed releases cannot hand a Windows launcher the Linux one.

### 3. Refuse bytes that do not hash to the content address

`LauncherUpdateDownloader` hashes what it wrote and refuses a mismatch, deleting the partial
file; nothing reaches the final name until it verifies. It also refuses a response longer than
the `size` in the signed document, so a host that keeps sending is stopped there rather than
after however many gigabytes it felt like sending.

The `url` is a convenience, not something to trust. An attacker who could rewrite it entirely
would still have to produce bytes hashing to a value somebody signed. It is still only followed
when it is `http` or `https` — the refusal `CachingImageLoader` applies to an artwork URL (D35),
for the same reason: the host is one the server named.

### 4. A failed check never stops the launcher from starting

`UpdateChecker.CheckAsync` catches everything but cancellation and answers `Undetermined` with a
line in the log. An unreachable server, a 404, a body that is not JSON, a signature that does not
verify, a clock that is wrong: all of them are silence in the window.

This is D50's reasoning about the crash-report uploader, unchanged — *a launcher that will not
open because it could not reach the update route would be the worst possible outcome of this
feature.*

A 404 is read as **up to date** rather than as a failure. From here, "no signing key on that
server", "nothing published at all" and "nothing published for this platform" are one situation:
there is nothing to update to. The server answers 404 to all three on purpose, so that a stranger
cannot learn which platforms a deployment builds for.

### 5. The public key is in the binary, not in `launcher.config.json`

`LauncherReleaseKey.PublicKeyBase64`, and it is **the one thing a fork changes in code**.

The reason is not that a file is easier to edit than a binary. It is that **the file the updater
overwrites must not be the file that authorizes the update**: `launcher.config.json` ships inside
the directory a swap replaces, so a key kept there would be replaced by whatever the update
brought with it. A constant compiled into the launcher lives inside the artifact whose
replacement is itself protected by the signature.

**Empty is the default, and it means this build checks for no updates at all** — rather than
checking and trusting whoever answers. That is the correct state for a fork that has not set up
signing, and it is the same answer the server arrives at from the other end: with no key
configured there, the release surface is off. See
[configuration-and-localization.md](configuration-and-localization.md).

---

## The channel is a shipped setting, not a user preference

`launcher.config.json` carries `updates.channel`, `stable` or `beta`.

Which stream a launcher follows is the choice of **whoever distributes it**. A player who could
move themselves onto a stream their distributor never published to would be a player who can
replace their own launcher with a build nobody meant them to have — and the launcher is the
program that has to still start in order to fix anything. There is no way back from a binary that
does not open.

An unrecognised channel is read as `stable` rather than failing validation. That is the opposite
of what `apiBaseUrl` does, and the difference is the consequence: a launcher pointed at nothing
is useless anyway, while a launcher that will not open because of a typo in a channel name is a
working launcher destroyed by a spelling mistake. The server refuses a channel it does not know
with 422, so passing the typo on would also spend one request per start to be told no.

---

## The swap

### The decision is a pure function of (exit code, elapsed time)

That sentence is the design, and everything else follows from it. There is no marker file, no
IPC and no watchdog outliving its purpose, because any of those would need two processes to
agree on a protocol while one of them is the thing under suspicion — and none of it could be
exercised without really replacing somebody's installation. `RelaunchWatch.Judge` takes an exit
code and an elapsed time and answers `Succeeded` or `Restore`, and the tests substitute the one
interface that produces those two numbers.

### The old installation is renamed, never deleted

`<install>` becomes `<install>.previous`, a sibling on the same filesystem, so putting it out of
the way is one atomic operation that needs no second copy of a self-contained build — the same
reasoning that keeps the download's staging tree inside its own root. And it is a rename rather
than a delete because a rollback with nothing to roll back to is not a rollback.

A `previous` left by an attempt that never resolved is **discarded** rather than kept, and that
is safe by proof rather than by assumption: the updater only runs because a launcher asked it
to, so whatever is in the installation directory right now works. Keeping the older copy would
only make the *next* rollback restore a version two updates behind.

### The launcher unpacks, and the updater stays small

`--source` wants a directory, and what is on disk is a zip. The launcher unpacks it, for two
reasons and the second is the real one:

- the updater is the only thing running when nothing can fix anything any more, so a bug in it
  has no other program to save it;
- **a zip can carry names that escape the directory it is opened into** — `../..`, an absolute
  path. The hash already proved the archive is the bytes somebody signed; it says nothing at all
  about the names inside. An archive that is correctly signed and hostile in its entry names is
  a real and different case, and the rules that refuse it — `ManifestPathRules` and `PathSafety`,
  behind `UpdateArchiveRules` — already live in Core for D24's reason and are already applied to
  every file of every build. A second implementation of a security rule in the updater would be
  a rule that eventually disagrees with itself.

### The updater is copied out of the directory it is about to replace

`GameLauncher.Updater` is published **inside** the installation, in `updater/`. On Windows a
running executable can be neither renamed nor deleted, so a helper left there makes the rename
fail for a reason nothing reports — an afternoon lost by whoever meets it first. **The launcher
copies it out**, into `<user data>/updates/<version>/updater/`, before starting it. Not the
system temporary directory: the user's data directory is known to be writable because everything
else the launcher keeps is already there, and the sweep that keeps one downloaded version at a
time removes this copy along with it. The helper cannot delete its own running image, so
somebody else has to, and that somebody already exists.

It is published **self-contained** with the launcher, trimmed and with invariant globalization,
because a machine running a self-contained launcher may have no .NET at all — an updater that
needed one would be missing at exactly the moment it is needed. That costs about 19 MB inside
every installation, and the trade is not close.

### The declared hole

**A launcher that starts, survives thirty seconds and then crashes is not rolled back.** From
inside the updater that is indistinguishable from somebody opening the new version and closing
it, and rolling *that* back would undo working updates. What covers it instead is the crash
reports, which do work, and `--rollback`:

```
GameLauncher.Updater --rollback --target <install dir> [--relaunch <exe>]
```

It stays a documented manual flag for as long as `<install>.previous` is on disk, which is until
the update after next.

There is a second consequence worth saying out loud: **nothing remembers that a release failed.**
A launcher that was rolled back is offered the same release again at the next start, because the
only state involved is which version is running. That is the honest shape of a design with no
memory, and giving it one would mean a file the update process writes about itself.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | The new version is in place |
| 2 | Refused: the command line, or what it named. **Nothing was changed** |
| 4 | The new launcher failed and the previous installation was restored |
| 5 | The swap failed and the previous installation could not be put back |

Four and five are separate because they mean two different things to somebody reading a log
afterwards, and only one of them leaves anybody with work to do.

---

## Nothing happens on its own

A swap requires this process to exit, so a silent update is an application closing under the
hands of somebody using it. The launcher shows one line — "Version 0.4.0 of *AppName* is
available." — with the release notes, an **Update and restart** button, and a way to put the
line away for this run. The next start asks again.

The check runs **once**, at start-up, after the crash-report sweep and the session restore. There
is no timer and no re-check: a release changes a few times a year, and a launcher that polls is a
launcher that has to decide what to do when the answer changes while somebody is playing.

---

## What is deliberately absent

- **Any memory of a failed release.** A rolled-back launcher is offered the same release again
  at the next start. Remembering would mean the update process writing a file about itself, and
  then deciding when that file stops applying — a server can publish a fixed artifact under the
  same version only by not doing so, since a version is only ever offered once.
- **Rolling back a launcher that fails later than thirty seconds**, which is the declared hole
  above. `--rollback` is the manual answer while the previous installation is on disk.
- **No minimum-version enforcement, and no field reserved for one.** A server that can tell a
  launcher it is too old to talk to is a remote kill switch, and a one-way door: one row would
  brick every installation. It belongs to the moment a wire contract actually breaks, with its
  own decision taken then. Reserving the field now would leave a later session something to be
  told is unused on purpose.
- **No delta updates.** A self-contained launcher changes almost every file between .NET builds,
  so blob negotiation would cost a round trip to learn that everything is needed. One archive.
- **No resume of the artifact download.** An update is one archive fetched once; a partial file
  from an interrupted attempt is thrown away rather than continued. Resuming buys nothing the
  hash does not already have to catch, and the blob fetcher's `Range` machinery exists for
  transfers that are gigabytes rather than tens of megabytes.
- **No background re-check, no notification, no "update on quit".**
- **No signature checking of anything else.** This is the only signed document the client
  verifies; build manifests are covered by their published hash instead (D19).

---

## Where a downloaded update waits

`<user data>/updates/<version>/` — under the user's own directory, never beside the executable,
because the application directory is read-only after install and is the very thing an update
replaces. It holds three things:

- `<sha256>.zip`, the verified archive;
- `staged/`, where it is unpacked, which becomes the updater's `--source`;
- `updater/`, the copy of the helper that has to outlive the directory it is replacing.

One version at a time: fetching a newer one sweeps the older directory away, which is also what
eventually removes the helper's copy, since it cannot delete its own running image. Three
self-contained builds under somebody's data directory are two too many.

See [logging-and-local-state.md](logging-and-local-state.md) for everything else on disk.

---

## Testing it

The rejection path is the point, and all of it is exercised without a network:

- a signature `openssl` really produced is accepted — the interop check, and the only one that
  would catch a disagreement between the tool a release is signed with and the runtime that
  checks it;
- a document changed by one byte, and one carrying a trailing newline, are refused;
- a **valid signature under another key** is refused, which is what proves the check looks at the
  key at all;
- an equal or older version, signed perfectly, is refused;
- an artifact whose bytes were changed after publication is refused at the hash, with the
  document and signature untouched;
- an unreachable server, a 404, a 422 and a body that is not JSON all leave the launcher running;
- an absent embedded key checks nothing and says nothing.

The swap is tested the same way, and the rejection path is again the point. `GameLauncher.Core.Tests`
covers the verdict (an exit of zero, a non-zero exit inside the window, one after it, and a
process still running), the command line round-tripping between the launcher that builds it and
the updater that parses it, and every archive entry name that is refused.
`GameLauncher.Updater.Tests` drives a **real installation directory** with the launcher
substituted: the happy path, the rollback with the old launcher started again, the declared
hole, a `--target` that does not exist, a launcher that is still running, a `previous` left by
an earlier attempt, and `--rollback` with nothing to roll back to. None of it needs a network or
a real launcher.

What no test reaches, and what was therefore driven by hand on Windows, is the thing itself: a
real self-contained publish replacing another one while the process that asked for it exits.
That is written up in `CLAUDE.md` §10.

`tests/GameLauncher.Core.Tests/Updates/ReleaseSigningFixture.cs` holds two throwaway P-256 key
pairs and the golden `openssl` signature. Neither private key signs anything outside the test
assembly.

## Related documents

- [architecture.md](architecture.md) — the layering, the HTTP clients, and the start-up sequence
- [configuration-and-localization.md](configuration-and-localization.md) — what a fork edits
- [logging-and-local-state.md](logging-and-local-state.md) — what the launcher writes to disk
- [downloads-and-installs.md](downloads-and-installs.md) — the other content-addressed transfer
- the backend's [launcher-releases.md](../../Custom-Game-Launcher-Backend/Documentation/launcher-releases.md) — the contract
