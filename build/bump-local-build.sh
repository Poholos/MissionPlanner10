#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_FILE="$SCRIPT_DIR/local-build-number.txt"

if [[ $# -ne 0 ]]; then
  echo "Usage: $0" >&2
  exit 2
fi
if [[ ! -r "$BUILD_FILE" ]]; then
  echo "Local build number is unavailable: $BUILD_FILE" >&2
  exit 2
fi
if [[ -n "$(git -C "$SCRIPT_DIR/.." status --porcelain -- "build/local-build-number.txt")" ]]; then
  echo "Refusing to overwrite an uncommitted local build-number change." >&2
  exit 2
fi

current="$(tr -d '[:space:]' < "$BUILD_FILE")"
if [[ ! "$current" =~ ^[1-9][0-9]{0,4}$ ]] || (( 10#$current >= 65535 )); then
  echo "Local build number must be an integer from 1 through 65534: '$current'" >&2
  exit 2
fi

next=$((10#$current + 1))
temporary="$(mktemp "$BUILD_FILE.XXXXXXXX")"
cleanup() {
  rm -f -- "$temporary"
}
trap cleanup EXIT
printf '%s\n' "$next" > "$temporary"
chmod --reference="$BUILD_FILE" "$temporary"
mv -- "$temporary" "$BUILD_FILE"
trap - EXIT

echo "Local Mission Planner build number: $current -> $next"
echo "Commit build/local-build-number.txt before producing a release."
