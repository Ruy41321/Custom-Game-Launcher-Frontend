# Publishing from the client

A publisher turns a directory into a build people can install without leaving the launcher.
This document covers the packaging rules, the resumable upload protocol as the client speaks
it, and how the client learns what this particular deployment will accept.

Implemented in `Core/Publishing/*`, `Core/Api/IPublishingApi.cs`,
`Infrastructure/Publishing/{DirectoryBuildPackager,BuildPublisher}.cs`,
`Infrastructure/Api/{PublishingApiClient,CapabilitiesApiClient,CachedServerCapabilityProvider}.cs`
and `App/ViewModels/DeveloperViewModel.cs`.

The server's half is in the backend's
[builds-and-uploads.md](../../Custom-Game-Launcher-Backend/Documentation/builds-and-uploads.md).

---

## Publishing is its own API interface (D30)

`IPublishingApi` is separate from `ICatalogApi`, and the separation is not organisational.

Every route on it needs a permission a player's account does not have — `game.publish`,
`build.upload`, `patchnote.write`. Putting them on a separate interface says that **in the type
system** rather than in a comment, and it means a player's launcher does not carry a client for
calls it can never make.

**This is a rule, not a preference: a write route goes on `IPublishingApi`, never on
`ICatalogApi`.** `ICatalogApi` is the read surface every account uses.

---

## The four steps

```
package  →  negotiate  →  upload  →  finalize
```

The separation between **negotiating** and **transferring** is the whole reason a second build
costs what actually changed.

### 1. Package

`DirectoryBuildPackager` reads the directory once: every file hashed, every path normalised to
`/` separators, the executable bit recorded, and every rule the server will apply checked
*here first*.

Reading the disk once is the point of `BuildPackage`: everything after this step comes from it,
so a large build is not walked again per phase.

`PublishFailure` names what went wrong specifically, because a packaging failure is local and
saying which rule was broken is the difference between fixing it and guessing:

| Reason | Means |
|---|---|
| `NothingToPublish` | a build must contain at least one file |
| `TooManyFiles` | more than `manifest.maxFiles` |
| `InvalidPath` | a name the manifest format cannot carry |
| `EntrypointMissing` | the chosen executable is not one of the files being published |
| `FileTooLarge` | a blob larger than `uploads.maxBlobBytes` |
| `Unreadable` | a file that is there but could not be read |

The path rules and the entrypoint check are applied **before a byte travels**. A name the
server will refuse is worth catching before gigabytes move, and an entrypoint that is not one
of the files is the same mistake with a more expensive ending — the server refuses the
manifest, which is the *last* call of the flow.

### 2. Negotiate

`POST /builds/{id}/blobs/missing` with every distinct blob. **Blobs, not files:** two paths of
a build with identical content are one declaration and one upload.

The answer is what actually has to travel. On a second build of the same game this is usually a
handful of files, and `PublishResult.BlobsAlreadyPresent` is the number that shows the
publisher why the update was cheap.

Note that negotiating an upload for content the server already holds is a **409, not a
session** — that is deduplication working, not a failure.

### 3. Upload

One blob at a time, in chunks, **at whatever offset the server says** (D31).

**The offset belongs to the server.** It is assigned by a conditional `UPDATE`, so a client that
disagrees is the one that is wrong. A refused chunk is answered by *asking* where the session
is — never by guessing — and `MaxOffsetCorrections` is two, because more than two corrections
in a row means a disagreement a retry will not fix.

**Sequential, not parallel.** The server bounds open sessions per user, and its staging disk is
that bound times the largest blob. Four uploads at once would be four times the scratch space
on a machine chosen for being cheap.

**The chunk size comes from the server** and is clamped at both ends:

```
chunk = min( capabilities.uploads.maxChunkBytes , MaxChunkBytes = 16 MiB )
        with FallbackChunkBytes = 4 MiB when the server does not say
```

The lower bound is the fallback: half the server's 8 MiB default, because guessing high is the
direction that fails — an oversized body is refused *before the handler runs*, so the error
does not mention size and reads like a routing problem. The upper bound is this client's own:
a remote number reaching `new byte[]` unchecked is how a misconfigured deployment becomes an
out-of-memory failure on somebody's laptop.

The chunk goes as `application/offset+octet-stream` with a mandatory `Upload-Offset` header —
the server requires it precisely so a client that lost its place cannot silently duplicate or
skip a range, which the hash would only catch after the whole file had been sent.

### 4. Finalize

`POST /builds/{id}/manifest` turns the build from `uploading` into `ready`.

File **sizes are deliberately not sent**: the server reads them back from the blobs it stored,
so a build cannot advertise a download size its content does not have. Files are sorted by path
with an ordinal comparison — a culture-aware sort would order `data/pak` and `Game.exe`
differently depending on the machine's locale.

---

## What the client is allowed to hard-code (D39, D40)

`GET /api/v1/capabilities` publishes this deployment's limits. It needs no token — nothing in
the document depends on who is asking, and `defaultQuotaBytes` is what a *new* account gets,
not what anybody has left — which is exactly what makes it readable at start-up, before a
session exists.

