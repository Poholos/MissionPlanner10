# Avalonia in-place migration status

Updated: **2026-08-24**.

## Current state

- The complete in-place Avalonia migration and audited cleanup were merged to `master` through
  PR #1 at merge commit `eb6cfe28f`. The later GTU `NV5Settings` synchronization was merged through
  PR #2, the independent diversity-radio key correction through PR #4, and the clean CI identity
  correction through PR #5 at master checkpoint `d273ca8aa`. PR #6 carries the final GTU
  `NV5Settings` refinement (`639a19acc`), focused CodeQL/AES follow-up (`c3265e3e8`) and explicit
  secure dependency declarations (`2e26a52a3`). Native baseline `67a3c4f` remains the immutable
  rollback reference in Git history.
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
  decisions and reproduction commands are recorded in `WARNING_AUDIT.md`. All **1263/1263**
  Avalonia tests pass on Linux. A 12-second Xvfb launch reaches the normal Avalonia event loop with
  no console errors.
- Informational version is derived from the current native Mission Planner version and formatted as
  `1.3.83+YYYYMMDD.<commit>`; dirty developer builds append `.dirty`.
- CI packages explicitly pin that clean commit identity before compilation. Runner-local files
  created by restore/build therefore cannot add a misleading `.dirty` suffix to Linux or Windows
  package names or to the application metadata embedded in any of the four platform builds; CI
  rejects a package if that suffix reappears.
- Native-identity packaging is integrated for Linux `.tar.gz`/`.deb`, Windows portable ZIP/MSI and
  macOS x64/arm64 app archives. All four RID publishes pass locally; the Linux packages pass
  `lintian` and extracted-DEB Xvfb smoke, while both macOS outputs contain architecture-correct
  pinned VLC/SimpleBLE runtimes. Details and signing boundaries are in `RELEASE.md`.
- Stable and beta auto-updates now select signed manifests directly from this fork's GitHub
  Releases. The matching Ed25519 private key is present only as the repository secret
  `UPDATE_SIGNING_KEY`; the committed public key is verified again during release.
- NV key handling is synchronized through GTU `NV5Settings` commit `77af510a` on clean GTU
  checkpoint `6c2a4b04`. NV5 accepts exactly 32 hexadecimal
  digits, displays uppercase, and maps the 16 raw bytes to four big-endian MAVLink `INT32` words.
  Ordinary Save writes edited words as exact typed `PARAM_SET` operations; explicit SET KEY uses
  the idempotent post-persistence `NV_ENCRYPTION_KEYS_SET`/`NV_ENCRYPTION_KEYS_ACK` transaction.
  Receive diversity does not mirror or couple keys: generation, staging and SET KEY target only
  the selected radio, allowing different keys on Radio 1 and Radio 2. NV4 generation now uses 32
  random bytes displayed as 64 uppercase hexadecimal digits, retains compatible printable/hex
  input, writes eight signed words plus singular `REFRESH_SETTING`, and locks ineffective
  `ENC_KEY_BITS` edits to 128.
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
- Post-merge master CI run `32713804092` passed the complete build/test graph, Linux DEB/TAR with
  lintian and extracted-payload smoke, Windows ZIP/MSI with real install/file validation/uninstall,
  and both macOS archives. Master CodeQL run `32713804084` also passed; the five open results are
  the same fully triaged findings recorded before merge, with no new NV5Settings finding.
- NV5 diversity hotfix master CI run `32716876527` and CodeQL run `32716876412` both passed, but the
  CI package run was superseded for distribution because runner-local state leaked `.dirty` into
  its Linux and Windows filenames. The clean-CI identity gate above is the corrective action; those
  superseded files are diagnostic artifacts rather than release candidates.
- Clean-identity master CI run `32719669739` and CodeQL run `32719669757` both passed. Its Linux,
  Windows and application metadata use clean `1.3.83-20260824.d273ca8a`/
  `1.3.83+20260824.d273ca8a` identities with no false `.dirty` suffix.
- PR #6 code checkpoint CI run `32721719954` passed Linux build/tests/DEB/TAR, real Windows
  MSI install/uninstall plus ZIP, and both macOS packages. CodeQL run `32721719966` passed and the
  branch-specific code-scanning API reports zero open alerts.
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
- The final artifact pass classifies every inactive project/solution, committed binary and
  key/certificate container. Six generated WinForms `.datasource` files, obsolete project trees,
  stale binary duplicates and unreferenced development keys are removed only through explicit,
  machine-checked audit rows. Operator scripts, `Lib.zip`, the swarm Blender authoring helper,
  generators, X-Plane bridge and conditional Windows payloads remain for documented reasons.
- The old Python 2/py2exe automatic log analyzer has been replaced in-process. Its 17 enabled
  official diagnostics now run cross-platform, have deterministic regression tests and report
  missing data independently; optical-flow recommendations no longer write a parameter file
  silently into the working directory.
