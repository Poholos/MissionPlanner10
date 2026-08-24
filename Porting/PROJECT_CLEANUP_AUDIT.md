# Project cleanup audit

Updated: **2026-08-24**.

## Scope and safety rule

Cleanup is isolated on `cleanup/project-audit`; it is not merged into `master`. The branch contains
the completed `port/avalonia-in-place` work and removes an artifact only when its native manifest
row is closed, its replacement is present, and the active build/package graph has no reference to
it. Git history remains the recovery source for every removed file.

The supported product graph is `MissionPlanner.slnx`: one `net10.0` Avalonia application, its
portable plugin contract, the exact transitive libraries used by that application, and the active
Avalonia/plugin regression tests. `MissionPlanner.csproj` still uses an explicit compile list, but
an evaluated MSBuild inventory now leaves no application-owned C# outside the active graph except
the official version source `Properties/AssemblyInfo.cs`; the two `Plugin/PortableApi` sources are
compiled by their own project.

## Removed replaced application remnants

- Removed the closed WinForms implementations for Radio/SiK settings, legacy firmware selection,
  `Script.cs`, the hidden `temp.cs` developer form, old board detection/firmware upload, and the
  unsafe broad HTTP/WebSocket server. Their Avalonia/service replacements and migration decisions
  remain named in `NATIVE_SURFACE.tsv`.
- Removed standalone SiK Radio, Updater, RESX Editor, old test, plugin and WiX project files after
  the native SiK page, signed updater, translation editor, built-in FaceMap/OpenDroneID/Terrain
  Maker/Shortcuts features, and WiX 5 installer had replaced them.
- Removed obsolete embedded SiK firmware/images. The current firmware service downloads current
  board-specific images over HTTPS and validates them before upload; user-facing SiK translation
  RESX files remain preserved.
- Removed the dormant non-shipped Dowding plugin, generated Dowding/Dronelogbook API clients, test
  Node server and plugin-only ONVIF dependency after the dedicated security/function audit proved
  they were not part of the official release graph.
- Removed the inherited WinForms `.sln`, the old net472 tests for deleted code, legacy MSI/driver
  scripts, AppX manifest/assets, bundled ADB binaries, old `NuGet.exe` bootstrap, and developer-only
  batch/signing remnants. Current builds use the SDK, `MissionPlanner.slnx`, WiX 5 and GitHub CI.

## Removed alternate application experiments

The repository previously contained several independent application stacks in addition to the
main product. They were not libraries consumed by Avalonia and could not be validated as part of
Mission Planner:

- the EOL Xamarin Android/iOS/macOS/UWP application and its `MissionPlannerLib` aggregation graph;
- generated Uno/XAML conversion experiments;
- the old Blazor WebAssembly/Cesium application snapshot;
- the retired Windows Store packaging project;
- the Xamarin-only SkiaSharp WinForms renderer.

The associated manual Android/Apple workflows and mobile-only copied resources were removed with
those graphs. Linux, Windows and macOS desktop support is provided by the single Avalonia project
and its current package workflows; this cleanup does not claim Android/iOS product support.

## Build and warning fixes

- `MissionPlanner.slnx` now lists the exact active transitive project graph instead of hiding most
  libraries behind project-reference discovery.
- Disabled inherited `GeneratePackageOnBuild` for alglibnet, netDxf, MAVLink and SharpZipLib. They
  are internal project references; release automation does not publish their incidental NuGet
  packages or consume the resulting readme/license warnings.
- Restricted the PE application icon property to Windows/RID builds while preserving the same icon
  as an Avalonia resource on all platforms.
- Kept transitive NuGet auditing enabled and promoted `NU1901` through `NU1904` to errors. The
  active graph reports no known vulnerable packages.
- The `temp.cs` regression now validates the frozen 68-handler audit directly, so preserving the
  obsolete 1,400-line WinForms form is no longer required merely to count its methods.

