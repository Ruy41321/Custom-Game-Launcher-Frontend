#!/usr/bin/env bash
#
# Installs the launcher's *first* copy on Linux. This script ships inside the release tarball,
# beside the `app/` directory it installs; it is not run from a checkout.
#
# Everything after this is the launcher replacing itself from a signed archive
# (Documentation/self-update.md), so this file is not part of the release loop.
#
#   ./install.sh                 install, or upgrade in place
#   ./install.sh --prefix DIR    install somewhere else
#   ./install.sh --uninstall     remove it again, keeping the user's data
#
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PAYLOAD_DIR="$SCRIPT_DIR/app"

APP_NAME="Custom Game Launcher"
EXE_NAME="GameLauncher"
DESKTOP_ID="custom-game-launcher"

# The user's data — settings, logs, and the games they installed — lives in
# `$XDG_DATA_HOME/CustomGameLauncher` (PathProvider.ResolveUserDataRoot). The application must
# not be installed into that directory: a self-update replaces its installation directory
# wholesale, so the two sharing a path would mean an update that deletes somebody's library.
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/CustomGameLauncher"
DEFAULT_PREFIX="$HOME/.local/opt/CustomGameLauncher"

INSTALL_DIR="${CGL_INSTALL_DIR:-$DEFAULT_PREFIX}"
MODE="install"

die() { printf '%s\n' "error: $*" >&2; exit 1; }
note() { printf '%s\n' "$*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) [ $# -ge 2 ] || die "--prefix needs a directory"; INSTALL_DIR="$2"; shift 2 ;;
    --prefix=*) INSTALL_DIR="${1#*=}"; shift ;;
    --uninstall) MODE="uninstall"; shift ;;
    -h|--help) sed -n '3,11p' "${BASH_SOURCE[0]}" | sed 's/^#\s\?//'; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
done

[ "$(id -u)" -ne 0 ] || die "run this as your own user, not as root — the launcher installs per-user and updates itself without elevation"

