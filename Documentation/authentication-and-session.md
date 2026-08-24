# Authentication and the session

How the launcher signs in, how it keeps a session alive without ever replaying a spent token,
where that session is stored, and what happens when there is no server to ask.

Implemented in `Core/Authentication/{AuthSession,AuthenticationService,IAuthenticationService}.cs`,
`Core/Api/IAuthApi.cs`, `Infrastructure/Api/{AuthApiClient,BearerTokenHandler}.cs` and
`Infrastructure/Authentication/FileTokenStore.cs`.

The server's half — Argon2id, JWT claims, refresh families — is described in the backend's
[authentication.md](../../Custom-Game-Launcher-Backend/Documentation/authentication.md). This
document is only about what the client does with it.

---

## Two HTTP clients, and why the split is in the DI graph

The single most important structural fact here: **the client that talks to `/auth` does not
attach a bearer token, and the one that talks to everything else does** (D14).

Refreshing a session has to work *precisely* when the access token has expired. A single
client whose handler obtained a token before every request would call `POST /auth/refresh`
through that handler, which would call `GetAccessTokenAsync`, which would refresh, which is the
same call. The cycle is not hypothetical — it is the default outcome of the obvious design.

Two alternatives were rejected:

- **One client whose handler skips `/auth/*`.** The rule then lives in a path comparison *and*
  in the DI registration, and only one of them is ever checked.
- **Refreshing by hand at each call site.** Every new endpoint becomes a chance to forget.

Splitting the registration makes the cycle **impossible to write** rather than something a
reviewer has to notice. `ICapabilitiesApi` rides on the same tokenless shape for a related
reason (D39): the limits document is read before the launcher knows whether it can sign in at
all.

### `BearerTokenHandler` does not retry a 401 (D15)

The token is fetched at send time and `GetAccessTokenAsync` rotates it a minute before expiry.
So a 401 that still arrives means the session was **revoked server-side** — most likely its
whole family, because somebody replayed a refresh token. Replaying the request with the same
credentials would only be told no twice, and it would double every genuinely rejected call
while making a revoked session look like a slow one.

---

## The session

`AuthSession` holds the access token, the refresh token, the **absolute** instant the access
token expires, the account, and the flattened permission list.

The absolute instant is the part worth explaining. The server hands out `expiresIn` seconds;
storing that would be storing a duration whose origin is lost the moment the process exits, and
this session is written to disk. An instant survives a restart; a lifetime does not.

`Permissions` is used **only** to decide what the UI offers. Every permission is enforced again
server-side (D8), and the client never treats its own check as sufficient. The names are
constants rather than an enum because the server's permission table is data and can grow
without a client release.

---

## Rotation

`GetAccessTokenAsync` is the only way a token reaches a request, and it does three things:

1. If the token is not within `RefreshMargin` (one minute) of expiry, return it.
2. Otherwise take `_refreshLock`, **re-check** under the lock, and rotate.
3. Persist the rotated session **before** publishing it.

Each of the three is load-bearing.

**The margin** is a minute against a ~15-minute token: it costs nothing and comfortably covers
a slow round trip and a skewed client clock, so a token can never expire between the check and
the call that uses it.

**The lock** exists because two views loading at once would otherwise both rotate the session,
and the second rotation replays a refresh token the first one already spent. The server reads a
replayed refresh token as theft and answers by revoking **the entire family** — so an
unserialised refresh does not degrade, it signs the user out. The re-check inside the lock is
what makes the second caller take the first one's result instead of rotating again.

**Persisting before publishing** covers the crash window. The opposite order leaves the spent
token on disk, and presenting it on the next start is the same replay, with the same
consequence.

A non-transient failure during rotation clears the session and rethrows. A transient one does
not — see below.

---

## Working offline (D29, D77, D78)

The rule: **an unreachable server keeps the session; a server that answers and refuses does
not.**

