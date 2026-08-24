#!/usr/bin/env bash
#
# Builds the Linux release tarball: the payload from `dist/linux-x64`, plus the `install.sh`
# that puts it on a tester's machine. The counterpart of `installer.iss` on Windows, and like it
# this is only for the *first* copy — updates are the signed archive of DISTRIBUTING.md §7,
# which is a plain zip of the publish output and is not built here.
#
#   dotnet publish src/GameLauncher.App -c Release -r linux-x64 --self-contained -o dist/linux-x64
#   scripts/package-linux.sh
#
# The tarball lands in Output/.
#
set -euo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PAYLOAD_DIR="$REPO_ROOT/dist/linux-x64"
OUTPUT_DIR="$REPO_ROOT/Output"

die() { printf '%s\n' "error: $*" >&2; exit 1; }

[ -d "$PAYLOAD_DIR" ] ||
  die "no dist/linux-x64 — build it first:
  dotnet publish src/GameLauncher.App -c Release -r linux-x64 --self-contained -o dist/linux-x64"
[ -f "$PAYLOAD_DIR/GameLauncher" ] || die "no GameLauncher in $PAYLOAD_DIR"
# A build without this downloads updates and then refuses to install them — a failure nobody
# sees until the second release, so it is worth a line here, where it costs a rebuild.
[ -d "$PAYLOAD_DIR/updater" ] || die "no updater/ in the payload — publish GameLauncher.App, not the bare project"

# The version comes out of the same file the update check compares, never a number typed here:
# a tarball called 1.1.0 around a binary that says 1.0.0 produces a machine that is offered the
# update it just installed, forever.
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$REPO_ROOT/Directory.Build.props" | head -n 1)"
[ -n "$VERSION" ] || die "no <Version> in Directory.Build.props"

NAME="CustomGameLauncher-$VERSION-linux-x64"
STAGE="$(mktemp -d)"
trap 'rm -rf -- "$STAGE"' EXIT

mkdir -p -- "$STAGE/$NAME/app"
cp -R -- "$PAYLOAD_DIR/." "$STAGE/$NAME/app/"
cp -- "$REPO_ROOT/install.sh" "$STAGE/$NAME/install.sh"
chmod +x -- "$STAGE/$NAME/install.sh"

# tar carries the mode, unlike a zip written on Windows — but the payload may have come from a
# Windows publish with no mode at all, so set the two files that have to be executable rather
# than hoping. install.sh forces them again at install time, for a tarball built elsewhere.
chmod +x -- "$STAGE/$NAME/app/GameLauncher"
[ ! -f "$STAGE/$NAME/app/updater/GameLauncher.Updater" ] ||
  chmod +x -- "$STAGE/$NAME/app/updater/GameLauncher.Updater"

# Say so when the mode did not stick, which is what happens when this runs on Windows: MSYS
# marks a file executable by looking at it, and an ELF binary is not something it recognises.
# Harmless — install.sh forces the bit at install time, and so does the updater — but it looks
# like a broken build in `tar tvzf` and is worth one line here rather than one hour later.
[ -x "$STAGE/$NAME/app/GameLauncher" ] ||
  note "note: the executable bit did not stick (building on Windows?). install.sh forces it on install."

mkdir -p -- "$OUTPUT_DIR"
ARCHIVE="$OUTPUT_DIR/$NAME.tar.gz"
tar -C "$STAGE" -czf "$ARCHIVE" "$NAME"

printf '%s\n' "$ARCHIVE"
printf '%s\n' "  tar xzf $NAME.tar.gz && cd $NAME && ./install.sh"