`CachedServerCapabilityProvider`:

- caches for **fifteen minutes**, not for the process, so a reconfigured server does not
  require everybody to restart;
- **never throws.** An unreachable server, or one older than the route, yields
  `ServerCapabilities.Fallback`. Refusing to publish because a document *about* publishing could
  not be read would be worse than the guessing it replaces;
- does not cache a failure.

### The numbers come from the server; the shape rules do not (D40)

`ManifestPathRules` no longer hard-codes `maxPathLength` or `maxFiles` — those are the server's
to state. The **structural** refusals stay client-side and unconditionally:

- no absolute path (including `C:relative`, which the leading-slash rule does not catch);
- no `..` or `.` segment, no empty segment;
- no backslash — `/` is the manifest separator;
- no control character.

They are what makes a path safe to resolve inside an install directory, which is **this
machine's** problem and not the server's (D24). The client would keep enforcing them against a
server that stopped, so fetching them as data would be fetching a path grammar over the wire
for rules whose whole purpose is to protect the client *from* the server.

That splits the old "rules are half copied" debt in two rather than pretending it is closed:
the part that could drift is gone, the part that must not is deliberate.

---

## Artwork and the devlog

Both are publisher surfaces and both live on `IPublishingApi`, never on `ICatalogApi`.

| Route | What it does |
|---|---|
| `POST /games/{id}/media?kind=&altText=&sortOrder=` | Upload one picture |
| `PATCH /media/{id}` | Alt text and position **only** |
| `DELETE /media/{id}` | Remove one picture |
| `POST /games/{id}/patch-notes` | Write an entry, published or as a draft |
| `PATCH /patch-notes/{id}` | Edit, publish or withdraw |
| `DELETE /patch-notes/{id}` | Remove an entry |

### The image is the body, and the client declares nothing about it

There is no multipart form: there is one file, and the fields that describe it are query
parameters, so the body stays byte-for-byte the thing that gets hashed and stored.

The upload sends `application/octet-stream`. **The server decides what an image is from its
leading bytes and never reads that header** (D28 of the server), because the answer becomes the
`Content-Type` of a public URL. Naming `image/png` would be a guess dressed as a fact, and an
invitation for a later reader to trust it.

The client still sniffs — `ImageFormats.LooksLikeAnImage`, shared with the artwork loader in
Core — but only to **refuse early**. A file that is obviously not PNG, JPEG or WebP is not worth
uploading to be told no. A positive answer is never treated as a guarantee. **SVG is refused on
both sides**, because it is a document format that can carry script rather than a picture.

### Every limit comes from the server

`MediaUploadRules` validates against `MediaCapabilities` and **holds no constant of its own**:
`media.maxBytes`, `maxScreenshotsPerGame` and `maxAltTextLength` all arrive from
`GET /api/v1/capabilities`. The rejection carries the limit that caused it, so the message can
quote the number.

The limits are shown on the page **before a file is chosen**, which is the whole point: a
publisher learns what this deployment accepts from the page rather than from a refusal after the
upload. That closes the last piece of the debt D39 opened.

The gallery cap applies to screenshots alone. A game has one cover, one banner and one logo, and
uploading another *is* how you replace it — counting those against the gallery would refuse a
legitimate replacement.

### A picture is never replaced in place

`PATCH /media/{id}` carries alt text and sort order and nothing else. There is no route that
swaps bytes under an existing id, because the id's whole meaning is the content it points at.
Changing a cover is uploading a new one and removing the old.

### Reordering: two arrows, not a drag

A swap writes **both** positions explicitly. Two screenshots left at the default order share a
sort order, so moving one "past" the other by arithmetic alone would leave them tied and the
swap invisible.

Two arrows rather than drag-and-drop, for three reasons: drag-and-drop in Avalonia can only be
verified by UI automation, which this repository deliberately does not have, while an arrow is a
command a test presses; a gallery is capped at a dozen entries, which is where dragging stops
paying; and a swap is two deterministic `PATCH`es where a drop could renumber the whole list,
with nothing to make those calls atomic.

### Publishing and withdrawing are one field

`published` does both, because a note that went out by mistake has to be able to come back.
**Re-publishing keeps the original date** — that date is when readers saw the entry, not when it
was last edited — and the page says so rather than leaving it to be discovered.

An entry may name a version **or none at all**, and it has a publication state of its own, so a
draft can be written before the build it talks about exists. Detaching an entry from its version
is sent as an **empty** `versionId`: null means "leave it alone" on the wire, which is the one
thing an absent field cannot express.

---

## Deleting, and being told what goes

`DELETE /builds/{id}` and `DELETE /games/{id}/versions/{versionId}`. Deleting a build is how a
publisher gets **quota back**: the server's collector reclaims the blobs nothing else references
and refunds the bytes, so this is not merely tidying up.

