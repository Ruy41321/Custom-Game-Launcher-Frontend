# Self-update

How the launcher finds out that a newer launcher exists, what it checks before believing it, and
what it deliberately does not do with the answer.

Implemented in `Core/Updates/*`, `Core/Api/ILauncherReleaseApi.cs`,
`Infrastructure/Api/LauncherReleaseApiClient.cs`, `Infrastructure/Updates/LauncherUpdateDownloader.cs`
and the update line in `App/ViewModels/MainWindowViewModel.cs`.

The server half — the signed release surface this talks to — is described in the backend's
[launcher-releases.md](../../Custom-Game-Launcher-Backend/Documentation/launcher-releases.md),
whose last section states the five rules a client has to hold. This page is the answer to that
section.

---

## What is implemented, and what is not

**Implemented:** the check. The launcher asks the server for the newest release on its channel,
verifies the signature over the document as it arrived, refuses anything that is not strictly
newer, tells the person, and — on a press — fetches the archive and refuses bytes that do not
hash to the content address inside the signed document.

**Not implemented:** the swap. `GameLauncher.Updater` still moves no files. So what the launcher
says when the download succeeds is *where the verified archive is*, and nothing more. Replacing
the installation and restarting is the next piece of work; until it lands, installing the
downloaded version is something a person does by hand.

That split is deliberate rather than an accident of running out of time: the download is what
makes the content-address check real, and promising a restart the launcher cannot perform would
be the one kind of lie this feature cannot afford.

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

 a person presses "Download the update"
 ────────────────────────────────────────────────────────────────
 ILauncherUpdateDownloader.DownloadAsync()
   · stream to <user data>/updates/<version>/<sha256>.zip.part
   · refuse a response longer than the size the document declares
   · hash it; refuse bytes that are not the ones the document named
   · rename into place — nothing takes that name until it verifies

 GameLauncher.Updater  ->  not implemented
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

## Nothing happens on its own

A swap requires this process to exit, so a silent update is an application closing under the
hands of somebody using it. The launcher shows one line — "Version 0.4.0 of *AppName* is
available." — with the release notes, a button, and a way to put the line away for this run. The
next start asks again.

The check runs **once**, at start-up, after the crash-report sweep and the session restore. There
is no timer and no re-check: a release changes a few times a year, and a launcher that polls is a
launcher that has to decide what to do when the answer changes while somebody is playing.

---

## What is deliberately absent

- **The swap.** Stated first because it is what somebody will look for. `GameLauncher.Updater`
  has its command line designed (`--source`, `--target`, `--wait-for-pid`, `--relaunch`) and
  moves no files. The plan it will implement — rename the old installation to `previous/` rather
  than deleting it, put the new one in place, relaunch, watch that process for about thirty
  seconds, and restore on a non-zero exit inside that window — is recorded in `HANDOFF.md`.
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

`<user data>/updates/<version>/<sha256>.zip` — under the user's own directory, never beside the
executable, because the application directory is read-only after install and is the very thing an
update replaces. One version at a time: fetching a newer one sweeps the older directory away,
since three self-contained builds under somebody's data directory are two too many.

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

`tests/GameLauncher.Core.Tests/Updates/ReleaseSigningFixture.cs` holds two throwaway P-256 key
pairs and the golden `openssl` signature. Neither private key signs anything outside the test
assembly.

## Related documents

- [architecture.md](architecture.md) — the layering, the HTTP clients, and the start-up sequence
- [configuration-and-localization.md](configuration-and-localization.md) — what a fork edits
- [logging-and-local-state.md](logging-and-local-state.md) — what the launcher writes to disk
- [downloads-and-installs.md](downloads-and-installs.md) — the other content-addressed transfer
- the backend's [launcher-releases.md](../../Custom-Game-Launcher-Backend/Documentation/launcher-releases.md) — the contract
