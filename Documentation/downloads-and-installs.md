# Downloads and installs

The largest module in the client, and the one where a mistake costs a user their game rather
than a screen. It covers getting a build onto this machine, keeping an interrupted attempt
cheap to resume, and never — under any interruption — presenting a directory as a build it is
not.

Implemented in `Core/Downloads/*`, `Core/Installs/*`, `Core/Api/IDownloadApi.cs`,
`Infrastructure/Downloads/{InstallationService,BlobFetcher,InstallPaths}.cs` and
`Infrastructure/Installs/SqliteInstallStore.cs`.

The server's half is in the backend's
[downloads-and-deltas.md](../../Custom-Game-Launcher-Backend/Documentation/downloads-and-deltas.md)
and [builds-and-uploads.md](../../Custom-Game-Launcher-Backend/Documentation/builds-and-uploads.md).

---

## The model, in one paragraph

The server stores every file of every build **content-addressed**: a blob keyed by its SHA-256,
and a manifest listing `(relative path → blob hash, size, executable bit)`. The client mirrors
that model exactly, which is what makes the two halves of an update agree without either
re-deriving the other's work.

**The client never diffs anything.** It asks.

---

## The five steps

### 1. Plan

`POST /builds/{id}/download` names the build currently installed. The server computes the
difference between the two manifests and answers with what to fetch, what is already correct,
what to delete, what it will cost, and signed URLs for the transfers.

Two properties of the plan matter to the client:

- **Only a *finished* install is ever named as the source** (D22). Planning against a directory
  that is half of one build and half of another would produce a plan that is correct about a
  state that does not exist.
- **The server falls back to a full download** when the delta stops being worth it, so the
  client does not have to decide that either.

`copyFrom` entries name content that only *moved*: the bytes are already on this disk, so they
are copied locally instead of fetched. The server restricts `copyFrom` to paths the update
keeps unchanged, which makes the plan **order-independent** — otherwise whether a copy worked
would depend on the order the client applied the plan in, which is a bug that appears on some
machines some of the time.

### 2. Check space

Before anything is written, which is the only moment the check is worth anything. Staging and
the install directory are probed **separately** and summed only when they share a volume —
adding them unconditionally would refuse installs that fit.

The failure carries both numbers (required and available), which is what lets the message say
how much is missing rather than only that something is.

### 3. Fetch

Blobs, not files. Two paths with identical content are one transfer.

- **Four concurrent transfers.** Enough to fill a domestic line; few enough that a small server
  is not being asked to serve one client from a dozen workers at once.
- **Into a content-addressed staging tree**, with the same two-level fan-out the server uses,
  under a directory derived from the build id. Deriving that directory name from a hash of the
  id (rather than validating the id and using it) means there is no shape to get wrong — and
  it is **stable across restarts**, which is what lets an interrupted transfer be found again
  by name instead of by remembering it.
- **HTTP `Range` to resume.** A blob is written to `.part`, and only when its bytes hash to the
  expected value is it renamed to its content address. **Nothing takes that name until its
  bytes hash to it.** A destination that already exists is therefore left alone: it can only
  have got there by matching.
- A server that ignores `Range` and answers 200 with the whole body is handled rather than
  trusted.

**410 is its own error code, apart from 403** (D25). nginx distinguishes an expired signature
from a bad one deliberately, and the client acts on the difference: an expiry is fixed by
asking for a fresh plan, and nothing about the account or the build has changed, whereas a bad
signature is a bug or a clock. Collapsing them would make the recoverable case look like the
unrecoverable one.

### 4. Apply

Files are copied out of staging and **hashed in the same pass** (D23). Then the plan's `remove`
paths go, and the directories they emptied with them.

Every manifest path is resolved against the install root and **refused if it escapes**
(D24, `PathSafety.ResolveInside`). The server validates these on ingestion and the database
constrains them again, and this is still worth a string comparison: a client that writes
wherever it is told is one compromised server away from writing into the user's startup folder.
The client's own check is the one that protects *this* machine, so it is not conditional on
trusting the server — that is the whole point of it.

### 5. Record

**The row flips to `Installed` last, and staging survives until it does** (D22).

---

## Why the row goes last

An install directory cannot be updated atomically without twice the disk. So the guarantee on
offer is a different one: **a game is never *presented* as installed until every file is
verified in place.**

`InstallState.Applying` is what a crash leaves behind. It is not a bug state — it is an
accurate description of a directory that is a mixture of two builds, and it is what stops the
next run computing a delta against a build that is only half there.

Keeping staging until the row changes means that crash is recovered by **redoing the apply, not
the download**. Deleting each staged blob as it was applied would make a crash during apply
cost the entire transfer again.

**What breaks if you reorder this:** writing the row first produces a directory that claims to
be a build it is not, and the next update then computes a delta *from* that claim. The install
is then wrong in a way nothing detects until the game fails to start.

---

## Verification

A separate, on-demand step: `POST /builds/{id}/verify` compares what is on disk against the
manifest, and an install the server calls broken is recorded as `Broken` — which is what turns
the next install into a **repair** rather than a delta from a build that is not really there.

