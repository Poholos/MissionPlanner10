# Avalonia in-place migration status

Updated: **2026-08-24**.

## Current state

- Functional migration is published on `port/avalonia-in-place` at `eaf456665`; the isolated
  `cleanup/project-audit` branch contains merge `a644cc4f7` and cleanup commit `08bc7e95e`.
  Rollback remains the untouched native baseline `67a3c4f`; `master` is not modified.
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
- A clean Release build of the complete test graph succeeds with zero warnings and zero errors
  after resolving all 156 inherited `ExtLibs` diagnostics without a repository-wide `NoWarn`; the
  decisions and reproduction commands are recorded in `WARNING_AUDIT.md`. All **1224/1224**
  Avalonia tests pass on Linux. A 12-second Xvfb launch reaches the normal Avalonia event loop with
  no console errors.
- Informational version is derived from the current native Mission Planner version and formatted as
  `1.3.83+YYYYMMDD.<commit>`; dirty developer builds append `.dirty`.
- Native-identity packaging is integrated for Linux `.tar.gz`/`.deb`, Windows portable ZIP/MSI and
  macOS x64/arm64 app archives. All four RID publishes pass locally; the Linux packages pass
  `lintian` and extracted-DEB Xvfb smoke, while both macOS outputs contain architecture-correct
  pinned VLC/SimpleBLE runtimes. Details and signing boundaries are in `RELEASE.md`.
- Stable and beta auto-updates now select signed manifests directly from this fork's GitHub
  Releases. The matching Ed25519 private key is present only as the repository secret
  `UPDATE_SIGNING_KEY`; the committed public key is verified again during release.
- NV5 encryption-key handling is synchronized with GTU commit `0ae81300`: the UI generates and
  displays exactly 32 uppercase hexadecimal digits, accepts only a 32-digit hexadecimal NV5 key,
  and maps it to four big-endian MAVLink `UINT32` words (`CHx_KEY_W0..W3`). A complete one- or
  two-radio snapshot is persisted through one idempotent `NV_ENCRYPTION_KEYS_SET`/
  `NV_ENCRYPTION_KEYS_ACK` transaction rather than four independent parameter writes per radio.
  NV4 retains its eight signed words followed by singular `REFRESH_SETTING`,
  whose write type is verified as `UINT32`.
- The firmware pages now retain the official modern and legacy safe upload paths: APJ/PX4/VRX
  bootloader upload with board-id matching, STM32 DFU/HEX/BIN, and APM1/APM2 STK500/STKv2 with
  readback verification. The Legacy manifest selector exposes platform and a functional format
  filter, including the still-published APM HEX images. Explicit target/port selection replaces the
  unsafe multi-device assumptions in old `BoardDetect`; obsolete Parrot/Solo network installers are
  reported as unsupported rather than routed to the wrong programmer.
- Flight Data local scripting now preserves the official IronPython 3.4.2 `.py` workflow and its
  live `MAV`/`cs`/`Ports`/`Script` bindings. Output is streamed into the Avalonia page and Abort uses
  a cooperative per-line trace hook instead of the original unsupported `Thread.Abort`. The local
  MoonSharp console remains available as a separate optional Lua tool.
- Flight Planner's visible live KML workflow is restored by an on-demand, loopback-only read server
  with bounded headers, responses and concurrency. It serves live vehicle/mission KML and the
  aircraft model on port 56781 while deliberately excluding the old public bind, guided-mode HTTP
  writes, raw MAVLink WebSocket, Mavelous host and WinForms/GDI MJPEG capture surface.
- CI, CodeQL, Dependabot and tag-release workflows are reconciled with the in-place tree. The EOL
  Xamarin/Uno/Blazor/Windows Store application experiments and their manual mobile workflows are
  removed on the cleanup branch; supported packaging remains Avalonia desktop for Linux, Windows
  and both macOS architectures.
