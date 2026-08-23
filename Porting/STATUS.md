# Avalonia in-place migration status

Updated: **2026-08-24**.

## Current state

- Migration is active on `port/avalonia-in-place`; rollback remains the untouched native baseline
  `67a3c4f` and the control-plane commit is published as `ba034cb5`.
- The root `MissionPlanner.csproj` is now the net10 Avalonia application with assembly and product
  identity `MissionPlanner`. It builds one main `MissionPlanner.dll` and has no source, build or
  runtime dependency on an `external/MissionPlanner` tree.
- 519 application-owned files were imported directly into the canonical native paths from pinned
  port commit `8ed19081`, with source blob provenance in `IMPORTED_APPLICATION.tsv` and an explicit
  transitional compile allow-list in `ImportedApplicationItems.props`.
- The root project references the native `ExtLibs` directly. CoreCLR fixes are applied directly to
  native `Settings`, `MAVState`, Radio uploader/IHex and Grid sources; there is no generated patch
  assembly.
- The official legacy plugin ABI is compiled into the main `MissionPlanner` assembly. The separate
  `MissionPlannerAvalonia.PluginApi.dll` is only the distinctly named portable plugin contract, so
  the output does not contain a second compatibility `MissionPlanner.dll`.
- `MissionPlanner.slnx` and 115 imported test-source files now live below
  `MissionPlannerTests/Avalonia`; their five adapted projects reference the root application and
  native libraries directly. The UDP transport fixture uses an ephemeral listener port instead of
  colliding with a live modem on 14550.
- Six pinned historical audits are preserved in `Porting/Reference` with a blob manifest. They are
  migration evidence, not a copied source tree.
- Release build of the test graph succeeds with zero warnings and zero errors. All **1139/1139**
  imported tests pass on Linux. A 12-second Xvfb launch reaches the normal Avalonia event loop with
  no console errors.
- Informational version is derived from the current native Mission Planner version and formatted as
  `1.3.83+YYYYMMDD.<commit>`; dirty developer builds append `.dirty`.
- No native WinForms source, RESX translation, project or plugin has been deleted. The native
  manifest still exposes 454 `unported-blocker` rows that require code-level classification before
  final cut-over is declared complete.
- Claude remains temporarily disabled by user instruction.

## Immediate next step

Commit and publish this verified application/test import. Then audit the 156 warnings seen during a
clean inherited-`ExtLibs` rebuild, import and rename the packaging/runtime support for the native
`MissionPlanner` identity, run all four RID publish gates, and produce the Linux portable archive
and `.deb`. README/CI/CodeQL/release workflow reconciliation follows before any merge to `master`.

## Acceptance baseline

- At least 1139 port tests retained and passing.
- Release build has zero errors and reviewed warnings.
- `linux-x64`, `win-x64`, `osx-x64`, and `osx-arm64` publish gates pass.
- Linux `.deb` and portable archive build and smoke successfully.
- CodeQL has no untriaged alerts.
- No live source/build/runtime reference to `external/MissionPlanner` remains.
- Parameter/session safety, NV modem behavior, speech serialization, airport alpha, movable Flight
  Data splitter, plugin lifecycle and localization coverage do not regress.
