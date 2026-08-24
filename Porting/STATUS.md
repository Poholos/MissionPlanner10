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
- A clean Release build of the complete test graph succeeds with zero warnings and zero errors
  after resolving all 156 inherited `ExtLibs` diagnostics without a repository-wide `NoWarn`; the
  decisions and reproduction commands are recorded in `WARNING_AUDIT.md`. All **1198/1198**
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
- CI, CodeQL, Dependabot and tag-release workflows are reconciled with the in-place tree. Legacy
  WinForms/Xamarin workflows remain available manually but no longer run against every port push.
- The frozen native inventory remains complete in `NATIVE_SURFACE.tsv`, while replaced WinForms
  sources are explicitly mapped to tested Avalonia artifacts and selected source files whose
  behavior is fully superseded have been removed. RESX translations remain preserved. The manifest
  now exposes **11** `unported-blocker` rows: Python scripting; legacy firmware/board detection and
  firmware selection; the embedded HTTP service; Dowding; and old WiX
  bootstrap sources. Obsolete directories, standalone projects and other build-system remnants are
  intentionally deferred to `cleanup/project-audit` after these functional blockers are closed.
- Claude remains temporarily disabled by user instruction.

## GTU synchronization checkpoint

- NV modem behavior was last compared with `/home/alex/src/AgroSky/GTU` at commit
  `0ae813004079bd46d63d708966b7eff266ad5949` (`feat: use NV5 encryption key words`). The GTU
  worktree was clean; its local `master` was one commit ahead of `origin/master`.
- Before each later NV modem change and before a release, recheck both committed and uncommitted
  GTU changes with `git status`, then compare every newer change to `hermes-gui/include/nv5settings.h`,
  `hermes-gui/src/nv5settings.cpp` and `hermes-gui/test/testnv5settings.cpp`. Update this commit and
  the NV regression tests whenever the source behavior advances.

## Immediate next step

Close the remaining 11 functional manifest blockers without mixing repository cleanup into this
branch. Then require the GitHub run to perform a real default-path MSI install/uninstall, build/sign
both `.app` archives on macOS, repeat Linux package smoke and complete CodeQL. Once functional and
packaging gates are green, run the conservative unused-file/directory/build-system audit on the
separate `cleanup/project-audit` branch.

## Acceptance baseline

- At least 1198 port tests retained and passing.
- Clean Release build has zero errors and zero warnings.
- `linux-x64`, `win-x64`, `osx-x64`, and `osx-arm64` publish gates pass.
- Linux `.deb` and portable archive build and smoke successfully.
- CodeQL has no untriaged alerts.
- No live source/build/runtime reference to `external/MissionPlanner` remains.
- Parameter/session safety, NV modem behavior, speech serialization, airport alpha, movable Flight
  Data splitter, plugin lifecycle and localization coverage do not regress.