That rule needs somewhere to write the answer down, or every caller pays a full timeout to
rediscover it — which is what made a launcher with a stopped backend unusable in practice
until 2026-08-18. `IServerReachability` is that place: one failure holds a circuit shut for
twenty seconds, the next request after the window is allowed through to find out, and its
outcome decides the next one. `ReachabilityHandler` reports it from every API client, and
`AuthenticationService` reads it before it spends a round trip on a rotation.

`RestoreAsync` at start-up:

| Stored session | Server | Outcome |
|---|---|---|
| absent | — | not signed in |
| fresh | not asked | restored |
| needs refresh | rotates | restored, rotated |
| needs refresh | unreachable (`ApiException.IsTransient`) | **restored as it stands**, logged |
| needs refresh | known unreachable (circuit open) | **restored as it stands**, with no request at all |
| needs refresh | refuses | signed out |

Signing in is no more possible offline than refreshing is. A launcher that answered an
unreachable server with the sign-in screen would lock a player out of games already on their
disk, in exchange for nothing: nothing has said the session is spent — the question could not
even be asked. The stored session is kept, and the first call that reaches a server is the one
that rotates it.

A **refusal** is different and stays an error. An expired or revoked session has to be said out
loud, or the player sees a short library and no explanation.

The library completes the picture from the other side: it reads the local install store first
and unconditionally, and — since D78 — the account's **last stored library** as well, so an
offline list is what the account owns rather than only what this disk holds. See
[catalog-and-artwork.md](catalog-and-artwork.md).

A launcher with **no** stored session cannot be signed into offline, because only a server
knows whether a password is right. The sign-in screen says so before a password is typed, with
a button that asks again, rather than accepting one and failing on a timeout.

It also offers a way past it (D79). **Continue offline** opens the library with no session:

- offered only when the server is unreachable **and** this computer has a game installed —
  otherwise it is a door to an empty room, or a way to skip a sign-in that would work;
- it creates **no session**. `IsSignedIn` stays false, so Explore, publishing and the
  account's settings stay hidden, and the header shows **Sign in** instead of Sign out, which
  is the only way out of the visit;
- the library it opens is **what is installed**, never the stored list of the last account to
  use this computer;
- the card's Details button is gone, because the game page needs the catalog.

**Known gap:** the game *detail* page does not work offline — it needs the catalog. Playing
happens from the library, which does. This is tracked as an open debt.

---

## Signing out

`SignOutAsync` clears local state **first and unconditionally**, then tries to tell the server.
A user who asks to sign out is signed out, whether or not there is a server to inform; a
failure to revoke is logged and the session expires on its own.

### What a page keeps across an account change (D70)

The shell owns its pages for the lifetime of the window, so nothing on them is thrown away when
they stop being shown. That is right while one person is signed in and wrong the moment
somebody else signs in: the dashboard went on showing the previous publisher's game, and the
library, the game page, Settings and the sign-in form all hold an account's state the same way.

The whole handler runs through `OnUiThread` first, and that is not a detail (D73):
`AuthenticationService` awaits its token store with `ConfigureAwait(false)`, so `SessionChanged`
is raised on the thread pool. Resetting a page writes bound properties and re-evaluates bound
commands, and Avalonia's thread check throws where nothing catches it — which does not produce
an error banner, it ends the process. Every sign-in closed the launcher between the day this
reset was added and the day somebody signed in through the window.

Every page implements **`IAccountScopedPage.ResetForAccountChange()`**, `MainWindowViewModel`
holds them in `Pages`, and the reset runs on a change of **account**:

```csharp
string? accountId = session?.User.Id;
if (!string.Equals(accountId, _accountId, StringComparison.Ordinal)) { … }
```

Two things about that shape are the whole decision.

**It is keyed on who, not on the event.** `SessionChanged` also fires on every token rotation,
several times an hour, with the same person behind it. A reset there would empty the library
under somebody's hands.

**The line is the account's data, not the page's state.** What an account's token fetched goes:
the library list, the search and its results, the game page, the publisher's games, selection
and forms, the password typed into the account-deletion box, the address left in the sign-in
form. What belongs to **this machine** stays: the install directory, the theme, the language,
the crash-report consent, and the install rows on disk — none of them changes because a
different person signed in on the same computer.

