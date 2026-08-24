#!/usr/bin/env bash
set -euo pipefail

# Create a compressed macOS installer image containing the application and an
# Applications shortcut. Run this only on macOS, after the .app is signed and stapled.
# Usage: make-dmg.sh <app-path> <output-dmg> [volume-name]

APP="${1:?application bundle path is required}"
DMG="${2:?output DMG path is required}"
VOLUME_NAME="${3:-Mission Planner}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "make-dmg.sh requires macOS and hdiutil" >&2
  exit 1
fi

if [[ ! -d "$APP/Contents/MacOS" ]]; then
  echo "Application bundle is incomplete: $APP" >&2
  exit 1
fi

mkdir -p "$(dirname "$DMG")"
WORK_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/missionplanner-dmg.XXXXXXXX")"

cleanup() {
  rm -rf "$WORK_ROOT"
}
trap cleanup EXIT

STAGING="$WORK_ROOT/staging"
mkdir -p "$STAGING"
ditto "$APP" "$STAGING/$(basename "$APP")"
ln -s /Applications "$STAGING/Applications"

hdiutil create -quiet -ov -format UDZO -volname "$VOLUME_NAME" \
  -srcfolder "$STAGING" "$DMG"
hdiutil verify "$DMG"
test -s "$DMG"

echo "Built $DMG"
