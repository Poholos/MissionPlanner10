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

# Native WinForms setup/configuration classes were frequently renamed or consolidated while
# retaining their user workflow in the Avalonia port. Keep the mapping explicit so deleting the
# replaced C# files cannot erase the evidence from the frozen native-surface inventory.
declare -A native_replacement_by_logical_path=(
  ["GCSViews/ConfigurationView/ConfigAC_Fence"]="GCSViews/ConfigurationView/ConfigAC_FenceView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigAC_FenceViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigADSB"]="GCSViews/ConfigurationView/ConfigADSBView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigADSBViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigAccelerometerCalibration"]="GCSViews/ConfigurationView/ConfigAccelCalibrationView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCalibrationPages.cs"
  ["GCSViews/ConfigurationView/ConfigAdvanced"]="Views/ActionPageView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigAdvancedViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigAntennaTracker"]="GCSViews/ConfigurationView/ConfigAntennaTrackerParamView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigAntennaTrackerParamViewModel.cs;GCSViews/ConfigurationView/ConfigAntennaTrackerView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigAntennaTrackerViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigArducopter"]="GCSViews/ConfigurationView/ConfigBasicTuningView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigBasicTuningViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigArduplane"]="GCSViews/ConfigurationView/ConfigArduplaneView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigArduplaneViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigArdurover"]="GCSViews/ConfigurationView/ConfigArduroverView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigArduroverViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigBatteryMonitoring"]="GCSViews/ConfigurationView/ConfigBatteryMonitoringView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigBatteryMonitoringViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigBatteryMonitoring2"]="GCSViews/ConfigurationView/ConfigBatteryMonitoring2View.axaml;ViewModels/GCSViews/ConfigurationView/ConfigBatteryMonitoring2ViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigCompassMot"]="GCSViews/ConfigurationView/ConfigCompassMotView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCompassMotViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigCubeID"]="GCSViews/ConfigurationView/ConfigCubeIDView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCubeIDViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigDroneCAN"]="GCSViews/ConfigurationView/ConfigDroneCanView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDroneCanViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigESCCalibration"]="GCSViews/ConfigurationView/ConfigESCCalibrationView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCalibrationPages.cs"
  ["GCSViews/ConfigurationView/ConfigFFT"]="GCSViews/ConfigurationView/ConfigFFTView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFFTViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFailSafe"]="GCSViews/ConfigurationView/ConfigFailSafeView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFailSafeViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFirmware"]="GCSViews/Setup/InstallFirmwareView.axaml;ViewModels/Setup/InstallFirmwareViewModel.cs;GCSViews/ConfigurationView/ConfigFirmwareLegacyView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFirmwareLegacyViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFirmwareManifest"]="GCSViews/Setup/InstallFirmwareView.axaml;ViewModels/Setup/InstallFirmwareViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFlightModes"]="GCSViews/ConfigurationView/ConfigFlightModesView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFlightModesViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFrameClassType"]="GCSViews/ConfigurationView/ConfigFrameClassTypeView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFrameClassTypeViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFrameType"]="GCSViews/ConfigurationView/ConfigFrameTypeView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFrameTypeViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFriendlyParams"]="GCSViews/ConfigurationView/ConfigFriendlyParamsView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFriendlyParamsViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFriendlyParamsAdv"]="GCSViews/ConfigurationView/ConfigFriendlyParamsView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFriendlyParamsViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigGPSOrder"]="GCSViews/ConfigurationView/ConfigGPSOrderView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigGPSOrderViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWAirspeed"]="GCSViews/ConfigurationView/ConfigAirspeedView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigAirspeedViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWBT"]="GCSViews/ConfigurationView/ConfigHWBTView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigHWBTViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWCAN"]="GCSViews/ConfigurationView/ConfigHWCANView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigHWCANViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWCompass"]="GCSViews/ConfigurationView/ConfigCompassLegacyView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCompassLegacyViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWCompass2"]="GCSViews/ConfigurationView/ConfigCompassView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCalibrationPages.cs"
  ["GCSViews/ConfigurationView/ConfigHWIDs"]="GCSViews/ConfigurationView/ConfigHWIDView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigHWIDViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWOSD"]="GCSViews/ConfigurationView/ConfigHWOSDView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigHWOSDViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWOptFlow"]="GCSViews/ConfigurationView/ConfigOptFlowView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigOptFlowViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWPX4Flow"]="GCSViews/ConfigurationView/ConfigPX4FlowView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigPX4FlowViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWParachute"]="GCSViews/ConfigurationView/ConfigParachuteView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigParachuteViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWRangeFinder"]="GCSViews/ConfigurationView/ConfigRangeFinderView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigRangeFinderViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWesp8266"]="GCSViews/ConfigurationView/ConfigHWESP8266View.axaml;ViewModels/GCSViews/ConfigurationView/ConfigHWESP8266ViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigInitialParams"]="GCSViews/ConfigurationView/ConfigInitialParamsView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigInitialParamsViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigMandatory"]="GCSViews/SetupView.axaml;ViewModels/SetupViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigMotorTest"]="GCSViews/ConfigurationView/ConfigMotorTestView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCalibrationPages.cs"
  ["GCSViews/ConfigurationView/ConfigMount"]="GCSViews/ConfigurationView/ConfigMountView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigMountViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigOSD"]="GCSViews/ConfigurationView/ConfigOSDView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigOSDViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigOptional"]="GCSViews/SetupView.axaml;ViewModels/SetupViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigParamLoading"]="GCSViews/ConfigurationView/ConfigParamLoadingView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigParamLoadingViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigPlanner"]="GCSViews/ConfigurationView/ConfigPlannerView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigPlannerViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigPlannerAdv"]="GCSViews/ConfigurationView/ConfigPlannerAdvView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigPlannerAdvViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigREPL"]="GCSViews/ConfigurationView/ConfigOnboardReplView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigOnboardReplViewModel.cs;GCSViews/ConfigurationView/ConfigScriptReplView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigScriptReplViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigRadioInput"]="GCSViews/ConfigurationView/ConfigRadioInputView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigRadioInputViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigRadioOutput"]="GCSViews/ConfigurationView/ConfigRadioOutputView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigRadioOutputViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigRawParams"]="Views/RawParamsView.axaml;ViewModels/RawParamsViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigSecure"]="GCSViews/ConfigurationView/ConfigSecureView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigSecureViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigSecureAP"]="GCSViews/ConfigurationView/ConfigSecureApView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigSecureApViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigSerial"]="GCSViews/ConfigurationView/ConfigSerialView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigSerialViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigSerialInjectGPS"]="GCSViews/ConfigurationView/ConfigGpsInjectView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigGpsInjectViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigSimplePids"]="GCSViews/ConfigurationView/ConfigBasicTuningView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigBasicTuningViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigTerminal"]="GCSViews/ConfigurationView/ConfigTerminalView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigTerminalViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigTradHeli"]="GCSViews/ConfigurationView/ConfigTradHeliView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigTradHeliViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigTradHeli4"]="GCSViews/ConfigurationView/ConfigTradHeli4View.axaml;ViewModels/GCSViews/ConfigurationView/ConfigTradHeli4ViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigUserDefined"]="GCSViews/ConfigurationView/ConfigUserDefinedView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigUserDefinedViewModel.cs"
  ["GCSViews/ConfigurationView/DeviceInfo"]="GCSViews/ConfigurationView/ConfigDroneCanView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDroneCanViewModel.cs"
  ["GCSViews/ConfigurationView/DroneCANModel"]="GCSViews/ConfigurationView/ConfigDroneCanView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDroneCanViewModel.cs"
  ["GCSViews/ConfigurationView/uitype"]="GCSViews/ConfigurationView/ConfigDroneCanView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDroneCanViewModel.cs"
  ["GCSViews/Help"]="Views/HelpView.axaml;ViewModels/HelpViewModel.cs"
  ["GCSViews/InitialSetup"]="GCSViews/SetupView.axaml;ViewModels/SetupViewModel.cs"
  ["GCSViews/SITL"]="Views/SimulationView.axaml;ViewModels/SimulationViewModel.cs;Services/SitlLauncher.cs"
  ["GCSViews/SoftwareConfig"]="Views/ConfigView.axaml;ViewModels/ConfigViewModel.cs"
)

