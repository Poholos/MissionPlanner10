# Avalonia in-place migration status

Updated: **2026-08-24**.

## Current state

- Migration is active on `port/avalonia-in-place` at native baseline `67a3c4f`.
- Both source repositories were clean and matched the frozen commits at start.
- The manifest generator and migration control documents are being established before source
  replacement.
- No native WinForms source, RESX translation, project, or plugin has been deleted.
- Claude remains temporarily disabled by user instruction.

## Immediate next step

Generate and audit the native-surface and port-source manifests, then import the first root-project
slice with one `MissionPlanner` assembly and direct native `ExtLibs` references. Preserve the
existing port tests during this conversion and fix their fixed-UDP-port baseline flake when moved.

## Acceptance baseline

- At least 1139 port tests retained and passing.
- Release build has zero errors and reviewed warnings.
- `linux-x64`, `win-x64`, `osx-x64`, and `osx-arm64` publish gates pass.
- Linux `.deb` and portable archive build and smoke successfully.
- CodeQL has no untriaged alerts.
- No live source/build/runtime reference to `external/MissionPlanner` remains.
- Parameter/session safety, NV modem behavior, speech serialization, airport alpha, movable Flight
  Data splitter, plugin lifecycle and localization coverage do not regress.
