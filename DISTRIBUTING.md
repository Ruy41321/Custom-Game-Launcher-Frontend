# Running your own Custom Game Launcher

This is the guide for shipping **your** launcher: your name on it, your server, your games,
your testers. It assumes you can use a terminal and rent a Linux machine. It does not assume you
write C# or C++, and you will not have to change either — except for **two lines**, which have a
section of their own and a reason.

It covers the whole cycle in the order you actually have to do it in. The order matters more
than it looks: two of these steps are hard to undo if taken late.

> Working through it once takes an afternoon. Most of that is waiting for builds.

---

## What you are signing up for

| You provide | Why |
|---|---|
| A Linux machine with Docker | The server is a `docker compose` stack. A small VPS is enough |
| A domain name, and TLS on it | The launcher talks to your server over HTTPS |
| A way to send email — **or not** | Verification and password-reset links. A transactional provider is the short path; §1.5 has what changes if you skip it, which is supported and costs you one manual job |
| A safe place for one private key | It signs your launcher updates. Losing it cannot be repaired |
| A way to give people the first download | There is no installer and no app store. A link is fine |

**Targets: Windows and Linux.** macOS is not a supported target.

---

## Step 0 — Decide three things before you start

**Your product name.** It appears in the window, in the emails — if you send any — and in the
user's data directory.

**Your hostname.** Say `games.example.com`. The launcher is compiled with the API address in a
configuration file, so changing it later means shipping a new launcher to everybody — possible,
but not free.

**Whether your launcher will update itself.** If yes, generate the signing key in Step 2 *before*
building anything, because the public half is compiled into the launcher and a build made without
it will never check for updates. This is the one step that is genuinely painful to take late.

---

## Step 1 — Put the server up

### 1.1 Get it and configure it

```bash
git clone https://github.com/Ruy41321/Custom-Game-Launcher-Backend && cd Custom-Game-Launcher-Backend && cp .env.example .env
```

Generate three real secrets — one each for `JWT_SECRET`, `FILE_SECURE_LINK_SECRET` and
`DB_PASSWORD`:

```bash
openssl rand -base64 48
```

Then edit `.env`:

```bash
LAUNCHER_ENV=production
DB_PASSWORD=<one of the three>
JWT_SECRET=<another>
FILE_SECURE_LINK_SECRET=<the third>
```

> **The placeholders in this repository do not work outside development, by design.** The server
> refuses to start on a secret containing `dev-insecure` — by name, not only by length, because
> the one in the compose file is thirty-eight characters long and published on GitHub. A
> deployment that forgets its `.env` fails loudly instead of signing every token with a value
> anybody can read.

### 1.2 Start it — and do not let it pick up the development overrides

`docker compose up` **automatically** loads `docker-compose.override.yml`, which is the
development file: it adds a mail catcher that swallows every message you think you are sending,
publishes the database port, and turns off address verification. On a server, always be explicit:

```bash
docker compose -f docker-compose.yml up -d --build
```

The first build compiles the API from source and takes a while. Migrations run at start-up.

```bash
curl -s http://localhost:8080/api/v1/health
```

### 1.3 TLS, which is not optional and is not included

The stack terminates nothing: the API publishes port 8080 in the clear, and the bundled nginx
serves files only — **it does not sit in front of the API**. You add a reverse proxy. Caddy is
the short path because it gets certificates by itself:

```
games.example.com { reverse_proxy 127.0.0.1:8080 }
files.example.com { reverse_proxy 127.0.0.1:8081 }
```

Then stop publishing the two ports to the world. You do not have to edit the compose file — the
host side of each mapping comes from `.env`:

```bash
API_HOST_PORT=127.0.0.1:8080
FILESERVER_HOST_PORT=127.0.0.1:8081
```

And point the public URLs at your names:

```bash
FILE_PUBLIC_BASE_URL=https://files.example.com/files
MEDIA_PUBLIC_BASE_URL=https://files.example.com/media
LAUNCHER_PUBLIC_BASE_URL=https://files.example.com/launcher
```

These are safe to change at any time: a download signature covers the **path** only, never the
host, so nothing already issued is invalidated.

### 1.4 The setting that fails silently

```bash
TRUSTED_PROXIES=["172.16.0.0/12"]
HSTS_ENABLED=true
```

**Set `TRUSTED_PROXIES` the moment a proxy goes in front, and not before.** Behind a terminator
every request arrives from the proxy, so without it **every per-address rate-limit bucket
collapses into one shared by everybody** — one launcher stuck in a crash loop locks all your
testers out of signing in, and nothing reports why. `HSTS_ENABLED` is harmless before TLS
(browsers ignore it over plain HTTP) but is listed here because the two belong together.

