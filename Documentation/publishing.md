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

### The gallery shows the pictures

Both lists hold `MediaCardViewModel`, the same type the game page uses for its screenshot strip:
a `GameMedia` and its decoded `Bitmap`, fetched through `IImageProvider` once the rows are on
screen. Before this the artwork tab was **alt text and nothing else**, so a publisher reordering
their own screenshots was reading the descriptions they had typed and remembering which picture
each one belonged to.

One type rather than one per screen, for the reason the server keeps `mayViewGame` in `domain/`:
two view models both meaning "a picture and its bitmap" is one shape maintained in two places.
The dashboard needs `Media` on it because its commands act on the record; the game page never
looks at it.

A picture that does not arrive leaves its row — with its description, its arrows and its delete
button — and an empty frame. `IImageLoader` reports every failure as null (an unreachable host, a
refused request, a response too large, bytes that are not an image), and a gallery that lost one
thumbnail is not a page that failed.

### A button to the game's own page

The dashboard shows the boxes a publisher filled in; only the game page shows what they add up
to — whether the banner is the right way round, whether the summary reads as a sentence, whether
the screenshots are in an order that means something. It is a navigation event to the shell
(D17), so this page does not know the game page exists, and "back" returns here because showing
the dashboard already records it as the list to come back to.

It is offered **for a draft too**, which is the case worth stating: `CatalogService::gameDetail`
serves a game to whoever may edit it whatever its visibility, and a publisher's own unreleased
page is a thing only they can read.

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

### Uploading a video

A video goes through the same route with `kind=video`, and differs in three numbers and one
refusal.

The numbers are `media.maxVideoBytes`, `media.maxVideosPerGame` and `media.videoContentTypes`,
all from `/capabilities`, and the dashboard shows **the video sentence** while video is the
chosen kind: a publisher choosing a trailer and reading the picture limit has been told the
wrong thing. The two galleries are also counted apart — a game at its video cap can still take
a screenshot — because the server counts them apart.

The refusal is the reason the client checks the size at all. An oversized picture is refused by
the API with a message naming the limit; an oversized video is refused by the web framework in
front of it, before any handler runs, as a bare `413` with no problem document. If the client
does not catch it, nothing can explain it.

**Video is offered only where the server said it stores one.** A deployment that names no video
limits cannot take one, and the kind is removed from the dropdown rather than left there to
produce refusals — the same rule that removes dead-end buttons elsewhere.

The picker offers `mp4` and `webm`, and the bytes are checked against the container rules before
anything travels: the ISO base media brand (so a HEIC is not offered as a trailer) and the WebM
DocType in the first 64 bytes (so Matroska is refused). As with pictures, a positive answer
vouches for nothing — the server decides, from the same bytes.

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

`DELETE /builds/{id}`, `DELETE /games/{id}/versions/{versionId}` and `DELETE /games/{id}`.
Deleting a build is how a publisher gets **quota back**: the server's collector reclaims the blobs nothing else references
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

### Deleting a whole game

The button is at the bottom of the **Details** tab — the one that owns the game's own fields —
and it arms the same prompt the other three use. Its sentence carries two things the others do
not, because nothing else on either side will say them:

- **Other people may have it in their library, and the server deletes it anyway.** A library
  entry is a bookmark, not a licence, and refusing while one exists would let a stranger
  permanently stop a publisher from withdrawing their own work. What those people already
  installed keeps working — an install is a directory on their machine that this server never
  knew about — but updating and verifying it stop, with a 404 that the launcher shows as "not
  available".
- **`draft` is the reversible thing they probably meant.** Somebody who wants a title to stop
  being visible does not want it destroyed, and the prompt says so rather than letting them find
  out afterwards.

When it succeeds the page **clears its selection** instead of letting the list fall through to
the next row: the four tabs below are all showing a game that no longer exists, and quietly
pointing a publisher at somebody's other title is worse than showing nothing.

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

### The version list: publish afterwards, and what is under each row (D65)

Each version is a `VersionRowViewModel` rather than a bare `GameVersion`, because the row has to
say two things the wire record cannot.

**Publish and Withdraw.** Until 2026-08-17 there was no server route that changed a version, so
a version created with "publish it now" unticked was a dead end: the only way past it was to
delete the version and its builds and upload everything again. `PATCH …/versions/{id}` fixed
that, and the row carries the button. It is a command rather than a checkbox because it is a
request the server can refuse, and a tick that springs back is a UI claiming state it does not
own — a refusal leaves the row saying what is actually true and puts the reason on the error
line. There is no confirmation prompt: D43's budget is for the things that cannot be undone, and
this one is undone by pressing the other button. Publishing is safe to repeat, because the
server keeps the original publication date.

**Which builds are under it.** The server sends versions and builds as two flat lists, so
joining them is this page's job. The summary shows each build's **name** where it has one and
falls back to its platform and architecture where it does not — the same information the builds
list below repeats, and the only thing that distinguishes two rows both reading
"0.3.0 beta published". The summary is refreshed **in place** after a build is published or
deleted, rather than the list being rebuilt, which would drop the selection the publisher is in
the middle of using.

A build is named on the publish form, and the field is cleared afterwards: the next build is a
different one, and inheriting the last label is how two builds end up called the same thing by
accident. The name is optional on both sides — an unnamed build is a valid build, and so is
every build published before the server grew the column.

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

- **No bulk actions.** Games, versions and builds are deleted one at a time, each with its own
  prompt; there is no "delete every draft" and no multi-select.
- **No draft/preview of what a build will look like** in Explore before publishing.
- **No upload queue or background publishing.** The dashboard publishes one build, in the
  foreground, from the page the publisher is on.
- **No Markdown preview** when writing a devlog entry. The launcher renders bodies as text
  everywhere, so a preview would be showing the same thing twice.
- **No retention policy.** Nothing deletes an old build on its own, on either side.

## Related documents

- [catalog-and-artwork.md](catalog-and-artwork.md) — artwork and the devlog, the other publisher surfaces
- [downloads-and-installs.md](downloads-and-installs.md) — the same content-addressed model, in reverse
- [authentication-and-session.md](authentication-and-session.md) — the permissions a publisher needs
- [architecture.md](architecture.md) — the tokenless capabilities client