**Nothing is deleted on one click.** `PendingDeletion` holds the sentence that says *what
disappears* — "this version and its 3 builds", not "are you sure?" — and the call is made only
when a second button is pressed.

It is state on the view model rather than a dialog behind a service. A dialog would be a second
thing no test can drive, and D32 spends that budget on the file picker alone. As state, a test
arms the deletion, reads exactly what the user is being told, and confirms or cancels — so the
**wording** is covered too, which is the part that actually protects somebody's build.

The devlog's prompt goes further and names the reversible alternative: somebody who wants a post
to stop being visible almost always wants to withdraw it rather than delete it.

**A 404 on any of these is shown as "not available", never as "you do not have permission."**

---

## The dashboard

`DeveloperViewModel` shows the publisher's own games **including drafts** (via
`ICatalogApi.GetMyGamesAsync`), the versions and builds of the selected one, and the publish
form. Three child view models hang off it, shown as tabs:

| Child | Covers |
|---|---|
| `GameEditorViewModel` | the game's own fields → `UpdateGameAsync` |
| `GameMediaViewModel` | artwork: upload, describe, reorder, remove |
| `GameDevlogViewModel` | entries: write, edit, publish, withdraw, delete |

**Why children and not pages.** A publisher works on **one game at a time**, so the selected
game is the context all four share. Separate pages would mean selecting it three times, or
inventing a shared navigation state that D17 does not have. Tabs over child view models are
binding rather than navigation, so the one-way rule stays intact — and three smaller view models
give three readable test classes instead of one enormous one.

**The editor sends only what changed.** Null is absence on the wire, so a field left alone is a
field the server does not touch; opening the tab and pressing save cannot rewrite a description
with whatever a text box happened to hold. The form is then reseeded from the *response*, so a
value the server normalised — a trimmed title — is the one shown afterwards.

A saved edit is announced to the dashboard, which replaces the row in the list rather than
reloading it. The reload is suppressed while that happens: assigning `SelectedGame` normally
means "the publisher picked another game", and letting a save mean that would refetch the
detail, reload three children, and wipe the message the publisher has not read yet.

Two conveniences worth keeping:

- **Platform and architecture default to the machine doing the publishing**, which is what it
  is nearly always building for — and being wrong here produces a build nobody can install.
- **The entrypoint is guessed** when the chosen directory contains exactly one `.exe`. Typing
  the name again is a chance to get it wrong in a way that only surfaces after the upload.

Progress is a phase plus bytes, same shape as an install (D26). The phases that move no bytes —
packaging, negotiating, finalizing — report as indeterminate rather than as a bar filling while
nothing transfers. Cancelling is a `CancellationTokenSource` the view model owns; a cancelled
publish leaves the build in `uploading` and the session resumable.

### The pickers are the only untestable steps (D32)

`IFolderPicker` (a build directory) and `IFilePicker` (an image) exist because a system dialog
is the one part of these flows that cannot be driven from a test. **Everything else is exercised
end to end** — packaging, negotiation, chunking, offset corrections, manifest submission, every
validation rule, every deletion prompt — which is what those two interfaces buy.

`IFilePicker` returns **bytes rather than a path**, on purpose. A view model that received a
path would have to read the file, which is I/O in a view model and a second untestable step;
here the dialog and the read are one operation and one substitution replaces both. The read is
capped so a hostile or mistaken file cannot be pulled into memory unbounded; the real refusal
happens afterwards, against the server's announced limit.

Both resolve the window at call time rather than holding it: the shell is built before the
window exists, and a view model that captured a null top level would fail the first time
somebody clicked.

---

## Errors a publisher sees

A `PublishingException` is localized by its `Reason` and then followed by the specific message,
because knowing *which file* broke a rule is the actionable half. An `ApiException` goes through
`IApiErrorPresenter` like everything else.

**A 404 is "not available", never "you do not have permission."** The server answers 404 for a
game the caller may not edit, and the client must not translate that into a permissions claim.

---

## What is not implemented

- **No delete surface for games.** The server cannot delete a game either; it is designed
  together with account erasure, because it is the same question.
- **No draft/preview of what a build will look like** in Explore before publishing.
- **No upload queue or background publishing.** The dashboard publishes one build, in the
  foreground, from the page the publisher is on.
- **No thumbnails in the artwork tab.** The gallery is a list of descriptions and positions;
  showing the pictures would work — `IImageProvider` is already there — but the ordering and
  the alt text are what this screen is for.
- **No Markdown preview** when writing a devlog entry. The launcher renders bodies as text
  everywhere, so a preview would be showing the same thing twice.
- **No retention policy.** Nothing deletes an old build on its own, on either side.

## Related documents

- [catalog-and-artwork.md](catalog-and-artwork.md) — artwork and the devlog, the other publisher surfaces
- [downloads-and-installs.md](downloads-and-installs.md) — the same content-addressed model, in reverse
- [authentication-and-session.md](authentication-and-session.md) — the permissions a publisher needs
- [architecture.md](architecture.md) — the tokenless capabilities client
