#!/usr/bin/env bash
set -euo pipefail

readonly native_baseline_commit="67a3c4f22bd1b38ac499f9756902e04fa4ed8444"
readonly source_port_commit="8ed19081c972a80a8b6996ed817581bc59cbcb4b"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/../.." && pwd -P)"
source_port="${1:-$repo_root/../MissionPlanner-Avalonia}"
output_dir="$repo_root/Porting"

if ! git -C "$repo_root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "Native Mission Planner repository not found: $repo_root" >&2
  exit 1
fi
if ! git -C "$source_port" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "Avalonia source repository not found: $source_port" >&2
  exit 1
fi
if ! git -C "$repo_root" cat-file -e "$native_baseline_commit^{tree}"; then
  echo "Native migration baseline is unavailable: $native_baseline_commit" >&2
  exit 1
fi
if ! git -C "$source_port" cat-file -e "$source_port_commit^{tree}"; then
  echo "Avalonia migration source is unavailable: $source_port_commit" >&2
  exit 1
fi

temporary_dir="$(mktemp -d)"
trap 'rm -rf -- "$temporary_dir"' EXIT

native_files="$temporary_dir/native-files"
port_files="$temporary_dir/port-files"
native_output="$temporary_dir/NATIVE_SURFACE.tsv"
import_output="$temporary_dir/PORT_SOURCE_IMPORT.tsv"

# Always inventory the two frozen migration trees. Reading the current index would make a
# successfully removed WinForms file disappear from the audit on the next regeneration.
LC_ALL=C git -C "$repo_root" ls-tree -r --name-only "$native_baseline_commit" |
  LC_ALL=C awk '($0 ~ /\.cs$/ || $0 ~ /\.resx$/) &&
      $0 !~ /^ExtLibs\// && $0 !~ /^MissionPlannerTests\//' |
  LC_ALL=C sort > "$native_files"
LC_ALL=C git -C "$source_port" ls-tree -r --name-only "$source_port_commit" |
  LC_ALL=C sort > "$port_files"

canonical_name() {
  local value="${1##*/}"
  value="${value,,}"
  value="${value%.designer.cs}"
  value="${value%.axaml.cs}"
  value="${value%.cs}"
  value="${value%viewmodel}"
  value="${value%window}"
  value="${value%view}"
  printf '%s' "$value"
}

declare -A port_candidates_by_name=()
while IFS= read -r source_path; do
  case "$source_path" in
    src/MissionPlannerAvalonia/*.cs|src/MissionPlannerAvalonia/*.axaml|src/MissionPlannerAvalonia/**/*.cs|src/MissionPlannerAvalonia/**/*.axaml)
      candidate="$(canonical_name "$source_path")"
      existing="${port_candidates_by_name[$candidate]-}"
      if [[ -n "$existing" ]]; then
        existing+=";"
      fi
      port_candidates_by_name["$candidate"]="$existing$source_path"
      ;;
  esac
done < "$port_files"

find_candidates() {
  local wanted
  wanted="$(canonical_name "$1")"
  printf '%s' "${port_candidates_by_name[$wanted]-}"
}

printf 'native_path\tkind\tstatus\tported_candidates\tevidence_or_next_action\n' > "$native_output"
while IFS= read -r native_path; do
  kind="csharp"
  status="unported-blocker"
  candidates=""
  evidence="Requires code-level classification before project exclusion or deletion."

  case "$native_path" in
    *.resx)
      kind="resx"
      status="retain"
      evidence="Preserve neutral/culture resource until AXAML localization mapping is verified."
      ;;
    Radio/Uploader.cs|Radio/IHex.cs|Grid/GridData.cs)
      status="retain"
      candidates="$native_path"
      evidence="The tested port compiled this exact native source through a temporary compatibility copy; compile it directly in-place."
      ;;
    Properties/AssemblyInfo.cs)
      status="merge"
      candidates="src/MissionPlannerAvalonia/MissionPlannerAvalonia.csproj;src/MissionPlannerAvalonia/Services/AppVersion.cs"
      evidence="Keep official version and add build date plus canonical commit hash."
      ;;
    Program.cs)
      status="replace"
      candidates="src/MissionPlannerAvalonia/Program.cs"
      evidence="Replace WinForms startup with the tested Avalonia entry point."
      ;;
    MainV2.cs|MainV2.Designer.cs)
      status="replace"
      candidates="src/MissionPlannerAvalonia/Views/MainWindow.axaml;src/MissionPlannerAvalonia/Views/MainWindow.axaml.cs;src/MissionPlannerAvalonia/ViewModels/MainWindowViewModel.cs"
      evidence="Main application shell replacement; legacy plugin ABI must be merged into the main assembly first."
      ;;
    GCSViews/FlightData.cs|GCSViews/FlightData.Designer.cs)
      status="replace"
      candidates="src/MissionPlannerAvalonia/Views/FlightDataView.axaml;src/MissionPlannerAvalonia/Views/FlightDataView.axaml.cs;src/MissionPlannerAvalonia/ViewModels/FlightDataViewModel.cs"
      evidence="Flight Data Avalonia replacement with splitter/session/airport fixes."
      ;;
    GCSViews/FlightPlanner.cs|GCSViews/FlightPlanner.Designer.cs)
      status="replace"
      candidates="src/MissionPlannerAvalonia/Views/FlightPlannerView.axaml;src/MissionPlannerAvalonia/Views/FlightPlannerView.axaml.cs;src/MissionPlannerAvalonia/ViewModels/FlightPlannerViewModel.cs"
      evidence="Flight Planner Avalonia replacement."
      ;;
    *)
      candidates="$(find_candidates "$native_path")"
      ;;
  esac

  printf '%s\t%s\t%s\t%s\t%s\n' \
    "$native_path" "$kind" "$status" "$candidates" "$evidence" >> "$native_output"
