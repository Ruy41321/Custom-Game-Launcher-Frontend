# The catalog, artwork and the devlog

What the player sees: Explore, the library, the game page, the pictures on all three, and the
posts a publisher writes about a game.

Implemented in `Core/Api/ICatalogApi.cs`, `Core/Models/{Game,GameDetail,GameMedia,PatchNote,GameQuery}.cs`,
`Core/Media/IImageLoader.cs`, `Infrastructure/Media/CachingImageLoader.cs`,
`App/Services/IImageProvider.cs` and the `Explore`, `Library` and `GameDetail` view models.

The server's half is in the backend's
[catalog.md](../../Custom-Game-Launcher-Backend/Documentation/catalog.md) and
[artwork-and-devlog.md](../../Custom-Game-Launcher-Backend/Documentation/artwork-and-devlog.md).

---

## Explore

`ICatalogApi.ExploreAsync` takes a `GameQuery`: a case-insensitive title substring, a sort
order, a 1-based page and a page size. Every field maps to one query parameter, and **defaults
are omitted from the query string** so a plain listing is a bare URL — which keeps logs and
cache keys readable.

`GameSort` values the server does not recognise fall back to its default rather than failing,
so the enum can grow without breaking against an older deployment.

The response is `PagedResult<T>` — the server's `{ items, total, limit, offset }` envelope, with
`Page` and `PageCount` derived rather than sent.

**Drafts never appear.** That is enforced server-side; the client renders the list it is given
and does not filter again.

**Known gaps:** there is no infinite scrolling and no debounce on the search box. Every
keystroke that reaches the command is a request.

---

## The library

Two sources, and the order matters.

`ILibraryApi.GetLibraryAsync` says what the account **owns** — a server-side list. The SQLite
install store says what is **on this disk**. They are different questions, and the local answer
is the one that has to survive a reinstall of the launcher.

The library page reads the **install store first and unconditionally**, then folds in the
server's list when it arrives. That is what makes it work offline (D29): the local half never
needed a server, so an unreachable one produces a library with a banner rather than an empty
page. A card carries both facts — what the account owns, and what this machine has — which is
what lets the same card offer Install, Update or Play.

Adding a game is idempotent; removing one that is not there is a genuine `NotFound`, because
there the two models really do disagree.

**Known gap:** offline, the library shows no covers. The install row does not keep `coverUrl`,
so there is nothing to look up even though the disk cache is already keyed by URL. One extra
column on the SQLite store would close it.

---

## The game page

`GameDetail` carries the game, whether the calling account has it in its library, the visible
versions, the builds, and **`media`** — every picture the game has.

Two helpers do the thinking:

- **`Artwork(kind)`** returns the single cover, banner or logo, or null. Null is the ordinary
  case for a banner and a logo, not an error.
- **`BuildFor(platform, architecture)`** picks what this machine could install: the newest
  *ready* build for the platform, preferring the running architecture. Null when the publisher
  has shipped nothing for it — which the page states rather than hiding.

**Versions are not filtered client-side.** The server decides which are visible — a publisher
sees their unreleased ones, nobody else does — so re-filtering here would be a second copy of a
visibility rule.

**Known gap:** the detail page does not work offline. It needs the catalog. Playing happens
from the library, which does.

---

## Artwork

### A fourth HTTP client, with no token (D35)

A media URL is **public, unsigned, and on whatever host the API named**. Attaching the
launcher's bearer token would hand a credential to a host the server chose — the same reasoning
as D20 for the file server, and the registration is split for the same reason: to make it
impossible to write rather than something a reviewer has to notice.

Unlike the file-server client it keeps the **ordinary 30-second timeout**. A cover is small, and
one taking thirty seconds is one the page is better off without.

### The disk cache has no revalidation and no expiry

Because **artwork is content-addressed server-side**: the same picture is always the same URL,
and changing a game's cover means uploading a different one, which is a different URL. A cached
entry therefore **cannot be stale**.

An HTTP cache with revalidation would be one round trip per cover to learn what the URL already
guarantees. Only the **size cap** evicts: 128 MiB, trimmed to 80% of that by dropping the least
recently written entries, and only on a miss — a cache that is being read is a cache that is
not growing.

The cache file name is the **SHA-256 of the URL**, so nothing about a remote name reaches the
file system: a path is 64 hex characters whatever the server called the picture. Writes go
through a temporary name and a rename, so two launchers writing the same picture at once cannot
leave a half-written file that reads as a corrupt image.