A test walks the shell's page properties by reflection and fails when one is not in `Pages`,
because a page missing from the list is a page that keeps somebody else's data.

---

## Erasing the account

The Settings page carries it, and it is the only irreversible thing the launcher can do to a
person's own data.

```
POST /api/v1/me/deletion    {"password": "...", "reason": "optional"}   -> 204
```

### Two presses, and the prompt is the safety

Nothing is sent on the first one. `AskToDeleteAccountCommand` arms a `PendingDeletion` — the
same shape the developer dashboard uses (D43) — and the sentence it carries is the point, not
the button beside it. The prompt says **what survives**, because that is the part nobody
expects: the server anonymises the account rather than deleting it, so anything a publisher
released stays online under a deleted name so that the people who installed it can still
update. Somebody who wanted their games gone has to delete them first, and being told that
afterwards is being told too late.

There are two wordings, chosen on whether the account holds `game.publish`. Asking the server
how many games it published would be a request made for the sake of a sentence, and a
publisher with none is told something harmlessly true.

The password is asked for because the server requires it: a token says who is asking, not that
the owner is at the keyboard. The box is emptied when the deletion is armed, cancelled or
succeeds — and deliberately **kept after a refusal**, because a mistyped password is the
likeliest way to arrive there and clearing it would mean typing it again to retry. Reopening
the page disarms anything left armed, so a confirmation cannot be walked into.

### Why it is not on `IAuthenticationService`

An erasure ends a session, so the obvious home for it is the service that owns sessions. It
cannot live there, and the reason is structural: the account route runs on the **authenticated**
client, whose `BearerTokenHandler` depends on `IAuthenticationService`. A session service that
needed the account client back would close a cycle the container refuses to build at all — the
same shape D14 keeps out of `/auth`, arriving from the other direction.

So `IAccountService` composes the two from outside: it calls the route, and **only if that
succeeds** it signs out. That order is the whole of it. Signing out is a local truth the server
is merely told about; an erasure is the server's answer, and forgetting the session after a
refusal would leave somebody signed out of an account that still exists, unable to read the
reason they were given. A DI test asserts the graph still builds, rather than leaving the
reasoning in a comment.

Nothing is shown when it succeeds: the sign-out raises a session change, and the shell answers
that by showing the sign-in screen, so a status message would be written onto a page nobody is
looking at.

### What the launcher does not clean up

**Installed games stay on disk**, and so do their rows in the local install store. That is
deliberate: the files belong to the machine rather than to the account, the server never knew
about them, and deleting somebody's games because they closed an account would be a second,
larger action they did not ask for. The library will simply be empty the next time an account
signs in on this machine.

---

## Where the session is stored (D16)

`%LOCALAPPDATA%\CustomGameLauncher\session.json` on Windows, the platform equivalent elsewhere
(see [logging-and-local-state.md](logging-and-local-state.md)). **In clear**, in a per-user
directory, with mode `0600` on Unix.

This is a deliberate decision, not an oversight, and the reasoning is about portability rather
than about the value of the credential:

- **DPAPI is Windows-only.** Choosing it leaves a fork to solve macOS and Linux itself.
- **A keyring means a libsecret dependency** that is absent on a headless or minimal Linux
  install, and Keychain is a second platform-specific integration on top.

Either choice covers one platform and abandons two. The file gets the strongest protection
available on *all three* instead, and the exposure is bounded by design: signing out revokes
the token, and replaying one the real client has already rotated revokes the family — so a
stolen session file is detectable and self-limiting rather than permanent.

The trade-off is recorded as an open debt so it stays a decision rather than becoming a
default.

---

## Registration and password reset

`RegisterAsync` returns `RegistrationResult`. Two fields deserve care:

- `EmailVerificationRequired` — the account cannot sign in until the address is verified.
- `VerificationEmailSent` — whether the message actually went out. The server creates the
  account whether or not its relay answered, so these are two different facts and the sign-in
  screen says two different things: check your inbox, or ask for the link again. It defaults
  to **false**, which is how a server too old to send the field is read — "ask again" is
  harmless against a server that did send, and "wait" is not.