done < "$native_files"

printf 'source_path\tplanned_native_path\taction\tnotes\n' > "$import_output"
while IFS= read -r source_path; do
  target_path=""
  action="review"
  notes="Confirm semantic mapping before import."

  case "$source_path" in
    external/MissionPlanner)
      action="remove"
      notes="Never import the old Mission Planner gitlink; native source already is the repository root."
      ;;
    external/Directory.Build.props|external/Directory.Packages.props)
      action="remove"
      notes="Old submodule shielding is unnecessary in the in-place layout; scope root build policy explicitly instead."
      ;;
    .gitmodules)
      target_path=".gitmodules"
      action="merge"
      notes="Do not overwrite the native ExtLibs/mono submodule declaration."
      ;;
    src/MissionPlannerAvalonia/MissionPlannerAvalonia.csproj)
      target_path="MissionPlanner.csproj"
      action="merge"
      notes="Root project replacement; rename main assembly/product to MissionPlanner and use direct native paths."
      ;;
    src/MissionPlannerAvalonia/Program.cs)
      target_path="Program.cs"
      action="replace"
      notes="Root Avalonia entry point."
      ;;
    src/MissionPlannerAvalonia/Views/GCSViews/*)
      target_path="GCSViews/${source_path#src/MissionPlannerAvalonia/Views/GCSViews/}"
      action="import"
      notes="Avalonia replacement in the native GCSViews feature tree."
      ;;
    src/MissionPlannerAvalonia/Views/FlightDataView.*|src/MissionPlannerAvalonia/Views/FlightPlannerView.*|src/MissionPlannerAvalonia/Views/SetupView.*)
      target_path="GCSViews/${source_path#src/MissionPlannerAvalonia/Views/}"
      action="import"
      notes="Top-level operational view placed in the native GCSViews tree."
      ;;
    src/MissionPlannerAvalonia/Views/Setup/*)
      target_path="GCSViews/Setup/${source_path#src/MissionPlannerAvalonia/Views/Setup/}"
      action="import"
      notes="Setup implementation placed under the native GCSViews tree."
      ;;
    src/MissionPlannerAvalonia/*)
      target_path="${source_path#src/MissionPlannerAvalonia/}"
      action="import"
      notes="Port-owned application source/resource imported into the native root."
      ;;
    src/MissionPlannerAvalonia.PluginApi/*)
      target_path="Plugin/PortableApi/${source_path#src/MissionPlannerAvalonia.PluginApi/}"
      action="import"
      notes="Keep a distinct portable contract identity; rename project and references deliberately."
      ;;
    src/MissionPlannerAvalonia.LegacyPluginApi/*)
      target_path="Plugin/LegacyCompatibility/${source_path#src/MissionPlannerAvalonia.LegacyPluginApi/}"
      action="merge"
      notes="Merge compatibility types into main MissionPlanner assembly; do not emit a second MissionPlanner.dll."
      ;;
    src/Px4Uploader/*)
      target_path="ExtLibs/px4uploader/${source_path#src/Px4Uploader/}"
      action="merge"
      notes="Merge portable uploader changes into the native ExtLib project."
      ;;
    src/UpstreamCompat/MonoRuntimeSettingsCompatibility.cs)
      target_path="ExtLibs/Utilities/Compatibility/MonoRuntimeSettingsCompatibility.cs"
      action="merge"
      notes="Retain only if CoreCLR compatibility remains necessary after direct native changes."
      ;;
    tests/*)
      target_path="MissionPlannerTests/Avalonia/${source_path#tests/}"
      action="import"
      notes="Preserve all port regression coverage while unifying the test graph."
      ;;
    docs/*)
      target_path="Porting/Reference/${source_path#docs/}"
      action="import"
      notes="Historical/reference documentation; reconcile current facts into native docs/README later."
      ;;
    build/*|srtm/*)
      target_path="$source_path"
      action="import"
      notes="Cross-platform build/package/runtime support."
      ;;
    .github/*|.editorconfig|.gitattributes|.gitignore|Directory.Build.props|Directory.Build.targets|Directory.Packages.props|README.md|LICENSE|NOTICE.md|Makefile|global.json|SITL-TESTING.md)
      target_path="$source_path"
      action="merge"
      notes="Merge by meaning; never overwrite native repository policy, licensing or workflows mechanically."
      ;;
    MissionPlannerAvalonia.slnx)
      target_path="MissionPlanner.slnx"
      action="merge"
      notes="Create the cross-platform solution graph with direct root ExtLib references."
      ;;
    LICENSES/*)
      target_path="$source_path"
      action="import"
      notes="Retain third-party notices for shipped native components."
      ;;
  esac

  printf '%s\t%s\t%s\t%s\n' "$source_path" "$target_path" "$action" "$notes" >> "$import_output"
done < "$port_files"

mkdir -p "$output_dir"
mv "$native_output" "$output_dir/NATIVE_SURFACE.tsv"
mv "$import_output" "$output_dir/PORT_SOURCE_IMPORT.tsv"

native_rows="$(( $(wc -l < "$output_dir/NATIVE_SURFACE.tsv") - 1 ))"
source_rows="$(( $(wc -l < "$output_dir/PORT_SOURCE_IMPORT.tsv") - 1 ))"
echo "Generated $native_rows native rows and $source_rows source rows."
