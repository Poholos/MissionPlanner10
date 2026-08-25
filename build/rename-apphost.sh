#!/usr/bin/env bash
set -euo pipefail

PUBLISH_DIR="${1:?usage: rename-apphost.sh PUBLISH_DIR RID}"
RID="${2:?usage: rename-apphost.sh PUBLISH_DIR RID}"

case "$RID" in
  win-*) SOURCE_NAME="MissionPlanner.exe"; TARGET_NAME="MissionPlanner10.exe" ;;
  *) SOURCE_NAME="MissionPlanner"; TARGET_NAME="MissionPlanner10" ;;
esac

if [[ -f "$PUBLISH_DIR/$TARGET_NAME" && ! -e "$PUBLISH_DIR/$SOURCE_NAME" ]]; then
  exit 0
fi
if [[ ! -f "$PUBLISH_DIR/$SOURCE_NAME" ]]; then
  echo "Publish output does not contain $SOURCE_NAME: $PUBLISH_DIR" >&2
  exit 1
fi
if [[ -e "$PUBLISH_DIR/$TARGET_NAME" ]]; then
  echo "Refusing to overwrite existing branded apphost: $PUBLISH_DIR/$TARGET_NAME" >&2
  exit 1
fi

mv "$PUBLISH_DIR/$SOURCE_NAME" "$PUBLISH_DIR/$TARGET_NAME"
