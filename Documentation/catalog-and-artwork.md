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
`Page` and `PageCount` derived rather than sent. Explore itself no longer shows a page number;
`Total` is what it reads, because that is what says whether there is more.

**Drafts never appear.** That is enforced server-side; the client renders the list it is given
and does not filter again.

### The search box waits, and a new search cancels the old one (D46)

Typing a word used to be one request per letter. The waste was the smaller half of the problem:
nothing ordered the answers, so a slow reply for `orb` arriving after the reply for `orbital`
left the **wrong results on screen**.

So there are two mechanisms, and they do different jobs:

- **A 300 ms debounce.** Every keystroke rearms the timer, so only the pause at the end of a
  word fires a request. The delay comes from `TimeProvider`, not `Task.Delay`, which is what
  lets a test advance it by hand — a debounce a test really waits out is a slow test that
  eventually fails on a loaded machine rather than on a bug.
- **Cancellation.** Each load owns a `CancellationTokenSource` and cancels the one it replaces.
  The debounce makes the race unlikely; this makes it impossible.

**Pressing Enter searches at once** and drops the pending debounce: somebody who presses Enter
has already said they finished typing.

A cancelled search is **not** an error. `OperationCanceledException` from a superseded request
never reaches `ErrorMessage`, and the superseded request does not clear the busy indicator for
the search that replaced it — otherwise fast typing would look like a page that keeps breaking.

The debounce lives in the **view model**, not the view's code-behind: that is where Enter
arrives as well, and a rule in a code-behind is a rule no test can press.

### The list grows by scrolling (D51)

There is no Previous/Next any more. Reaching the bottom of the grid appends the next page, and
four rules make that behave:

- **One request at a time.** `LoadMoreCommand` refuses while anything is in flight. This is not
  a defensive extra: the scroll handler fires on every scroll event, so without it one flick of
  a wheel is several requests for the same page and several copies of it in the list.
- **The page number advances only on an answer that arrived.** A failed or superseded request
  leaves the next scroll asking for the same page rather than stepping over one nobody saw.
- **The end is a state, not a discovery.** `HasMore` comes from the server's own count, so
  reaching the bottom of a finished list asks for nothing — and an empty page ends the list too,
  which guards against a total that disagrees with what is being served.
- **A new search, or a new sort order, replaces the list.** That is the *only* path that empties
  it: an append that cleared first would make the grid flash and lose the place of somebody
  reading it. The scroll offset returns to the top when — and only when — the list is replaced.

The two entry points are separate for exactly that reason: `LoadAsync` replaces, `LoadMoreAsync`
appends, and no caller has to pass a flag saying which it meant.

The scrolling itself is an attached behaviour, `Views/InfiniteScroll`, rather than a code-behind
handler — a rule in a code-behind is a rule no test can press. It holds **no policy**: it fires
the command whenever the viewport is within 200px of the end, and whether that means anything is
the view model's decision. Only the view model's half is unit-tested; the geometry needs a
window and is verified by looking at one.

---

## The library

Two sources, and the order matters.

`ILibraryApi.GetLibraryAsync` says what the account **owns** — a server-side list. The SQLite
install store says what is **on this disk**. They are different questions, and the local answer
is the one that has to survive a reinstall of the launcher.

The library page reads the **install store first and unconditionally**, then folds in the
server's list when it arrives. That is what makes it work offline (D29): the local half never
needed a server, so an unreachable one produces a library with a banner rather than an empty
page.

Since D78 there is a third source, and it is what makes the offline page the *library*: every
successful answer is stored per account under the user's data directory (`ILibraryCache`), and
an unreachable server is answered from it. The install rows alone showed nothing at all to
somebody who had not downloaded a game yet, and hid every title owned and not installed here;
anything installed that the stored list does not mention is appended to it, so a game installed
since the last successful load is still on the page and still playable. A card carries both facts — what the account owns, and what this machine has — which is
what lets the same card offer Install, Update or Play — plus, since D69, whether what is
here is still the newest build.