- Claude remains temporarily disabled by user instruction.

## GTU synchronization checkpoint

- NV modem behavior was last compared with `/home/alex/src/AgroSky/GTU` at clean local and fetched
  `origin/master` `6c2a4b04f03fa4e693d8e6adc2b39b734e817856`. The only `NV5Settings`
  source/header/UI/test change after `98e98833` is committed GTU refinement
  `77af510a47f8cbe7ea02fcc047019b07fb2c0c26`: selected-radio key targeting remains independent
  of `DIVERSITY`, and **Revert selected** restores one staged parameter locally without sending
  MAVLink. Both behaviors and their regression tests are ported. `REFRESH_SETTING` remains a
  typed `UINT32`; NV5 key words remain signed `INT32` values preserving the same raw bytes.
- Before each later NV modem change and before a release, recheck both committed and uncommitted
  GTU changes with `git status`, then compare every newer change to `hermes-gui/include/nv5settings.h`,
  `hermes-gui/src/nv5settings.cpp` and `hermes-gui/test/testnv5settings.cpp`. Update this commit and
  the NV regression tests whenever the source behavior advances.

## Cleanup audit

- Completed functional commit `eaf456665` passed packaging run `32685680444`: real default-path MSI
  install/file checks/uninstall, Linux DEB/TAR with lintian and extracted-payload smoke, and both
  macOS architectures all succeeded. CodeQL run `32685680428` succeeded with zero open alerts.
- `cleanup/project-audit` was the isolated review branch and was merged through PR #1 after the
  explicit user decision. It removes closed WinForms sources,
  replaced standalone projects, EOL alternate application stacks, generated proprietary API
  clients and obsolete launch/deploy/CI/binary artifacts; exact decisions and retained areas are in
  `PROJECT_CLEANUP_AUDIT.md`.
- `MissionPlanner.slnx` now names the complete active transitive graph. Its Release build has zero
  warnings/errors, analyzer verification has zero diagnostics (the .NET 10 workspace-loader notices
  are documented separately), NuGet reports no vulnerable packages, the native manifest has zero
  blockers and all 1263 tests pass after cleanup plus the later NV5Settings and security regression
  coverage.
- Clean-commit Linux TAR/DEB and Windows ZIP packaging succeeds after cleanup. The DEB passes
  `lintian`, payload assertions and a 12-second Xvfb launch; the Windows archive contains the
  expected self-contained `win-x64` application. CI run `32688021866` also passes Windows ZIP/MSI
  build, default-path install/file checks/uninstall, both macOS architectures and all Linux gates;
  all five named artifact bundles are present.
- The five formerly open, fully reviewed CodeQL findings now carry narrow source-level rationale at
  the exact sinks: one unreachable vendored netDxf writer, three explicit operator-selected local
  exports protected by reject-by-default warnings, and the required AES block primitive in
  SharpZipLib's WinZip AES-CTR construction. The AES code also fixes a real independent AES-256
  IV-size defect and has round-trip coverage for both supported key sizes. No alert was broadly or
  repository-wide suppressed; exact decisions remain in `PROJECT_CLEANUP_AUDIT.md`. PR #6 CodeQL
  run `32721719966` confirms zero open alerts on the branch.
- The two remaining Secret Scanning warnings were inherited Mapbox values in removed official
  Mission Planner/Xamarin/Cesium history. At the user's explicit request they are resolved as
  `wont_fix`, not falsely marked revoked; the audit comments preserve the origin and the decision
  not to rewrite published upstream-derived history.
- `Scripts/`, localization RESX, NoFly data, the X-Plane/HIL bridge and independently meaningful
  remaining library/generator projects are deliberately retained; non-inclusion in the active
  solution alone is not deletion evidence. The former `ExtLibs/mono` submodule was removed only
  after every reference was shown to come from the retired WinForms project graph.

## Immediate next step

Continue functional parity work from the merged Avalonia `master` in focused feature branches.
Before a release, repeat physical UDP/TCP/UART acceptance with representative NV4/NV5 hardware
and recheck GTU `NV5Settings` changes newer than clean checkpoint `6c2a4b04`.

## Acceptance baseline

- At least 1263 port tests retained and passing.
- Clean Release build has zero errors and zero warnings.
- `linux-x64`, `win-x64`, `osx-x64`, and `osx-arm64` publish gates pass.
- Linux `.deb` and portable archive build and smoke successfully.
- CodeQL has no untriaged alerts.
- No live source/build/runtime reference to `external/MissionPlanner` remains.
- Parameter/session safety, NV modem behavior, speech serialization, airport alpha, movable Flight
  Data splitter, plugin lifecycle and localization coverage do not regress.