**Prove it, because nothing else will.** There is no header, no log line and no page that says
whether this is right; the only evidence is the limiter's own behaviour. Exhaust it from one
machine — a wrong password against an address that does not exist, so nothing real is touched:

```bash
for i in $(seq 1 12); do curl -s -o /dev/null -w "%{http_code} " -X POST https://games.example.com/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"nobody@example.invalid","password":"wrong"}'; done; echo
```

You should see ten `401` and then `429`. Now, **within the same minute**, make the same request
from a second machine — the server itself over its public name will do. `401` means the two
addresses have buckets of their own and this is configured correctly. `429` means everybody
shares one, and one launcher in a crash loop can lock out every tester you have.

Worth re-running after anything changes in front of the API.

### 1.5 Email — or deliberately none

There are two supported answers here and you should pick one on purpose. **With SMTP**, people
confirm their own address and reset their own password. **Without it**, they cannot, and you
become the recovery procedure — which is a real cost and a small one for a launcher with a
dozen testers.

What is not supported is *half* of it: the server **refuses to start** outside development if
it is configured to send and cannot, and names the variable it is missing, because a deployment
where nobody can finish registering is worse than one that did not come up.

#### With SMTP

```bash
MAIL_TRANSPORT=smtp
SMTP_HOST=smtp.yourprovider.example
SMTP_PORT=587
SMTP_USERNAME=...
SMTP_PASSWORD=...
MAIL_FROM_ADDRESS=launcher@example.com
MAIL_LINK_BASE_URL=https://games.example.com
```

`MAIL_LINK_BASE_URL` is the origin the links in your emails are built from. It is deliberately
**not** derived from the request, because a `Host` header is chosen by whoever is calling and
building a link from it would let a stranger pick the domain that appears in somebody else's
inbox. If your origin changes, change it here too.

**Test the relay before anything depends on it.** This talks to it exactly as the server will —
same host, port, TLS and credentials — without involving the API, so a failure names itself
instead of arriving as a registration nobody can finish:

```bash
python3 - <<'EOF'
import smtplib, ssl
from email.message import EmailMessage
env = dict(l.strip().split('=', 1) for l in open('.env')
           if l.strip() and not l.startswith('#') and '=' in l)
m = EmailMessage()
m['From'] = env['MAIL_FROM_ADDRESS']; m['To'] = 'you@example.com'
m['Subject'] = 'relay test'; m.set_content('It works.')
s = smtplib.SMTP_SSL(env['SMTP_HOST'], int(env['SMTP_PORT']), timeout=20)
s.login(env['SMTP_USERNAME'], env['SMTP_PASSWORD']); s.send_message(m); s.quit()
print('SENT')
EOF
```

Use `SMTP_SSL` for an implicit-TLS port such as 465 and `SMTP` plus `starttls()` for 587, which
is the same distinction `SMTP_SECURITY` makes. A connection that hangs and then times out is
usually the provider **blocking outbound SMTP** — many do, against spam, and they will lift it
for a machine on request. `SMTPAuthenticationError` almost always means `SMTP_USERNAME` needs to
be the full address rather than the part before the `@`.

> **Whether the mail arrives is a DNS problem, not a code one.** SPF, DKIM and a reverse record
> decide whether a verification link lands in an inbox or in spam. A transactional provider
> handles all three for you; a VPS sending directly on port 25 handles none. Expect the first
> messages from a brand-new domain to be treated harshly whatever you do — especially the ones
> carrying a link, which is every message this server sends.

#### If you have no SMTP server, and do not want one

This is supported. It is not a degraded mode you have to work around — the server knows it
cannot send and behaves accordingly, and so does the launcher.

```bash
MAIL_TRANSPORT=none
REQUIRE_VERIFIED_EMAIL=false
```

**Both lines, or the server will not start.** With no transport there is nothing to deliver a
verification link with, so an account that has to confirm its address is an account that can
never sign in — the server refuses that combination at boot rather than letting you discover it
at your first registration. It names the variable to change.

You can leave `SMTP_*` and `MAIL_FROM_ADDRESS` empty; nothing reads them.

What changes:

| | With mail | `MAIL_TRANSPORT=none` |
|---|---|---|
| Confirming an address | A link, by email | Not required; accounts work immediately |
| Forgetting a password | "Forgotten your password?" on the sign-in screen sends a link | **The button is not there.** The launcher shows *"ask an administrator for a temporary one"* instead |
| Getting back in | The user does it themselves | **You do it**, from the console — see below |

The launcher does not guess any of this. It reads `mail.enabled` from
`GET /api/v1/capabilities` at start-up, so a launcher already in somebody's hands adapts the
moment you change the setting and restart the API — you do not have to ship a new build.