### Three refusals, deliberately

1. **http and https only.** Any other scheme is not fetched.
2. **The read is capped at 16 MiB regardless of `Content-Length`.** A missing or dishonest
   header must not turn into an unbounded read.
3. **The format is decided by the leading bytes**, never by the declared `Content-Type` — PNG,
   JPEG and WebP by signature. This is the server's own D28 rule, applied again by the side
   about to hand those bytes to a decoder. **SVG is refused here as it is refused there**: it is
   a document format that can carry script rather than a picture.

### A picture that will not load is *no picture*, never an error (D36)

`IImageLoader.LoadAsync` returns **null** for an empty URL, a refused request, an unreachable
host, a response too large, or bytes that are not an image. `IImageProvider` **remembers the
null too**, so a cover the server does not have is not re-asked every time a grid scrolls past
it.

A missing cover is not something the user can act on, and an error banner over a page that
installs and plays perfectly well would train people to ignore banners. The card keeps its
frame and shows the title's first letter, so **the grid does not reflow while covers arrive**.

### Decoding sits in the App layer (D37)

`Bitmap` cannot be constructed without an initialised Avalonia. A view model that could not be
built without one is a view model that stops being tested — the same reasoning as `IFolderPicker`
in D32.

So Core hands out **bytes** and knows nothing about images, and `CachedImageProvider` in the App
layer decodes and memoises per URL for the life of the process. A cover seen in Explore is
therefore the same bitmap the library and the detail page show.

A test can substitute `IImageProvider` and assert **which URL was asked for**; it cannot assert
on a decoded picture, and trying to construct one is how a view-model test starts needing a UI
toolkit.

### `coverUrl` versus `media`

`coverUrl` rides on the **game**, resolved server-side by a correlated subquery, so an Explore
grid gets one picture per card without a second request per result. It is an empty string when
there is no cover rather than an absent key, so the client reads the field either way.

The full list lives on the **detail** response. A game detail is a fixed-size document and a
gallery is bounded at 12; the devlog is not, which is why it is paged separately.

### The gallery's order

`GameDetail.Screenshots` sorts by `SortOrder`, **with `CreatedAt` as the tie-break**. Without
the tie-break, two screenshots left at the default sort order would swap places between loads —
a UI that changes for no reason the user did.

---

## The devlog (D38)

`ICatalogApi.GetPatchNotesAsync` — its own paged route, newest first, default page size 10.
Below the server's own default of 20 on purpose: the page shows a few entries and a "more"
button, and a shorter first page reaches the screen sooner. The server clamps anything it
disagrees with.

**It is paged from the page itself.** It is an unbounded list next to a fixed-size one, so it
arrives *after* the page and grows on request. The page number is **derived from how many
entries are already shown**, which makes a reload and a "show older" the same call and makes
fetching the same page twice impossible.

**Its failures are its own.** `DevlogError` is a separate property from `ErrorMessage`, because
the devlog is the least important thing on the page: a game that can still be installed and
played must not be replaced by an apology about its blog. One error field would make a devlog
outage look like a broken page.

**Bodies are rendered as text.** The server stores Markdown and renders nothing; the launcher
shows it as written. Rendering remote Markdown is rendering remote markup — a dependency and a
decision with consequences, for a few paragraphs that do not need one.

A patch note is **not** a version's release notes: it may name a version or none at all, and it
has a publication state of its own. Drafts are filtered server-side, so a publisher sees their
own and nobody else does.

---

## What is not implemented

Stated explicitly:

- **The read side is complete; the write side is not.** No screen uploads an image, edits alt
  text, reorders a gallery or writes a devlog entry. The server has had four media routes and
  four patch-note routes since 2026-08-04, and a publisher does all of it with `curl` today.
  This is an open debt, tracked alongside the developer dashboard's other missing edits.
- **`media.maxBytes`, `maxScreenshotsPerGame` and `maxAltTextLength` are read from
  `/capabilities` and displayed nowhere**, because nothing uploads a picture yet.
- **No full-screen screenshot viewer.** The gallery is a hero image plus a thumbnail strip.
- **No search debounce and no infinite scroll** in Explore.

## Related documents

- [architecture.md](architecture.md) — why artwork gets a client of its own
- [publishing.md](publishing.md) — the publisher surfaces that do exist
- [downloads-and-installs.md](downloads-and-installs.md) — what the Install button starts
- [authentication-and-session.md](authentication-and-session.md) — why the library survives an offline start
