# Guiding somebody through a deployment

**This page is addressed to an assistant, not to a person.** If you are a human reading it, the
document you want is [DISTRIBUTING.md](../DISTRIBUTING.md); this one only describes how to help
somebody else through it.

It exists because deploying this launcher is not hard but is easy to get subtly wrong, and the
two settings that matter most fail *silently* — the deployment looks finished and is not. What
follows is the shape of a session that ends with a working one.

---

## The one rule

**`DISTRIBUTING.md` is the source of truth. Do not restate it.**

Every command, value and explanation lives there. Your job is to walk somebody through it in
order, adapt it to what they actually have, verify each step before the next, and recognise the
failures that do not announce themselves. When you need to explain *why*, cite the section —
`§1.4`, `§3.2` — rather than paraphrasing it, so the person ends the session knowing where the
answer lives without you.

A second copy of these instructions is a second thing to keep in step with the code, and it will
lose.

---

## Before the first command

Five answers. Ask for them together, at the start; four of the five are expensive to change
later, and one of them cannot be changed at all.

1. **The product name.** It reaches the window, the installer, the Start-menu entry, the
   `.desktop` file and the user's data directory.
2. **The hostnames.** One for the API, one for the file server, and — if they want §1.8 — one
   for the registry. The registry's is the address every copy carries forever; it deserves a
   name they are willing to keep.
3. **Mail, or deliberately none.** Both are supported; *half* of it is refused at boot. §1.5.
4. **Self-update, or not.** If yes, the signing key is generated **before any build**, because
   the public half is compiled into the binary and a build made without it will never check for
   updates. §Step 2.
5. **A registry, or not.** Optional, and the reason to say yes is the day they outgrow the first
   server. §1.8.

Do not begin issuing commands until you have all five. A deployment that discovers at step nine
that it needed a decision at step two is a deployment that gets rebuilt.

---

## Things you must never do

- **Never ask for, receive, generate on their behalf, or echo the release signing key's private
  half.** It is the only secret in this project that must not be on the server, and the only one
  whose loss cannot be repaired. They generate it; you never see it. If it appears in the
  conversation anyway, say plainly that it should be considered compromised and regenerated
  before any launcher is distributed.
- **Never ask them to paste a secret** — a password, a token, an `.env`. When you need to check
  one, check its *shape*: its length, its prefix, whether it parses. §3.2 gives you the two facts
  that catch a mangled public key by sight.
- **Never run a destructive command on their machine or their server without saying, first,
  exactly what disappears.** Deletions in this project are real: there is no undo route, and the
  collector eventually reclaims the bytes.
- **Never declare something working because a build succeeded.** See *Verification*, below.

## Things that are safe to look at

The public halves of both keys, the health document, `/capabilities`, a signed registry answer,
a release document, TLS certificate details, DNS answers, and any log line that does not carry a
token. Say so when you ask for one — people are rightly cautious, and a request that explains
itself gets answered.

---

## The order, and the two places it is not negotiable

Follow `DISTRIBUTING.md`'s order. Two constraints inside it are worth stating out loud when you
reach them, because both are silent when broken:

**The signing key comes before any build.** Not before the *release* — before the first build
they hand to anybody. A launcher compiled with an empty key checks for no updates at all and
looks perfectly healthy doing it, and the only way to correct it afterwards is to have everyone
install by hand, which is the situation self-update exists to end.

**`TRUSTED_PROXIES` changes at the same moment the reverse proxy does.** Not before, which opens
a hole; not after, which silently collapses every per-address rate limit into one bucket shared
by every client. §1.4 has the reasoning and the procedure that proves it.

There is also an ordering that trips people up and is merely annoying rather than dangerous: if
mail is enabled, **TLS has to work before the first account registers**, because the verification
link points at the public hostname. Registering first produces a link that fails until the proxy
is up.

---

## Verification, which is the part that gets skipped

After every step, check the thing that would be wrong — and prefer a check that could actually
fail. The pattern to hold to:

- **Ask from outside**, not from the server. `curl` against `127.0.0.1` proves the container is
  up and nothing about DNS, the proxy, the certificate or the firewall.
- **Prefer a check that reveals the invisible.** `DISTRIBUTING.md` now carries three that exist
  for exactly that: the rate-limit probe in §1.4, the SMTP relay test in §1.5, and verifying a
  release signature yourself in §7 before uploading it.
- **A green test suite is not a working program.** This project's own history is unusually clear
  about it: several of the bugs that reached users were invisible to a full suite and obvious in
  the window. When the change is something a person looks at, ask them to look.
- **When something fails, read the message before theorising.** The server refuses to start on a
  bad configuration and *names the variable*. The publish command refuses a malformed release
  document and says which rule. Most failures here are self-describing, and the table at the end
  of `DISTRIBUTING.md` covers most of the rest.

---

## Failures that look like something else

These are the ones where the message points away from the cause. When a symptom matches, check
the table at the end of `DISTRIBUTING.md` before debugging from first principles.

| What they report | What it usually is |
| --- | --- |
| "Nobody can sign in any more" after adding TLS | Every rate-limit bucket is shared — §1.4 |
| "The update downloads and then fails" | Backslashes in the archive entry names, or no `updater/` in the build |
| "The server refuses my release document" | A trailing newline. `printf`, never `echo` |
| "It says my key is wrong" | The public half was mangled by a shell that pipes text instead of bytes |
| "The launcher can't reach the server" | `apiBaseUrl` without its trailing `/api/v1/` |
| "It installed but nothing changed" | The version in the document and the version in the binary disagree |
| "Everything is 403 after signing in" | An operator issued a temporary password; that is the point |

---

## How to end the session

Tell them, in a few lines: what is deployed and verified, what is **not** — including anything
deliberately deferred — and where the next thing they will want lives. Two specific pointers are
worth leaving every time, because they are the things people discover too late:

- **Backups** are the database and `/data/blobs`. Nothing in this project takes them, and the
  cost of not having them grows every day the deployment is used.
- **The private signing key** is unrecoverable and is not on the server. If they have not backed
  it up somewhere they would back up a password manager, they are one disk failure away from a
  fleet of launchers that can never be updated again.