`RequestPasswordResetAsync` reports success **whether or not the address exists**, and returns
nothing at all. The server refuses to be an account-enumeration oracle, and the client must not
undo that by presenting the answer as confirmation that an account was found.

## A server that sends no mail (D72)

Some deployments cannot set up an SMTP relay, and the server supports that as a configuration
rather than treating it as broken: `MAIL_TRANSPORT=none` switches off everything that needs a
message, and the routes that would send answer 404.

The launcher reads **`mail.enabled` from `GET /api/v1/capabilities`** and hides "forgotten your
password?" where it is false, putting a sentence in its place that names the administrator to
ask. `ReportMailFailure` still reads the 404 — that path is the backstop and is not going
anywhere — but a button whose only possible outcome is a failure is not an offer, and the screen
somebody is on when they press it is the screen they are already stuck on.

The fallback is **true**, which is the opposite of `crashReports.enabled` and deliberately so:
that one is permission to send something about the user, so silence means no. This one is a
feature somebody needs, and a server too old to carry the key does send mail — reading its
silence as "no mail" would hide the way back into an account on every deployment older than the
field. Guessing wrong in this direction costs one refusal the screen already explains; guessing
wrong in the other costs somebody their account.

### The way back in: a one-time password

An operator sets a temporary password through the server's loopback console
([the backend's administration.md](../../Custom-Game-Launcher-Backend/Documentation/administration.md))
and reads it out. From here it is an ordinary password that signs in — and a session that says
so:

```
"user": { …, "passwordChangeRequired": true }
```

The shell reads it from the **session**, not from a refusal. `AfterSignInAsync` is the single
method both the start-up restore and the sign-in button pass through, and it sends somebody to
`ChangePasswordViewModel` instead of to a library whose first request would come back
`403 password_change_required`. `CanNavigate` hides the tabs while it is in force, because every
one of them leads to a page that only answers no.

There is **no way out of that screen** while `IsForced`. That is the feature rather than an
oversight: the server refuses every route but the change, so a "later" button would lead to a
launcher where nothing works and nothing explains itself.

### The change itself

```
POST /api/v1/me/password    {currentPassword, newPassword}   -> 200 session
```

`IAccountApi.ChangePasswordAsync` is on the **authenticated** client, so — exactly as for the
erasure (D47) — it cannot live on `IAuthenticationService`, whose token handler that client
depends on. `AccountService` composes the two, and the ordering rule is the mirror image of the
erasure's: the new session is adopted **only if the change succeeded**, because a refusal leaves
the old password and the old session in force and forgetting either would sign somebody out for
mistyping their current password.

The answer is a whole session because the server has just revoked every session the account
held, this caller's included. `IAuthenticationService.AdoptAsync` exists for that one caller: it
persists the session and announces it, without pretending anybody signed in.

The page copies **none** of the password rules. The server owns them and names the rule it
refused (D64), so a deployment that lowers its minimum needs no client release. The single local
check is that the two new passwords match — the one mistake no server can catch, because only
one of the two is ever sent.

The screen is also the ordinary way to change a password, with a cancel button and without the
sentence at the top. One page rather than two: the flow is identical, and a second
implementation would be the one that goes untested.

## Asking for a link (D53)

The sign-in screen can ask the server to send either of the two messages. Both requests go
through `IAuthenticationService` — `RequestPasswordResetAsync` and
`ResendVerificationEmailAsync` — for the same reason `RegisterAsync` does: the routes are on
the tokenless client, they touch no session, and a service of their own would be a third
interface for two pass-throughs. There is no cycle to compose around here as there was for
erasure (D47), because nothing in this path needs a bearer token.

**The launcher triggers; the browser finishes.** There is still no screen for choosing a new
password or confirming an address, and that is a decision rather than a gap — see
[What is not implemented](#what-is-not-implemented).

Where the two affordances appear:

- **"Forgotten your password?"** sits under the password box in sign-in mode only, and asks
  for whatever address is in the email field. In registration mode it is noise.
- **"Send the confirmation link again"** appears only in the state where it means something,
  which has three entrances: a registration that required verification *whether or not the
  message went out* — one that left and was filtered leaves a person exactly where one that
  never left does — and **a sign-in refused with 403**.

That last one carries a fact worth stating plainly: `/auth/login` answers **403 for an
unconfirmed address and 403 for a disabled account**, with the same code and no way to tell
them apart. The client says the one that can be acted on (`Auth.ConfirmAddressFirst`) and
offers the resend; for a disabled account that button is harmless, because the resend route
answers identically and sends nothing. Before this, both cases read *"Your account is not
allowed to do that"*, which named neither.

### What the user is told, and what is deliberately not said

Every success sentence is **conditional** — "if that address belongs to an account…", "if that
address still needs confirming…". The server pays for identical answers so that the endpoint
cannot be used to discover who has an account here; a client that answered "we have sent you a
link" would give that away from the other side. A test asserts that the sentence for an address
that does not exist is the same as the one for an address that does.

Two refusals are not shown as ordinary errors:

- **429** goes on the **info** line, not the red one, with the wait named when `Retry-After`
  says something worth naming — two minutes or more, so no resx has to decline "1 minutes" in
  three languages. Three messages in fifteen minutes is deliberately tight, and the person most
  likely to reach that limit is the one whose message genuinely never arrived.
- **404**, which is what a deployment configured with `mail.transport: "none"` answers on both
  routes, says *this server does not send email* rather than "that is not available".

### The button guards itself, and no clock is involved

A successful request disarms its own button until the address in the field changes; a
**refused** one does not, because after a 429 pressing again once the wait is over is exactly
the right thing to do. There is no countdown and no timer. `Retry-After` is per **IP**, not per
address, so a disabled button would lock out a second person on the same network who never
pressed anything — and a restart would clear it anyway, which makes it a promise the launcher
cannot keep.

---

## Errors the user sees

Everything funnels through `IApiErrorPresenter` (D18), which maps an `ApiException` to one
localized sentence. It takes an override for the single case where one code means two things:
**on the sign-in form a 401 means the password was wrong**, not that a session aged out.

Two rules carried over from the server and repeated here because this is the surface where
they are easiest to break:

- A **404 is "not available"**, never "you do not have permission."
- A failed password reset says nothing about whether the address exists.

---

## What is not implemented

- **No data export.** Erasure exists; the other half of the same regulation does not, on either
  side. There is no route to ask the server for a copy of what it holds.
- **No deferred erasure.** The server's own schema has a `pending` state and the launcher could
  show a countdown, but the erasure is immediate by decision — so there is nothing to cancel and
  no screen for cancelling it.
- **No "remember me" / "stay signed out" distinction.** A restored session is restored; there
  is one behaviour.
- **No second factor.** The server has none, so the client has none.
- **No screen for confirming an address, and none for a reset *link*.** Both flows end in a
  browser, on a page the server serves, and `VerifyEmailAsync` / `ConfirmPasswordResetAsync`
  keep no caller. The launcher does have a password *change* screen — see above — but it is a
  different thing: it is reached with a session in hand rather than with a token out of an
  inbox. This is a decision, not a gap: the reset page is where the password rules
  are written, and a second set of fields here would be a second place for a product rule to
  live and drift — the reasoning of D40, on a surface where the client protects nothing by
  duplicating. It would also mean asking somebody to copy a token out of a mail client and
  paste it into a desktop application, which is a habit worth not teaching. The two methods
  stay on `IAuthApi` as the surface such a screen would use.
- **No countdown on a throttled request**, for the reasons above: `Retry-After` is per IP and
  a restart clears any local guard.

## Related documents

- [architecture.md](architecture.md) — the four HTTP clients, and why they are four
- [catalog-and-artwork.md](catalog-and-artwork.md) — what the library shows when the server is gone
- [logging-and-local-state.md](logging-and-local-state.md) — where `session.json` lives
- [publishing.md](publishing.md) — the permissions a publisher's account needs