```bash
curl -s https://games.example.com/api/v1/capabilities | grep -o '"mail":[^}]*}'
#   {"mail":{"enabled":false}}
```

#### Handing somebody a password, when they cannot reset it themselves

In the console (§1.6), open **Users**, find the account, press **Edit**, then
**Set a temporary password…**. It asks once — because it ends every session that account has
open and cancels any reset link it was sent — and then shows the new password **once**:

```
Temporary password for player@example.com
     pwxze-dcpd7-kdj9x
```

Read it out, paste it into your chat, whatever you trust. Then close the row and it is gone:
it is not stored anywhere, the console cannot fetch it again, and neither can you. If you lose
it, set another one — that is the whole recovery procedure.

Three things are true of that account until they choose their own password:

- **it can do nothing else.** Every route answers *"this account is using a temporary password"*.
  The launcher does not let them past the screen that changes it — no tabs, no cancel;
- **their old password is dead**, and so is every device they were signed in on;
- **they cannot keep the one you gave them.** Typing it in as the new password is refused, which
  is what stops a "temporary" password from quietly becoming permanent.

The audit trail records that you did it and to whom (`user.password.temporary_set`), and
deliberately **not** what the password was.

> **You cannot do this to your own account.** The flag it sets is honoured on the public API,
> and the console has no password-change screen of its own — so you would lock yourself out of
> the surface you administer. Ask another operator, or use the launcher.

Everything here works with mail switched on too. The difference is only whether somebody has a
self-service way to avoid asking you.

### 1.6 The first operator

Granting a role through the console needs a permission nobody has on a fresh deployment, so the
first one comes from the shell — which is already the strongest authority on that machine:

```bash
docker compose exec api /app/launcher-api --grant-role you@example.com admin
```

The console is bound to loopback and never exposed. Reach it through a tunnel:

```bash
ssh -L 9090:127.0.0.1:9090 user@your-server
```

Then open <http://localhost:9090/admin>. Set `ADMIN_ENABLED=true` in `.env` first.

### 1.7 Before you forget

- **Firewall**: only 80 and 443 need to be reachable. The database publishes no port; keep it
  that way.
- **Backups**: the database and `/data/blobs`. Artwork is content-addressed and re-uploadable;
  blobs are not.
- **`.env` is mode 0600** and never in version control.
- **Logs grow.** `/var/log/launcher` rotates by size; the volume does not.

### 1.8 The address your launchers carry forever — and how not to be stuck with it

Everything above assumes your API stays where you first put it. `apiBaseUrl` is compiled into
`launcher.config.json` and ships inside every copy you hand out, so the day you move the backend
— a bigger machine, a different provider, a domain you would rather use — **every installation
already out there is broken until somebody installs a new one.** Which is the situation
self-update exists to end, arriving from the other side.

