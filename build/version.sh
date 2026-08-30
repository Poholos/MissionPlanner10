#!/usr/bin/env bash

# Resolve one version contract for assemblies, UI, release archives and native packages.
# This file is intended to be sourced by another Bash script. When executed directly it
# prints the resolved values as tab-separated fields.
# ShellCheck analyzes the sourced and directly executed branches separately and treats the
# fallback `exit` in `return ... || exit ...` as unreachable, although both branches are required.
# shellcheck disable=SC2317

_mp_version_script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
_mp_version_root="$(cd "$_mp_version_script_dir/.." && pwd)"
_mp_upstream_info="$_mp_version_root/Properties/AssemblyInfo.cs"
_mp_local_build_file="$_mp_version_root/build/local-build-number.txt"

if [[ ! -r "$_mp_upstream_info" ]]; then
  echo "MissionPlanner version source is unavailable: $_mp_upstream_info" >&2
  return 2 2>/dev/null || exit 2
fi
if [[ ! -r "$_mp_local_build_file" ]]; then
  echo "MissionPlanner local build number is unavailable: $_mp_local_build_file" >&2
  return 2 2>/dev/null || exit 2
fi

MP_UPSTREAM_VERSION="${MISSIONPLANNER_UPSTREAM_VERSION:-$(
  sed -nE 's/.*AssemblyFileVersion\("([0-9]+\.[0-9]+\.[0-9]+)"\).*/\1/p' \
    "$_mp_upstream_info" | head -1
)}"
MP_LOCAL_BUILD_NUMBER="${MISSIONPLANNER_LOCAL_BUILD_NUMBER:-$(
  tr -d '[:space:]' < "$_mp_local_build_file"
)}"
MP_COMMIT="${MISSIONPLANNER_COMMIT:-$(git -C "$_mp_version_root" rev-parse --short=8 HEAD)}"

if [[ -n "${MISSIONPLANNER_DIRTY_SUFFIX+x}" ]]; then
  MP_DIRTY_SUFFIX="$MISSIONPLANNER_DIRTY_SUFFIX"
elif [[ -n "$(git -C "$_mp_version_root" status --porcelain --untracked-files=normal -- \
    . ':!out')" ]]; then
  MP_DIRTY_SUFFIX=".dirty"
else
  MP_DIRTY_SUFFIX=""
fi

if [[ ! "$MP_UPSTREAM_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Upstream MissionPlanner version must be numeric x.y.z: '$MP_UPSTREAM_VERSION'" >&2
  return 2 2>/dev/null || exit 2
fi
if [[ ! "$MP_LOCAL_BUILD_NUMBER" =~ ^[1-9][0-9]{0,4}$ ]] ||
    (( 10#$MP_LOCAL_BUILD_NUMBER < 1 || 10#$MP_LOCAL_BUILD_NUMBER > 65535 )); then
  echo "Local build number must be an integer from 1 through 65535: '$MP_LOCAL_BUILD_NUMBER'" >&2
  return 2 2>/dev/null || exit 2
fi
if [[ ! "$MP_COMMIT" =~ ^[0-9A-Fa-f]{8,40}$ ]]; then
  echo "Repository commit must be an 8-40 character Git hash: '$MP_COMMIT'" >&2
  return 2 2>/dev/null || exit 2
fi
if [[ "$MP_DIRTY_SUFFIX" != "" && "$MP_DIRTY_SUFFIX" != ".dirty" ]]; then
  echo "Dirty suffix must be empty or '.dirty': '$MP_DIRTY_SUFFIX'" >&2
  return 2 2>/dev/null || exit 2
fi

MP_COMMIT="$(printf '%s' "$MP_COMMIT" | tr '[:upper:]' '[:lower:]')"
IFS=. read -r _mp_version_major _mp_version_minor _mp_version_patch <<< "$MP_UPSTREAM_VERSION"
if (( _mp_version_major > 255 || _mp_version_minor > 255 )); then
  echo "MSI version fields exceed Windows Installer limits: $MP_UPSTREAM_VERSION" >&2
  return 2 2>/dev/null || exit 2
fi
MP_PRODUCT_VERSION="$MP_UPSTREAM_VERSION.$MP_LOCAL_BUILD_NUMBER"
MP_FILE_VERSION="$MP_PRODUCT_VERSION"
MP_INFORMATIONAL_VERSION="$MP_PRODUCT_VERSION+$MP_COMMIT$MP_DIRTY_SUFFIX"
MP_PACKAGE_VERSION="${PACKAGE_VERSION:-$MP_INFORMATIONAL_VERSION}"
MP_ARTIFACT_VERSION="$MP_PRODUCT_VERSION-$MP_COMMIT$MP_DIRTY_SUFFIX"
# Windows Installer accepts only three numeric fields; the explicit local build number remains
# the monotonic third field while the full four-part product version stays in package metadata.
MP_MSI_VERSION="$_mp_version_major.$_mp_version_minor.$MP_LOCAL_BUILD_NUMBER"
# Epoch 1 migrates from the old un-epoched CalVer packages. The tracked local build number appears
# before the hash, so package order never depends on lexical Git-hash order.
MP_DEBIAN_VERSION="${DEBIAN_VERSION:-1:$MP_PRODUCT_VERSION+$MP_COMMIT$MP_DIRTY_SUFFIX}"

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$MP_UPSTREAM_VERSION" "$MP_LOCAL_BUILD_NUMBER" "$MP_COMMIT$MP_DIRTY_SUFFIX" \
    "$MP_FILE_VERSION" "$MP_INFORMATIONAL_VERSION" "$MP_PACKAGE_VERSION" \
    "$MP_ARTIFACT_VERSION" "$MP_DEBIAN_VERSION" "$MP_MSI_VERSION"
fi