Adding a game is idempotent; removing one that is not there is a genuine `NotFound`, because
there the two models really do disagree.

### Play waits for the update check (D69)

A card offers **Play** only when this machine's newest build for the game is the one that is
installed — the same rule the game page has had since D61, which the card could not follow
because it knew nothing about updates. Now it asks:

- **one request per *installed* game**, not per row. A library is everything an account was
  ever given; what is installed is bounded by the disk, and a card with nothing on this machine
  has no Play button to take away. `LibraryViewModel.CheckForUpdatesAsync` walks the cards and
  calls `ICatalogApi.GetGameAsync` for those in `Installed`;
- **after the list and the covers are on screen**, in the order the cards are shown, so nothing
  waits on it;
- and it compares `GameDetail.BuildFor(platform, architecture)` against `InstalledGame.BuildId`,
  which is exactly what the game page compares.

When there *is* an update, the button **disappears** and the sentence that replaces it is
`Detail.UpdateBeforePlaying` — the same sentence, because it is the same rule, and a greyed-out
button with no explanation is the same dead end with worse manners. `PlayAsync` refuses too:
the check can land between a press and the click that follows it.

**A question that could not be asked leaves Play where it was.** Offline no check is made at
all, and a single refused check leaves its card untouched and says nothing. Refusing to start a
game already on this disk because a server could not be reached is precisely what the offline
library exists to prevent (D29). What it costs is a card that can offer Play for the length of
one request and then withdraw it.

### Offline, the covers are there too (D45)

The install row keeps **`coverUrl`**, so the offline card is built with the same URL the online
one would carry and asks for its picture the same way. The disk cache is keyed by URL and needs
no server, so a cover seen once is on screen again with the backend stopped.

Two rules make that hold:

- **An update rewrites the cover**, because a publisher can change one.
- **An update never replaces a cover with nothing.** A response that arrived without a cover is
  not a publisher who removed it, and taking it as one would discard the only copy of the URL
  this machine has — precisely when there is no server left to ask again.

The column was added by **appending** a migration, so a launcher upgraded over an existing
database opens it and its rows take the empty default.

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

## Videos (D74, D75, D76)

A video is a fifth `MediaKind`, and everything about the *transport* is the artwork story
unchanged: the server stores it content-addressed on the same public root, the URL is absolute
and unsigned, and the launcher never sends a token to fetch it. Three things are different, and
each is a decision rather than a detail.

### It is never handed to an image decoder

`MediaCardViewModel.IsVideo` is the switch, and `LoadAsync` returns early for one — in that one
place rather than at every call site. The server stores the container and nothing else, so there
is no thumbnail to fetch; asking `IImageProvider` for one would spend a download to be told no.
A video card shows a frame and its description until somebody presses play.

`GameDetail.Videos` is its own list beside `Screenshots`, sorted the same way (`SortOrder`, then
`CreatedAt` as the tie-break) and for the same reason. `GameDetail.Artwork(kind)` answers `null`
for a video the way it does for a screenshot: those are galleries, and "the video" is a question
with no answer.

### The launcher asks whether this server does video at all

`/capabilities` carries `media.maxVideoBytes`, `media.maxVideosPerGame` and
`media.videoContentTypes`, and `MediaCapabilities.SupportsVideo` demands all three.

**The fallback is no video**, which is the opposite of `mail.enabled`'s (D72). The asymmetry is
the decision, not an oversight: mail is a feature every server older than its key still had,
while video did not exist before these keys existed. Reading silence as "yes" here would offer
an upload that cannot succeed.

### The size limit is enforced here, because the server's refusal for it says nothing

An oversized *picture* comes back as a 422 naming its limit. An oversized **video** never
reaches a handler: the web framework in front of the API refuses the body first, with a bare
`413` and **no problem document at all**. So `MediaUploadRules` checking `MaxVideoBytes` before
the upload is not an optimisation — it is the only thing that can produce a sentence.

