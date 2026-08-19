# Service discovery

The launcher ships with the address of its API in `launcher.config.json`. The day that address
changes, every installed copy stops working until a new build is distributed — which is a
release, for a string.

The service registry breaks that coupling. It is a separate service
([ServiceRegistry](https://github.com/Ruy41321/ServiceRegistry)) whose only job is to answer
"where does `game-launcher-api` live right now?", and whose own address is the one thing that
never changes. The launcher asks it at start-up, and the address in `launcher.config.json`
becomes the fallback rather than the truth.

**It is off unless configured**, and an unmodified build of this repository asks no registry
anything.

## Turning it on

Two things, in two places, and the split is deliberate.

**The URL goes in `launcher.config.json`:**

```json
"serviceRegistry": {
  "url": "https://registry.example.com/",
  "serviceKey": "game-launcher-api",
  "environment": "production"
}
```

**The verification key goes in the binary**, in
`src/GameLauncher.Core/Discovery/ServiceRegistryKey.cs`, next to `LauncherReleaseKey` and for
the same reason: `launcher.config.json` ships inside the directory a self-update replaces, so a
key kept there would be replaced by whatever the update brought with it. The registry prints
the string to put there:

```bash
docker compose run --rm registry keygen
```

The URL may live in the configuration file safely. Pointing a launcher at a hostile registry
gains an attacker nothing, because the answer it returns will not carry a signature this key
accepts — and a launcher with a URL and **no key asks nothing at all**, rather than asking and
believing whoever answers.

## What a registry answer is

An envelope, signed with ECDSA P-256 / SHA-256 — the same scheme, the same code
(`ReleaseSignature`) and the same reasoning as a signed release: `ECDsa` is in .NET's base
class library and Ed25519 is not, so the registry signs what this client can check without a
new dependency.

```json
{
  "payload": "<base64 JSON>",
  "signature": "<base64 ASN.1 DER>",
  "keyId": "b6514e5ab1841e1b",
  "algorithm": "ecdsa-p256-sha256"
}
```

The payload travels as base64 rather than as a nested object so that the client verifies the
exact bytes it received; re-serialising JSON before checking a signature over it is how two
implementations end up disagreeing about whitespace. `algorithm` is informational and is never
read: the scheme is pinned, because an algorithm taken from the message is an algorithm the
sender chooses.

`SignedEndpointReader` refuses, in this order: an empty compiled-in key, a body that is not an
envelope, a signature that does not verify, a payload that is not a claim, an address that is
not absolute `http`/`https`, and — the one that is easy to miss — **a perfectly valid claim
about another service or another environment**. A registry key signs every record it holds, so
a signature proves the registry said it and not that it answered the question asked.

## When the launcher asks

| Situation | What happens | Cost |
| --- | --- | --- |
| No registry configured, or no key compiled in | `ApiBaseUrl` from the file | nothing |
| Something cached | the cached address, used as it stands | no round trip |
| Nothing cached | the registry is asked, with a **3-second** deadline | up to 3s, once per machine |
| Nothing cached, registry unreachable | `ApiBaseUrl` from the file | the deadline |

After the window is up, `RefreshAsync` asks again regardless and stores what it learns. So a
backend that moves is picked up **for the next start**, not for the session in progress: every
typed `HttpClient` binds its base address when the container is built, and rebinding them all
at runtime would be a much larger change for a case a restart already covers.

The one consequence worth stating plainly: **the first launch after the backend moves fails to
reach it**, and the second works. The alternative — asking the registry on every start, before
the window — puts a network round trip in front of every launch to fix the rarest case, which
is the trade `D77`/`D78` spent a whole session removing from everything else.

## The cache

One file per service under the user's data directory, `registry/<key>.<environment>.json`,
holding the envelope **exactly as it arrived**, signature and all.

Storing the signed envelope rather than the address is the point of the design. That file is
writable by anything running as this user, so it is verified on the way in exactly as a
response from the network is; editing it changes nothing except that the launcher discards it
and asks the registry again. Deleting it costs one lookup at the next start.

A refresh does not overwrite a stored claim with an **older** one. A genuine, correctly signed
answer from before the address moved is a replay, and keeping it would move every launcher that
received it back to the old backend.

## What this deliberately does not do

- **It does not re-resolve mid-session.** See above.
- **It does not fetch the verification key from the registry.** An attacker who can impersonate
  the registry would hand over their own. `GET /v1/pubkey` exists on the service for operators
  checking what is deployed, and the launcher never calls it.
- **It says nothing to the user.** A registry that cannot be reached is not something anybody
  can act on, and the launcher they are looking at is working — on the address it already had.
  Every outcome is a log line, and the effective address is logged on every start:

  ```text
  [INF] Using API address http://localhost:8080/api/v1/, from "Registry".
  ```

- **It resolves one service.** The registry can hold many; this launcher asks about one, and a
  second would be a second `serviceKey` in the configuration rather than new machinery.