# Resolve to an absolute path without requiring the directory to exist yet.
case "$INSTALL_DIR" in
  /*) ;;
  *) INSTALL_DIR="$PWD/$INSTALL_DIR" ;;
esac
INSTALL_DIR="${INSTALL_DIR%/}"

DESKTOP_FILE="${XDG_DATA_HOME:-$HOME/.local/share}/applications/$DESKTOP_ID.desktop"
BIN_LINK="$HOME/.local/bin/$DESKTOP_ID"

if [ "$MODE" = "uninstall" ]; then
  # `.previous` is the copy the last self-update set aside; it is a sibling of the installation
  # and nothing else knows about it. The data directory deliberately stays: removing the
  # launcher is not a request to delete somebody's library.
  for target in "$INSTALL_DIR" "$INSTALL_DIR.previous"; do
    if [ -d "$target" ]; then rm -rf -- "$target"; note "removed $target"; fi
  done
  [ ! -e "$DESKTOP_FILE" ] || { rm -f -- "$DESKTOP_FILE"; note "removed $DESKTOP_FILE"; }
  [ ! -L "$BIN_LINK" ] || { rm -f -- "$BIN_LINK"; note "removed $BIN_LINK"; }
  command -v update-desktop-database >/dev/null 2>&1 &&
    update-desktop-database "$(dirname -- "$DESKTOP_FILE")" >/dev/null 2>&1 || true
  note ""
  note "Your data is untouched, in $DATA_DIR."
  note "Delete it by hand if you want it gone — it holds your settings and your installed games."
  exit 0
fi

[ -d "$PAYLOAD_DIR" ] || die "no app/ beside this script — run it from the unpacked release tarball"
[ -f "$PAYLOAD_DIR/$EXE_NAME" ] || die "no $EXE_NAME in $PAYLOAD_DIR — this is not a Linux build"
# A build without this downloads updates and then refuses to install them, which is a failure
# nobody sees until the second release.
[ -d "$PAYLOAD_DIR/updater" ] || die "no updater/ in the payload — this build cannot update itself"

[ "$INSTALL_DIR" != "$DATA_DIR" ] || die "that is the launcher's data directory; an update would delete your games. Pick another --prefix"
case "$INSTALL_DIR" in
  "$DATA_DIR"/*) die "$INSTALL_DIR is inside the launcher's data directory ($DATA_DIR); an update would delete your games. Pick another --prefix" ;;
esac

# A self-update renames the installation aside to `<install>.previous` and puts the new one in
# its place, so the *parent* has to be writable too. Checked here because otherwise the failure
# arrives weeks later, on a release, looking like a broken update rather than a bad path.
PARENT_DIR="$(dirname -- "$INSTALL_DIR")"
mkdir -p -- "$PARENT_DIR" || die "cannot create $PARENT_DIR"
[ -w "$PARENT_DIR" ] || die "$PARENT_DIR is not writable, so the launcher could not update itself. Pick a --prefix inside your home directory"

if [ -e "$INSTALL_DIR" ]; then
  [ -d "$INSTALL_DIR" ] || die "$INSTALL_DIR exists and is not a directory"
  # Refuse to empty a directory that is not one of ours. An upgrade replaces the installation
  # wholesale rather than merging, so a stale file from an older release cannot survive.
  [ -f "$INSTALL_DIR/$EXE_NAME" ] || die "$INSTALL_DIR is not empty and does not look like an installation — refusing to replace it"
  note "replacing the installation in $INSTALL_DIR"
  rm -rf -- "$INSTALL_DIR"
fi

mkdir -p -- "$INSTALL_DIR"
cp -R -- "$PAYLOAD_DIR/." "$INSTALL_DIR/"

# Forced rather than trusted: an archive built on Windows for a Linux runtime identifier carries
# no mode at all, and a launcher without +x cannot start — which from the updater is
# indistinguishable from a new version that crashed, so it would roll itself back on every
# release for a reason nothing reports. Same rule the installer half of the updater applies.
chmod +x -- "$INSTALL_DIR/$EXE_NAME"
[ ! -f "$INSTALL_DIR/updater/$EXE_NAME.Updater" ] || chmod +x -- "$INSTALL_DIR/updater/$EXE_NAME.Updater"

mkdir -p -- "$(dirname -- "$DESKTOP_FILE")"
{
  printf '%s\n' '[Desktop Entry]'
  printf '%s\n' 'Type=Application'
  printf '%s\n' "Name=$APP_NAME"
  printf '%s\n' 'Comment=Install and play games from your own launcher'
  printf '%s\n' "Exec=\"$INSTALL_DIR/$EXE_NAME\""
  [ ! -f "$INSTALL_DIR/assets/logo.png" ] || printf '%s\n' "Icon=$INSTALL_DIR/assets/logo.png"
  printf '%s\n' 'Terminal=false'
  printf '%s\n' 'Categories=Game;'
  printf '%s\n' "StartupWMClass=$EXE_NAME"
} > "$DESKTOP_FILE"
chmod 644 -- "$DESKTOP_FILE"

command -v update-desktop-database >/dev/null 2>&1 &&
  update-desktop-database "$(dirname -- "$DESKTOP_FILE")" >/dev/null 2>&1 || true

mkdir -p -- "$HOME/.local/bin"
ln -sfn -- "$INSTALL_DIR/$EXE_NAME" "$BIN_LINK"

note ""
note "$APP_NAME is installed in $INSTALL_DIR."
note "Start it from your applications menu, or run: $BIN_LINK"
case ":$PATH:" in
  *":$HOME/.local/bin:"*) ;;
  *) note "($HOME/.local/bin is not on your PATH, so only the menu entry will work for now.)" ;;
esac
note "It will update itself from now on. To remove it: ./install.sh --uninstall"