`VideoFormats.LooksLikeAVideo` mirrors `ImageFormats` and D41's rule exactly: it refuses early,
it never vouches. It reads the ISO base media **brand** and not just the `ftyp` box, because
HEIC and AVIF are ISO base media files too; and it looks for the EBML `DocType` only in the
first 64 bytes, where the header is, so Matroska — which shares WebM's four magic bytes — is
refused.

### Playing it

`IVideoPlayback` is one player for the launcher, behind an interface for the reason
`IImageProvider` is: what is underneath needs a native library and a window to draw into.
`LibVLCSharp.Avalonia` renders into a `VideoView`, and LibVLC fetches the URL itself — which is
what makes seeking a Range request rather than a wait for the whole file.

Two properties are about *not* being there, and they are the interesting ones:

- **`IsAvailable` can be false, and that is ordinary.** There is no `VideoLAN.LibVLC.Linux`
  package — VideoLAN expects libvlc from the distribution — so on Linux playback depends on
  whether VLC is installed. The page says so and stays usable. Initialisation is lazy, and
  `ShowVideoUnavailable` short-circuits on `HasVideos`, so a game page with no trailer never
  loads ~100 MB of native library to answer a question nobody asked.
- **`Player` is null until something plays**, and the view holds a `ContentControl` rather than a
  hidden `VideoView`. `IsVisible="False"` does not take a control out of the visual tree, and a
  `NativeControlHost` creates its child window the moment it is attached — which crashed the
  launcher on every game page until `app.manifest` gained its `supportedOS` list (D76).

Everything except the picture is a state machine and is tested: a machine that cannot play is
never asked, a refusal leaves a sentence, and Stop, Back, loading another game and losing the
account all silence it. The picture is checked by hand.

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

## The publisher's side

Uploading a picture, describing it, reordering the gallery and writing a devlog entry are all
done from the developer dashboard — see [publishing.md](publishing.md), which covers the write
routes, the validation against the server's announced limits, and why the gallery is reordered
with two arrows rather than a drag.

Two rules from this document apply there in reverse:

- **The client does not decide what an image is.** It refuses what is obviously not PNG, JPEG
  or WebP to save a pointless upload, and never treats a positive answer as a guarantee. SVG is
  refused on both sides.
- **A picture is never replaced in place.** There is no route that swaps bytes under an existing
  id, so changing a cover is uploading a new one and removing the old.

---

## What is not implemented

Stated explicitly:

- **No full-screen screenshot viewer.** The gallery is a hero image plus a thumbnail strip.
- **No virtualisation.** Explore appends cards to a `WrapPanel` inside a `ScrollViewer`, so
  scrolling through a very large catalog keeps every card it has loaded alive. That is fine for
  the deployments this launcher targets and is the thing to change first if one grows.
- **No Markdown rendering.** Devlog bodies are shown as text, on both the player's page and the
  publisher's — rendering remote markup is a decision this feature does not need.
- **Offline, the *detail page* still does not work.** It needs the catalog. The library does,
  covers included.
- **A video has no poster frame, no seek bar and no volume control.** The card shows a
  description and a play button, and the player is LibVLC's surface with a Stop button under it.
  Extracting a first frame server-side is a decoding job the server deliberately does not do, and
  transport controls are a piece of UI, not a missing capability.
- **Nothing plays without libvlc**, and on Linux that means the distribution's VLC. Stated in
  D75; the page says so rather than pretending.

## Related documents

- [architecture.md](architecture.md) — why artwork gets a client of its own
- [publishing.md](publishing.md) — the publisher surfaces that do exist
- [downloads-and-installs.md](downloads-and-installs.md) — what the Install button starts
- [authentication-and-session.md](authentication-and-session.md) — why the library survives an offline start