declare -A native_removal_by_logical_path=(
  ["GCSViews/ConfigurationView/ConfigAteryx"]="Ateryx-specific setup is retired with the legacy Ateryx HIL path; PORT_STATUS records SITL as its supported replacement."
  ["GCSViews/ConfigurationView/ConfigAteryxSensors"]="Ateryx-specific sensor setup is retired with the legacy Ateryx HIL path; PORT_STATUS records SITL as its supported replacement."
  ["GCSViews/ConfigurationView/ConfigFirmwareDisabled"]="The Avalonia setup navigation hides unavailable firmware actions instead of opening a WinForms disabled-placeholder page."
)

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
      candidates="MissionPlanner.csproj;Services/AppVersion.cs"
      evidence="Keep official version and add build date plus canonical commit hash."
      ;;
    Program.cs)
      status="replace"
      candidates="Program.cs"
      evidence="Replace WinForms startup with the tested Avalonia entry point."
      ;;
    MainV2.cs|MainV2.Designer.cs)
      status="replace"
      candidates="Views/MainWindow.axaml;Views/MainWindow.axaml.cs;ViewModels/MainWindowViewModel.cs"
      evidence="Main application shell replacement; legacy plugin ABI must be merged into the main assembly first."
      ;;
    GCSViews/FlightData.cs|GCSViews/FlightData.Designer.cs)
      status="replace"
      candidates="GCSViews/FlightDataView.axaml;GCSViews/FlightDataView.axaml.cs;ViewModels/FlightDataViewModel.cs"
      evidence="Flight Data Avalonia replacement with splitter/session/airport fixes."
      ;;
    GCSViews/FlightPlanner.cs|GCSViews/FlightPlanner.Designer.cs)
      status="replace"
      candidates="GCSViews/FlightPlannerView.axaml;GCSViews/FlightPlannerView.axaml.cs;ViewModels/FlightPlannerViewModel.cs"
      evidence="Flight Planner Avalonia replacement."
      ;;
    *)
      logical_path="$native_path"
      logical_path="${logical_path%.Designer.cs}"
      logical_path="${logical_path%.designer.cs}"
      logical_path="${logical_path%.cs}"
      if [[ -v 'native_replacement_by_logical_path[$logical_path]' ]]; then
        status="replace"
        candidates="${native_replacement_by_logical_path[$logical_path]}"
        evidence="Mapped to the listed native Avalonia view/model; feature-level parity and deliberate safety differences are recorded in Porting/Reference/PORT_STATUS.md."
      elif [[ -v 'native_removal_by_logical_path[$logical_path]' ]]; then
        status="remove"
        candidates="Porting/Reference/PORT_STATUS.md"
        evidence="${native_removal_by_logical_path[$logical_path]}"
      elif [[ "$logical_path" == "Antenna/TrackerGeneric" ]]; then
        status="replace"
        candidates="Services/AntennaTrackerOutputs.cs;ViewModels/AntennaTrackerUIViewModel.cs"
        evidence="All three official antenna-tracker output protocols are implemented by the cross-platform tracker service and covered by protocol/lifecycle tests."
      elif [[ "$logical_path" == "Antenna/TrackerUI" ]]; then
        status="replace"
        candidates="Views/AntennaTrackerUIView.axaml;ViewModels/AntennaTrackerUIViewModel.cs;Services/AntennaTrackerOutputs.cs"
        evidence="The native Avalonia tracker view ports live target tracking and all official output interfaces; see Porting/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == Grid/* ]]; then
        status="replace"
        candidates="Views/GridUIView.axaml;ViewModels/GridUIViewModel.cs;ViewModels/SurveyMissionBuilder.cs;Services/SurveyGridSupport.cs"
        evidence="Survey Grid UI, mission generation, split flights, camera profiles and grid persistence are ported and regression-tested; see Porting/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == Joystick/* ]]; then
        status="replace"
        candidates="GCSViews/ConfigurationView/ConfigJoystickView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigJoystickViewModel.cs;Services/JoystickControl.cs;Services/JoystickInput.cs"
        evidence="Joystick setup/actions and the application-level sender have native Linux, Windows and macOS backends with lifecycle/mapping tests; see Porting/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == Log/* ]]; then
        status="replace"
        candidates="Views/LogBrowseView.axaml;ViewModels/LogBrowseViewModel.cs;Views/MavlinkLogWindow.axaml;ViewModels/MavlinkLogConvertViewModel.cs;Services/TlogExportService.cs"
        evidence="DataFlash browsing, download/index and telemetry-log conversion/export workflows are implemented by native Avalonia views/services and tested; see Porting/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == GeoRef/* ]]; then
        status="replace"
        candidates="Views/GeoRefView.axaml;ViewModels/GeoRefViewModel.cs"
        evidence="The GeoRef workflow, matching modes, reports and EXIF output are ported and tested; see Porting/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == "MagCalib" ]]; then
        status="replace"
        candidates="Views/OfflineMagFitWindow.axaml;ViewModels/OfflineMagFitViewModel.cs;Services/OfflineMagFitService.cs"
        evidence="Offline magnetometer calibration for tlog/bin/log is implemented and tested; see Porting/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == Swarm/* ]]; then
        status="replace"
        candidates="Views/FormationControlWindow.axaml;ViewModels/FormationControlViewModel.cs;Views/SwarmFollowPathWindow.axaml;ViewModels/SwarmFollowPathViewModel.cs;Views/SwarmFollowLeaderWindow.axaml;ViewModels/SwarmFollowLeaderViewModel.cs;Views/SwarmWaypointLeaderWindow.axaml;ViewModels/SwarmWaypointLeaderViewModel.cs;Views/SwarmSequenceWindow.axaml;ViewModels/SwarmSequenceViewModel.cs"
        evidence="Formation, Follow Path, Follow Leader, Waypoint Leader and Sequence workflows are ported with exact-link safety and controller tests; see Porting/Reference/PORT_STATUS.md."
      else
        candidates="$(find_candidates "$native_path")"
      fi
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
