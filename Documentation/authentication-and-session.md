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

## Working offline (D29)

The rule: **an unreachable server keeps the session; a server that answers and refuses does
not.**

`RestoreAsync` at start-up:

| Stored session | Server | Outcome |
|---|---|---|
| absent | — | not signed in |
| fresh | not asked | restored |
| needs refresh | rotates | restored, rotated |
| needs refresh | unreachable (`ApiException.IsTransient`) | **restored as it stands**, logged |
| needs refresh | refuses | signed out |

Signing in is no more possible offline than refreshing is. A launcher that answered an
unreachable server with the sign-in screen would lock a player out of games already on their
disk, in exchange for nothing: nothing has said the session is spent — the question could not
even be asked. The stored session is kept, and the first call that reaches a server is the one
that rotates it.

A **refusal** is different and stays an error. An expired or revoked session has to be said out
loud, or the player sees a short library and no explanation.

The library completes the picture from the other side: it reads the local install store first
and unconditionally, so the offline list is the half of the answer that never needed a server,
and the network result is folded in when it arrives. See
[catalog-and-artwork.md](catalog-and-artwork.md).

**Known gap:** the game *detail* page does not work offline — it needs the catalog. Playing
happens from the library, which does. This is tracked as an open debt.

---

## Signing out

`SignOutAsync` clears local state **first and unconditionally**, then tries to tell the server.
A user who asks to sign out is signed out, whether or not there is a server to inform; a
failure to revoke is logged and the session expires on its own.

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
- `DevEmailVerificationToken` — **present only against a development server**, which has no
  mail transport yet and returns the token in the body. This is an open debt on the server
  side; when mail delivery lands, the field disappears there and this property comes out here.

`RequestPasswordResetAsync` reports success **whether or not the address exists**. The server
refuses to be an account-enumeration oracle, and the client must not undo that by presenting
the answer as confirmation that an account was found.

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

- **No account deletion or data export.** GDPR erasure is a server-side milestone that has not
  been built; there is no client surface for it and no placeholder.
- **No "remember me" / "stay signed out" distinction.** A restored session is restored; there
  is one behaviour.
- **No second factor.** The server has none, so the client has none.

## Related documents

- [architecture.md](architecture.md) — the four HTTP clients, and why they are four
- [catalog-and-artwork.md](catalog-and-artwork.md) — what the library shows when the server is gone
- [logging-and-local-state.md](logging-and-local-state.md) — where `session.json` lives
- [publishing.md](publishing.md) — the permissions a publisher's account needs