The Release compiler build is strictly zero-warning and zero-error. `dotnet format ... analyzers`
also exits successfully with zero analyzer diagnostics and changes zero files. At diagnostic
verbosity, its .NET 10 MSBuildWorkspace loader still prints `Found project reference without a
matching metadata reference` for inherited netstandard project-reference edges; the same projects
resolve, compile and audit successfully in the authoritative build. These loader notices are not
suppressed or misreported as source warnings.

## Security scan triage

CodeQL run `32688021913` completes successfully for cleanup commit `c66b5335b`. Its branch-specific
API result contains five open but reviewed alerts and no untriaged alert:

- #1 is a generic writer in vendored netDxf. The Avalonia application reads DXF overlays and has no
  path to `DxfDocument.Save`; retain the reader and re-evaluate this flow if DXF export is added.
- #2 and #3 are the two overload paths of intentional operator-selected parameter-file export.
  Raw Parameters and DroneCAN both require `Dialogs.ConfirmDangerous` with reject as the default.
- #4 is intentional operator-selected decoded tlog export. Every UI entry point uses the same
  reject-by-default warning for precise coordinates, identifiers, missions, network details and
  parameters before the service writes a local file.
- #5 is the required ECB block primitive inside SharpZipLib's interoperable WinZip AES-CTR
  construction. It encrypts an incrementing nonce into a keystream and authenticates ciphertext;
  application data is not encrypted as plain ECB.

The alerts are not dismissed or hidden merely to produce an empty dashboard. Any GitHub dismissal
must preserve these reasons and remains a separate explicitly authorized review action. The older
source-port alert numbering and decisions remain immutable in `Reference/CODEQL_TRIAGE.md`.

## Intentionally retained

- `Scripts/` contains 19 official Mission Planner IronPython examples. They are operator scripts,
  not build scripts, and are supported by `Services/PythonScriptHost.cs`; the directory is not
  cleanup material.
- Existing neutral and translated RESX files remain source data for the native translation editor.
  A source-less directory containing RESX files is therefore not automatically unused. Only empty
  Visual Studio resource templates or resources belonging to an explicitly retired feature were
  deleted and marked `remove` in the manifest.
- `NoFly/` contains four official KML/KMZ datasets. They are flight-domain data, not generated
  output, so they remain pending an explicit default-data packaging policy.
- `APMPlannerXplanes/` is the small native X-Plane/HIL bridge, not an abandoned UI port. It remains
  separate from the desktop application by design.
- Project/solution/make metadata below remaining `ExtLibs/` belongs to vendored libraries,
  protocol/code generators, hardware helpers or independently meaningful tools. The active subset
  is explicit in `MissionPlanner.slnx`; blanket deletion of the rest would make future upstream
  updates or specialized tools harder without proving that the files are meaningless.
- `ExtLibs/mono` is an existing upstream submodule unrelated to the removed application ports and
  is intentionally untouched.
- `Properties/AssemblyInfo.cs` remains the authoritative upstream Mission Planner version source;
  build date and Git commit are appended by the native version pipeline.

## Generated local output

`bin/`, `obj/`, `TestResults/`, `out/`, `dist/`, `upload/` and `__pycache__/` are ignored and must
never be committed. They are reproducible diagnostic/package output rather than source cleanup
targets.

## Reproduction gates

```bash
./build/porting/check-native-surface.sh
dotnet restore MissionPlanner.slnx --force --nologo
dotnet build MissionPlanner.slnx -c Release -m:1 --nologo
dotnet format MissionPlanner.slnx analyzers --verify-no-changes --no-restore
dotnet test MissionPlannerTests/Avalonia/MissionPlanner.Tests/MissionPlanner.Tests.csproj \
  -c Release -m:1 --no-build --nologo
dotnet list MissionPlanner.slnx package --vulnerable --include-transitive --no-restore
make linux-packages
make windows-zip
```

CI run `32688021866` passes all host-native gates: Windows ZIP/MSI build plus default-path
install/file checks/uninstall, both macOS architectures, and Linux build/test/package/smoke. Its
five named artifact bundles are present in GitHub Actions.
