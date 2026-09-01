#!/usr/bin/env bash
set -euo pipefail

# Create a compressed macOS installer image containing the application and an
# Applications shortcut. Run this only on macOS, after the .app is signed and stapled.
# Usage: make-dmg.sh <app-path> <output-dmg> [volume-name]

APP="${1:?application bundle path is required}"
DMG="${2:?output DMG path is required}"
VOLUME_NAME="${3:-Mission Planner 10}"

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

run_with_retry() {
  local description="$1"
  shift

  local attempt
  for attempt in 1 2 3; do
    if "$@"; then
      return 0
    fi

    if [[ "$attempt" -eq 3 ]]; then
      echo "$description failed after $attempt attempts" >&2
      return 1
    fi

    echo "$description failed (attempt $attempt/3); retrying..." >&2
    sleep "$((attempt * 2))"
  done
}

create_dmg() {
  rm -f "$DMG"
  hdiutil create -ov -format UDZO -volname "$VOLUME_NAME" \
    -srcfolder "$STAGING" "$DMG"
}

# DiskImages can briefly retain a just-created image on hosted macOS runners.
# Retry both operations so a transient Resource temporarily unavailable error
# does not discard an otherwise valid package, while preserving a hard failure.
run_with_retry "DMG creation" create_dmg
run_with_retry "DMG verification" hdiutil verify "$DMG"
test -s "$DMG"

echo "Built $DMG"