[ServiceRegistry](https://github.com/Ruy41321/ServiceRegistry) is the answer: a very small
service whose only job is to tell a launcher where the API is *right now*. The launcher asks it
at start-up, and you change the answer from a browser panel in a few seconds.

It is optional. A build with no registry configured behaves exactly as this page has described
so far.

```bash
git clone https://github.com/Ruy41321/ServiceRegistry && cd ServiceRegistry && cp .env.example .env
```

Three secrets, and the first one is a key pair of its own:

```bash
docker compose run --rm registry keygen    # REGISTRY_SIGNING_KEY, and the public half
docker compose run --rm registry hashpw    # ADMIN_PASSWORD_HASH
openssl rand -base64 48                    # JWT_SECRET
```

**This private key belongs on this machine**, unlike the release key of Step 2 — it is what the
registry signs its answers with, so it lives where the signing happens. The public half is the
second of the two lines a fork changes in code (§3.2). Quote the Argon2id hash in `.env`:
it contains `$`, which compose would otherwise read as a variable.

Running both stacks on one host needs the ports moved, because the registry defaults to the
API's:

```bash
HOST_PUBLIC_PORT=127.0.0.1:8082
HOST_ADMIN_PORT=9091
```

Note the asymmetry, which will otherwise cost you ten minutes: the public mapping takes the
`127.0.0.1:` prefix from you, while the admin one already has it written into the compose file —
supplying it twice produces `invalid IP address: 127.0.0.1:127.0.0.1`.

```bash
docker compose up -d --build
```

Then another `reverse_proxy` block in Caddy, its own `TRUSTED_PROXIES` when you reach §1.4 (a
comma-separated list here, not a JSON array), and one record in the panel through an SSH tunnel
to 9091:

```
key          game-launcher-api
environment  production
baseUrl      https://games.example.com/api/v1/
```

**Every answer it gives is signed**, with the same ECDSA P-256 the release check already uses.
That is what makes the *URL* safe to keep in a configuration file while the key stays in code:
pointing a launcher at a hostile registry gains an attacker nothing, because the answer will not
verify. A launcher with a URL and no key asks nothing at all.

> **Put it on a name you are willing to keep.** It is the one address every copy carries
> forever, so it earns a hostname of its own rather than a subdomain of the thing it exists to
> let you move. Ideally, in time, a different host from the API.

One cost, stated plainly: a backend that moves is picked up at the **next** start, not the one in
progress, because every HTTP client binds its address when the launcher starts. The first launch
after a move fails to reach the server.

---

## Step 2 — The signing key, before you build anything

Skip this only if you never want your launcher to update itself.

Generate the pair **on your own machine**, not on the server:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out release-signing.key
```

```bash
openssl ec -in release-signing.key -pubout -outform DER | openssl base64 -A
```

The second command prints the **public** half. Put it in the server's `.env`:

```bash
LAUNCHER_RELEASE_PUBLIC_KEY=MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...
```

and restart the API. Empty — the default — turns the whole release surface off, which is the
correct state until you have a key.

### Why the private half must not go on the server

It is the only secret in the deployment that must **not** be on the machine, and the only one
whose loss cannot be repaired by generating another. Keeping it elsewhere is what buys this:

> Somebody who takes your server, its database and its disks can stop your launchers from
> updating. They cannot make them update to anything.

An automatic update is code a machine runs without anybody looking at it, so this is the one
place where a compromised server must not be able to reach your users' computers. Putting the
key on the server — or in a CI secret a workflow file can print — gives that back.

### And why losing it cannot be repaired

Every launcher you have already handed out carries the matching public half **inside its binary**.
A new key signs releases none of them will accept. The only way out is a build everybody installs
by hand, which is exactly the situation self-update exists to end. Back it up somewhere you would
back up a password manager.

---

## Step 3 — Make the launcher yours

```bash
git clone https://github.com/Ruy41321/Custom-Game-Launcher-Frontend && cd Custom-Game-Launcher-Frontend
```

### 3.1 One JSON file

`launcher.config.json` in the repository root ships read-only beside the executable:

```json
{
  "appName": "My Studio Launcher",
  "apiBaseUrl": "https://games.example.com/api/v1/",
  "serviceRegistry": { "url": null, "serviceKey": "game-launcher-api", "environment": "production" },
  "theme": { "variant": "dark", "accentColor": "#7C5CFF" },
  "branding": { "logoPath": "assets/logo.png", "windowIconPath": "assets/icon.ico" },
  "localization": { "defaultLanguage": null, "supportedLanguages": ["en", "it", "fr"] },
  "updates": { "channel": "stable" },
  "defaultInstallDirectory": null
}
```

- **`apiBaseUrl` must end in `/api/v1/`**, trailing slash included — without it the last segment
  is silently dropped when a relative path is resolved against it. With a registry configured
  this is the **fallback**: the address used when the registry cannot be reached and nothing is
  cached yet.
- **`serviceRegistry.url`** is where §1.8 lives, or `null` for a launcher that asks nobody. It
  is configuration rather than code precisely because the *key* is not: an attacker who
  redirects a launcher to their own registry gains nothing, since the answer will not verify.
- **`theme.accentColor` is read and applied to nothing.** It has shipped since the first
  milestone and no code consults it; the views that use an accent take the toolkit's. It is left
  in the example because forks already have it in their files, not because setting it does
  anything. `theme.variant` — `dark`, `light`, `system` — does work.
- **`updates.channel`** is `stable` or `beta`, and it is a shipped setting rather than a user
  preference on purpose: a player who could move themselves onto a stream you never published to
  could replace their launcher with a build that does not open, and the launcher is the program
  that has to start in order to fix anything. An unrecognised name is read as `stable` rather
  than refused, so a typo cannot brick a working launcher.

User preferences — language, theme, install directory — live in a separate file under the
platform's app-data directory, so an update never overwrites them.

### 3.2 The two lines of code

`src/GameLauncher.Core/Updates/LauncherReleaseKey.cs`:

```csharp
public static string PublicKeyBase64 => "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...";
```

That is the same string you put in the server's `.env`, and it is **the only code change a fork
makes**. It is not in `launcher.config.json`, and the reason is worth ten seconds: *the file the
updater overwrites must not be the file that authorizes the update.* `launcher.config.json` ships
inside the directory a swap replaces, so a key kept there would be replaced by whatever the update
brought with it. A constant compiled into the launcher lives inside the artifact whose replacement
the signature already protects.

Leave it empty and your launcher checks for no updates at all — which is the honest state for a
fork that has not set up signing, rather than checking and trusting whoever answers.

And `src/GameLauncher.Core/Discovery/ServiceRegistryKey.cs`, if you set up §1.8:

```csharp
public static string PublicKeyBase64 => "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...";
```

That one is the public half `servicereg keygen` printed, and the panel shows it again under
**Signing key**. It is in code for exactly the reason the other one is — the file the updater
overwrites must not be the file that authorizes what overwrites it — and empty means this build
asks no registry anything, whatever `launcher.config.json` says.

Both strings are 124 characters and both begin `MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE`, because
that prefix names the P-256 curve rather than your key. It is a useful thing to know by sight:
a string that starts differently, or is not 124 long, did not survive the copy.

### 3.3 Logo and icon

Drop your files where `branding` points. Anything missing simply is not shown — a path that does
not resolve is answered with no logo rather than with a launcher that will not open, so a typo
here costs you a picture and never the program. **The paths are case-sensitive on Linux** and are
relative to the application directory; a leading `/` makes one absolute and it will find nothing.

**The icon on the executable is a different thing**, and it catches everybody. What `branding`
sets is the icon *inside* the window, at run time, and a PNG is fine for it. The icon Windows
shows on the `.exe` itself, on the shortcut and in the taskbar is a **compiled-in Win32
resource**: it has to be an `.ico`, and it has to exist when the build runs. Put one at
`assets/icon.ico` and the project picks it up — `<ApplicationIcon>` is conditional on that path,
so a clone without one builds exactly as before. The same file is what `installer.iss` gives to
`SetupIconFile` for the installer's own icon.

An `.ico` holds several sizes; generate it from a **square** source, or whatever tool you use
will squash a wide logo to fit.

### 3.4 Languages, if you want fewer or more

English, Italian and French ship. To add one, copy
`src/GameLauncher.Core/Localization/Strings.resx` to `Strings.<culture>.resx`, translate the
values, add the culture to `SatelliteResourceLanguages` in `GameLauncher.Core.csproj`, add a
`LanguageOption` to `ResourceManagerLocalizationService.SupportedLanguages`, and list it in
`launcher.config.json`. No UI code changes.

---

## Step 4 — Build it

Set the version first. `Directory.Build.props`:

```xml
<Version>1.0.0</Version>
```

**This number is what the update check compares.** Build an artifact and call it 1.1.0 in the
release document while this still says 1.0.0, and every launcher that installs it will keep being
offered 1.1.0 forever.

Then, one per platform:

```bash
dotnet publish src/GameLauncher.App -c Release -r win-x64 --self-contained -o dist/win-x64
```

```bash
dotnet publish src/GameLauncher.App -c Release -r linux-x64 --self-contained -o dist/linux-x64
```

You can build both from either operating system. Each output is self-contained — your users need
no .NET — and contains `GameLauncher(.exe)`, `launcher.config.json`, your assets, and a
subdirectory **`updater/`** holding the helper that performs a self-update. If `updater/` is not
there, the launcher will download an update and then refuse to install it, so check.

---

## Step 5 — Hand out the first copy

There is no installer and no auto-discovery. Zip the directory and put it somewhere your testers
can reach — a release page, a link, a shared drive. They unzip it anywhere they can write and run
`GameLauncher.exe` (Windows) or `./GameLauncher` (Linux).

**This is the only manual install.** Everything after it is the launcher updating itself.

On Linux, make sure the file is executable if your zip tool dropped the bit:

```bash
chmod +x GameLauncher
```

### An installer instead, on Windows

A zip is enough and always works. If you would rather hand people something that looks like an
installation — a Start-menu entry, a shortcut, a line in *Installed apps*, an uninstaller —
`installer.iss` in the repository root builds one with [Inno Setup](https://jrsoftware.org/isinfo.php)
6.3 or later. Build the payload as in Step 4, then:

```bash
ISCC.exe installer.iss
```

`Output\CustomGameLauncher-Setup-<version>.exe` is the file you send. It reads its version out of
the executable you just published, so there is no second number to keep in step, and it refuses to
build if `dist/win-x64` is missing or has no `updater/` in it.

**It installs per-user, into `%LOCALAPPDATA%\Programs\CustomGameLauncher`, and that is not a
preference.** A self-update renames the installation directory aside and puts the new one in its
place, so the user has to be able to write both that directory *and its parent* without
elevation — the swap happens as the launcher exits, with nobody there to answer a UAC prompt.
Installed under `C:\Program Files`, this launcher would install once and never update again. The
installer sets `PrivilegesRequired=lowest` for that reason, and warns on the directory page if you
point it somewhere its parent is not writable.

Three things it deliberately does not do:

- **It is not part of the release loop.** Updates are the signed archive of Step 7, unpacked by
  the launcher itself. You do not rebuild the installer to ship an update, and the installer is
  never published to the server.
- **It does not delete the user's data on uninstall.** Settings, logs and installed games live
  under `%LOCALAPPDATA%\CustomGameLauncher` and stay. Removing the launcher is not a request to
  delete somebody's library.
- **It does not make SmartScreen go away.** An unsigned executable downloaded from the internet
  is flagged whether it is a zip or a setup — that needs a code-signing certificate, which is a
  yearly bill and a different problem from the release signing in Step 2.

### And a tarball, on Linux

`scripts/package-linux.sh` builds the same thing in the shape Linux takes it in — the payload,
plus the `install.sh` that puts it in place. Publish as in Step 4, then:

```bash
scripts/package-linux.sh
```

`Output/CustomGameLauncher-<version>-linux-x64.tar.gz` is what you send. It takes its version
from `Directory.Build.props`, and refuses to build a payload that has no `updater/` in it. Your
testers run:

```bash
tar xzf CustomGameLauncher-1.0.0-linux-x64.tar.gz && cd CustomGameLauncher-1.0.0-linux-x64 && ./install.sh
```

That installs into `~/.local/opt/CustomGameLauncher`, writes a menu entry and a
`~/.local/bin/custom-game-launcher` symlink, and forces the executable bit — which an archive
built on Windows for a Linux runtime identifier does not carry, and whose absence looks exactly
like a new version that crashed. `--prefix DIR` puts it somewhere else; `./install.sh
--uninstall` removes it, keeping the user's data.

**It refuses to install into `~/.local/share/CustomGameLauncher`**, or anywhere under it. That is
where the launcher keeps settings, logs and installed games, and a self-update replaces its
installation directory wholesale — so the two sharing a path would mean an update that deletes
somebody's library. It checks that the parent directory is writable for the same reason the
Windows installer does.

**An AppImage would not work here.** A single immutable file is precisely what a self-update
cannot replace, and adopting one would mean giving up the updater this launcher already has.

---

## Step 6 — Publish a game

All of this happens inside the launcher, in the **Developer** tab. Two things first:

Register an account in the window, then grant it the publisher role from the server:

```bash
docker compose exec api /app/launcher-api --grant-role you@example.com dev
```

Sign out and back in — permissions travel inside the access token, so an existing session does
not learn about a new role. The **Developer** tab appears.

Then, in order:

1. **Create a game.** It starts as a `draft`, which is visible only to you.
2. **Add a version** — `1.0.0`, with release notes.
3. **Publish a build** from a directory. Pick the folder holding your game, pick the executable,
   press publish. The client hashes everything, asks the server which files it already has, and
   uploads only the rest. A second build that changes one file uploads one file.
4. **Upload a cover** and screenshots, and write a devlog entry if you want one.
5. **Set the visibility to `public`** when you want it in Explore. `unlisted` means reachable by
   people who have it in their library but not listed.

Your testers add the game from Explore and install it. An update is the same flow: new version,
new build. They get a delta — only what changed travels.

### What deleting means

A deletion is armed and then confirmed, and the confirmation sentence says what actually goes.
Read it: deleting a game removes its builds and its download history, and it cannot be undone.
If you only want a title to stop being visible, set it back to `draft`.

---

## Step 7 — Ship an update of the launcher itself

This is the loop you will run a few times a year. It has five parts and one trap.

**1. Bump the version** in `Directory.Build.props`, and build as in Step 4.

**2. Zip the publish output — not with `Compress-Archive`.** Windows PowerShell 5.1 writes `\` in
zip entry names, and the launcher refuses those (it is the same rule that stops `..\`), so the
release would be one nobody can install, rejected with a message that sounds like an attack. Use
Python, which is already on most machines:

```bash
python -c "import os,zipfile;r='dist/win-x64';z=zipfile.ZipFile('launcher-1.1.0-win-x64.zip','w',zipfile.ZIP_DEFLATED);[z.write(os.path.join(b,f),os.path.relpath(os.path.join(b,f),r).replace(chr(92),'/')) for b,_,fs in os.walk(r) for f in fs];z.close()"
```

**3. Write and sign the release document.** One per platform. It must have **no trailing
newline** — the server refuses anything that is not byte-for-byte its canonical form, so use
`printf`, never `echo`:

```bash
SHA=$(openssl dgst -sha256 -r launcher-1.1.0-win-x64.zip | cut -d' ' -f1); SIZE=$(stat -c %s launcher-1.1.0-win-x64.zip); printf '%s' "{\"schema\":1,\"channel\":\"stable\",\"version\":\"1.1.0\",\"platform\":\"windows\",\"arch\":\"x64\",\"sha256\":\"$SHA\",\"size\":$SIZE,\"releasedAt\":\"2026-09-01T10:00:00Z\",\"notes\":\"What changed, in a sentence.\"}" > release.json
```

```bash
openssl dgst -sha256 -sign release-signing.key release.json | openssl base64 -A > release.json.sig
```

`platform` is `windows` or `linux`; `arch` is `x64`. `notes` is one paragraph shown in the banner.

**Check your own signature before uploading a few hundred megabytes.** It is the same check the
server is about to make, and then every installed launcher makes again:

```bash
openssl ec -in release-signing.key -pubout -out pub.pem 2>/dev/null && openssl base64 -d -A -in release.json.sig -out sig.bin && openssl dgst -sha256 -verify pub.pem -signature sig.bin release.json
```

`Verified OK` is the only acceptable answer.

**4. Hand the server the three files.** It verifies before it stores anything:

```bash
docker compose cp release.json api:/tmp/r.json && docker compose cp release.json.sig api:/tmp/r.sig && docker compose cp launcher-1.1.0-win-x64.zip api:/tmp/a.zip && docker compose exec api /app/launcher-api --publish-release /tmp/r.json --signature /tmp/r.sig --artifact /tmp/a.zip
```

**5. Check it from outside**, with no account at all — which is exactly what a launcher does:

```bash
curl -s "https://games.example.com/api/v1/launcher/releases/latest?platform=windows&arch=x64"
```

### What your users see

At the next start, one line: *"Version 1.1.0 of My Studio Launcher is available"*, the notes, and
an **Update and restart** button. Pressing it downloads the archive, refuses it unless the bytes
hash to what you signed, unpacks it, and restarts into the new version — usually in a second or
two. There is a **Not now** that puts the line away until the next start.

**It is never silent.** A swap requires the launcher to exit, so an automatic one would be an
application closing under somebody's hands.

### If the new version does not start

The updater watches it for about thirty seconds. A launcher that exits with an error inside that
window is **rolled back**: the previous installation is put back and started again. You do not
have to do anything, and your user sees the old version come up.

Two limits, stated plainly because they are real:

- **A launcher that starts, survives thirty seconds and then crashes is not rolled back.** From
  the updater that is indistinguishable from somebody opening it and closing it. Crash reports
  cover that case if you have them turned on.
- **Nothing remembers that a release failed**, so a rolled-back launcher is offered the same
  release again at the next start. If you shipped a broken one, retire it.

### Retiring a bad release

```bash
docker compose exec api /app/launcher-api --retire-release stable windows x64 1.1.0
```

This does **not** roll anybody back. The previous release becomes the newest, and every launcher
declines it for not being newer than what it runs — so everybody stands still, which is the
intended outcome. Rolling a fleet backwards is a bigger action than withdrawing, and it is not
taken on your behalf. Fix the bug and publish 1.1.1.

Somebody already stuck on the broken version can be talked through the manual way out, as long as
the previous installation is still on their disk — it is there until the update after next, as
`<install dir>.previous`.

Run the helper **from the copy the last update left under the user's data directory**, not from
the one inside the installation: restoring deletes the installation directory before putting the
old one back, and that cannot be done while an executable inside it is running.

```bash
"%LOCALAPPDATA%\CustomGameLauncher\updates\1.1.0\updater\GameLauncher.Updater.exe" --rollback --target "C:\path\to\the\launcher" --relaunch "C:\path\to\the\launcher\GameLauncher.exe"
```

On Linux the same file lives under `~/.local/share/CustomGameLauncher/updates/1.1.0/updater/`.

---

## Step 8 — Running it day to day

Through the tunnel to <http://localhost:9090/admin>:

- **Users, roles and quotas.** Every publisher has a cumulative upload allowance; the default is
  5 GB and you can raise it per account. Deleting builds refunds it.
- **An audit trail** you cannot turn off, written by the same statement as the change it records.
- **Download analytics** — what was fetched, from which version, full or delta.
- **Crash reports**, if your users opted in, grouped by what is actually the same bug rather than
  by message text.

Two safety rules the console enforces, so you cannot lock yourself out: you cannot deactivate
your own account, and the last active holder of the user-management permission is frozen.

---

## What this does not do

Stated so you do not go looking:

- **No payments, no licences, no DRM.** Anybody who can reach your server and see a game can
  install it. Visibility is `draft` / `unlisted` / `public`, and that is the whole model.
- **No app store, and nothing signed with a code-signing certificate.** The first copy is a zip
  somebody unzips, or the Windows installer and the Linux tarball of Step 5 — unsigned either
  way, so SmartScreen has a word to say about it. That is a different thing from the release
  signing of Step 2, which protects updates and is not optional.
- **No automatic retention.** Nothing deletes an old build on its own — you delete them, and the
  disk is reclaimed after a grace period. Retired launcher artifacts are never swept at all.
- **No data export.** Account erasure exists; the other half does not.
- **No moderation tools.** An operator can delete any game through the API, but the console has
  no button for it.
- **No settings screen in the console.** Everything about how the deployment behaves —
  `MAIL_TRANSPORT` included — is `.env` plus a restart of the API. The console manages accounts
  and reads numbers; it does not configure the server.
- **macOS.** Not a target.

---

## When something is wrong

| Symptom | Almost always |
|---|---|
| The server will not start and names a variable | It is telling you the truth. Read the line |
| Everybody is throttled at once after adding TLS | `TRUSTED_PROXIES` is empty — §1.4 |
| Registrations succeed but no mail arrives | The relay accepted it and a spam filter did not — SPF/DKIM, §1.5 |
| "Forgotten your password?" is missing from the sign-in screen | `MAIL_TRANSPORT=none` on that deployment, and the launcher is saying so correctly — §1.5. Hand out a temporary password from the console |
| Somebody is stuck on a screen asking for a new password | They are: an operator gave them a temporary one. Nothing else works until they choose their own, which is the point — §1.5 |
| Everything answers `password_change_required` | The same thing, seen from the API |
| The launcher shows a sign-in screen saying the server cannot be reached | It cannot reach the server at all. Check `apiBaseUrl` and that it ends in `/api/v1/` |
| No update is ever offered | The public key is empty in the build, or the version in the document is not strictly newer, or the release is for another platform |
| `--publish-release` refuses the document | A trailing newline, usually. Use `printf` |
| The update downloads and then fails | The archive has `\` in its entry names, or no `updater/` in the build |
| `ISCC` or `package-linux.sh` refuses to build | The payload is not there, or was published without `updater/` — build Step 4 first |
| A launcher never sees an update, and it was installed by the installer | It was moved, or installed, somewhere its parent directory is not user-writable — reinstall under `%LOCALAPPDATA%\Programs` |
| A public key is 128 characters and its prefix reads `Kj9IPz0` | The DER went through a **PowerShell pipe**, which carries text and not bytes, so every byte it could not represent became `?`. Use a shell where a pipe is a pipe, or `-outform DER -out` to a file and base64 that |
| The server refuses a secret you generated | `openssl rand -base64` can produce a `$`, and compose reads that as a variable inside `.env`. `openssl rand -hex 48` cannot |
| `invalid IP address: 127.0.0.1:127.0.0.1` from the registry | `HOST_ADMIN_PORT` was given the prefix its compose file already writes. The public port takes one; the admin port does not — §1.8 |
| The registry never accepts the password you hashed | The Argon2id hash was not single-quoted in `.env`. It contains `$` |
| `rm: cannot remove '/tmp/...': Operation not permitted` after `docker compose cp` | The container does not run as root and those files were written by one. `docker compose exec -u root` |
| The publish is refused for the version | `1.1` instead of `1.1.0`. The three-component form is the only one accepted, because two spellings of one version are two rows racing to be newest |
| Two executables inside the installer, one of them the old name | `dist/` was not emptied before republishing. `dotnet publish` does not clean its output directory |
| Every launcher is offered forever the version it just installed | The version in the release document and the one in the binary disagree — the document is compared against `Directory.Build.props`, not against a number you type |
| An update rolls back every time on Linux | The archive lost the executable bit and the launcher could not start — the installer forces it now, so make sure you are on a build from 2026-08-07 or later |

The launcher writes a log next to its own data — `%LOCALAPPDATA%\CustomGameLauncher\logs` on
Windows, `~/.local/share/CustomGameLauncher/logs` on Linux. The server's is in the `api` container
and in `docker compose logs api`.

---

## Where to read more

- [README.md](README.md) — what the launcher is
- [Documentation/self-update.md](Documentation/self-update.md) — the update mechanism in full,
  including every rule a client holds and why
- [Documentation/configuration-and-localization.md](Documentation/configuration-and-localization.md)
  — everything a fork can change
- [Documentation/authentication-and-session.md](Documentation/authentication-and-session.md)
  — what the launcher does on a server that sends no mail, and the forced password change
- The server's
  [hardening-and-deployment.md](https://github.com/Ruy41321/Custom-Game-Launcher-Backend/blob/main/Documentation/hardening-and-deployment.md)
  §6 — the deployment checklist this page summarises
- The server's
  [administration.md](https://github.com/Ruy41321/Custom-Game-Launcher-Backend/blob/main/Documentation/administration.md)
  — the console, including the one-time password and the two rules that stop it locking you out
- [CONTRIBUTING.md](CONTRIBUTING.md) — if you end up wanting to change the launcher itself