- The frozen native inventory remains complete in `NATIVE_SURFACE.tsv`, while replaced WinForms
  sources are explicitly mapped to tested Avalonia artifacts and selected source files whose
  behavior is fully superseded have been removed. RESX translations remain preserved. The manifest
  now exposes **0** `unported-blocker` rows. The old WiX generator is explicitly mapped to the
  current WiX 5 packaging/version/CI implementation; its private upload commands and
  certificate/DPInst custom actions are intentionally retired. The experimental Dowding project
  was never selected by the upstream solution build; its general tracker, CoT and multi-vehicle map
  workflows are ported while the dormant proprietary integration is classified in
  `Reference/DOWDING_AUDIT.md` and removed with its generated clients/ONVIF dependency on the
  cleanup branch. Replaced standalone projects and alternate application/build-system remnants are
  classified in `PROJECT_CLEANUP_AUDIT.md` before deletion.
- Claude remains temporarily disabled by user instruction.

## GTU synchronization checkpoint

- NV modem behavior was last compared with `/home/alex/src/AgroSky/GTU` at commit
  `0ae813004079bd46d63d708966b7eff266ad5949` (`feat: use NV5 encryption key words`). The GTU
  worktree was clean and its local `master` matched `origin/master`.
- Before each later NV modem change and before a release, recheck both committed and uncommitted
  GTU changes with `git status`, then compare every newer change to `hermes-gui/include/nv5settings.h`,
  `hermes-gui/src/nv5settings.cpp` and `hermes-gui/test/testnv5settings.cpp`. Update this commit and
  the NV regression tests whenever the source behavior advances.

## Cleanup audit

- Completed functional commit `eaf456665` passed packaging run `32685680444`: real default-path MSI
  install/file checks/uninstall, Linux DEB/TAR with lintian and extracted-payload smoke, and both
  macOS architectures all succeeded. CodeQL run `32685680428` succeeded with zero open alerts.
- `cleanup/project-audit` is the isolated follow-up branch. It removes closed WinForms sources,
  replaced standalone projects, EOL alternate application stacks, generated proprietary API
  clients and obsolete launch/deploy/CI/binary artifacts; exact decisions and retained areas are in
  `PROJECT_CLEANUP_AUDIT.md`.
- `MissionPlanner.slnx` now names the complete active transitive graph. Its Release build has zero
  warnings/errors, analyzer verification has zero diagnostics (the .NET 10 workspace-loader notices
  are documented separately), NuGet reports no vulnerable packages, the native manifest has zero
  blockers and all 1224 tests pass after cleanup.
- Clean-commit Linux TAR/DEB and Windows ZIP packaging succeeds after cleanup. The DEB passes
  `lintian`, payload assertions and a 12-second Xvfb launch; the Windows archive contains the
  expected self-contained `win-x64` application. CI run `32688021866` also passes Windows ZIP/MSI
  build, default-path install/file checks/uninstall, both macOS architectures and all Linux gates;
  all five named artifact bundles are present.
- CodeQL run `32688021913` succeeds. The branch has five open but fully reviewed alerts and zero
  untriaged alerts: one unreachable vendored netDxf writer, three explicit operator-selected local
  export flows already protected by reject-by-default warnings, and the required AES block
  primitive in SharpZipLib's WinZip AES-CTR construction. `PROJECT_CLEANUP_AUDIT.md` records the
  current alert numbers and exact decisions; none was dismissed merely to empty the dashboard.
- `Scripts/`, localization RESX, NoFly data, the X-Plane/HIL bridge, `ExtLibs/mono` and independently
  meaningful remaining library/generator projects are deliberately retained; non-inclusion in the
  active solution alone is not deletion evidence.

## Immediate next step

Review the isolated `cleanup/project-audit` deletion set and its audit before proposing it for
merge. Do not merge this destructive cleanup into `master` without an explicit review decision.

## Acceptance baseline

- At least 1224 port tests retained and passing.
- Clean Release build has zero errors and zero warnings.
- `linux-x64`, `win-x64`, `osx-x64`, and `osx-arm64` publish gates pass.
- Linux `.deb` and portable archive build and smoke successfully.
- CodeQL has no untriaged alerts.
- No live source/build/runtime reference to `external/MissionPlanner` remains.
- Parameter/session safety, NV modem behavior, speech serialization, airport alpha, movable Flight
  Data splitter, plugin lifecycle and localization coverage do not regress.