**There is deliberately no full re-hash after an install** (D23). Every blob is already verified
before it is allowed to take its content address, so the only step a download check cannot
cover is the copy into the install directory — and those bytes are being read anyway, so
hashing them costs nothing. A full pass over a 50 GB install would only catch a disk that
changed its mind between two reads, and paying minutes for that on every install would teach
people to skip it. `VerifyAsync` offers it on demand, which is where that check belongs.

**Files the manifest never mentioned are reported but do not make an install broken.** An
install directory legitimately accumulates saves, configuration and logs; a launcher that
called an install corrupt because of a save file would train people to ignore the check.

---

## Install states

| State | Means | How you get there | How you leave |
|---|---|---|---|
| `Installed` | every file of the build is in place as far as the last operation knows | apply finished and the row was written | an update, an uninstall, a failed verify |
| `Applying` | an install or update is running | set before the first file is copied | the apply finishes, or a crash makes it `Broken` at next start |
| `Broken` | the directory is not the build it claims to be | a failed verify, or startup recovery finding `Applying` | reinstalling, which the server plans as a full download |

---

## Startup recovery (D34)

Called once at start-up. It does two things and starts nothing.

**Rows left `Applying` become `Broken`.** Nothing is applying while the launcher is closed, so
a row saying so is the mark of a process that died mid-apply. `Broken` is what the directory
actually is, and it is the state the game page already explains and offers to repair — so
recovery reuses a story the user can already follow instead of inventing one.

**Abandoned staging is swept by *age*, not emptied.** The threshold is seven days: long enough
that a download interrupted over a weekend still resumes for free, short enough that an
abandoned one does not sit on a disk forever. Clearing staging at every start would turn every
interrupted download into a full one — it would throw away exactly the thing that makes
resuming cheap.

**Recovery deliberately downloads nothing.** Fetching gigabytes because an application was
opened is not a decision to make on somebody's behalf. The repair is offered, not performed.

A related consequence, and not a bug: **an interrupted download does not restart by itself.**
Pressing Install again reuses the `.part` files still in staging, so nothing is re-downloaded.
If a self-resuming queue is ever wanted it has to be designed as one, not bolted onto recovery.

---

## Where a game is installed (D33)

`UserSettings.InstallDirectory` decides where the **next** game goes. Games already installed
keep their directory, and the Settings page says so rather than leaving it to be discovered.
Moving somebody's gigabytes because a preference changed is a different action from choosing
where the next install lands, and only one of them was asked for.

A configured directory that cannot be created falls back to the platform default. Refusing to
install would punish the user for a preference they can no longer act on, and there is always a
place that works.

Each game gets a directory of its own, named after its slug when the slug reduces to something
usable as a directory name, and after a hash of the game id otherwise.

---

## Progress (D26)

Progress is **bytes and a phase**, never a single percentage. A percentage cannot say that a
step is running but transferring nothing, so the phases that move no bytes — planning, checking
space, verifying — get an indeterminate bar rather than one that fills while nothing happens.

The rate comes from a **sliding window** over the last few seconds, because what a person wants
to know is how fast it is going *now*; an average since the start takes minutes to notice that
the line has come back. Both the speed and the estimate are **omitted until there is something
to base them on**: a countdown that says four hours and then twelve seconds is worse than no
countdown.

The fraction is clamped. A resumed transfer that the server then answers in full can report
more than it promised, and a bar that overshoots looks broken.

`Progress<T>` posts its callback to a captured `SynchronizationContext` — which in a test is
the thread pool. Both test projects have a synchronous `IProgress<T>` for this, and the view
model funnels every report through one property so the same path is exercised either way.

---

## The manifest is verified before it is parsed (D19)

`GET /builds/{id}/manifest` serves **exactly** the bytes `manifestSha256` covers, so verifying a
download is hashing the response as it arrived. The client does not re-canonicalise: rebuilding
a canonical form here would put a second definition of a wire contract in a second language,
and the two would drift.

Refusing **before parsing** is the part that matters. A document that is not the one that was
published must never become the one that gets installed, and by the time it has been parsed it
already describes the build.

`ApiErrorCode.Integrity` sits beside `Network` as a failure no server ever sends — a server
that knew would not have sent the response. Folding it into `Unknown` would tell the user
"something went wrong" for the one failure that retrying actually fixes.

---

## Local state

One row per game in SQLite, keyed by the game id, with WAL (D21). See
[logging-and-local-state.md](logging-and-local-state.md) for the schema, the versioning scheme
and why it is not a JSON file.

---

## Uninstalling

Deletes the install directory and its row, and reports freed bytes. Removing a game that is not
installed reports zero rather than failing.

---

## What is not implemented

- **No download queue.** One install at a time, driven from the page the user is on.
- **No automatic resume** — by design, see above.
- **No sub-file binary diffs.** Deltas are file-level, which is the server's model: a one-byte
  change inside a 2 GB `.pak` re-downloads that file.
- **No test covers nginx.** `BlobFetcher` is tested against a stub that speaks `Range` the way
  the real module does; the real file server is only ever verified by hand with the console
  project described in [architecture.md](architecture.md).

## Related documents

- [launching-games.md](launching-games.md) — what an installed row is then used for
- [logging-and-local-state.md](logging-and-local-state.md) — the SQLite store and the staging tree
- [publishing.md](publishing.md) — the same content-addressed model, in the other direction
- [architecture.md](architecture.md) — the file-server HTTP client and why it carries no token
