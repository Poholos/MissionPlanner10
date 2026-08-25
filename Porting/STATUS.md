# Avalonia in-place migration status

Updated: **2026-08-25**.

## Live UDP/NV, SITL/X-Plane and Mission Planner 10 checkpoint

- Local branch `fix/udp-nv-connect-state` is at code checkpoint
  `5e6c1c8fc0e5234e243e7e914c56e63190757315`. The new atomic commits retain the first SITL TCP
  connection (`f0e407dca`), drain short packets from persistent UDP listeners (`1813353ef`),
  restore and filter the complete NV parameter cache (`2d4205e6e`), apply the Mission Planner 10
  product/installer identity (`8caed8f30`), migrate legacy SITL/SRTM caches while preserving Unix
  executable modes (`4145b5bb1`) and reject reflected GCS RTSP requests as modem identities
  (`5e6c1c8fc`). The managed assembly intentionally remains
  `MissionPlanner.dll` for plugin ABI compatibility; apphosts, install directories and package
  names are Mission Planner 10. The official Mission Planner filesystem map-tile location remains
  shared and is not migrated or renamed.
- The final UDP-reader stall was traced backwards to the initial native Avalonia import
  `c68d7f366`: its generic stream loop required `BytesToRead > 10`. `UdpSerial` exposes datagram
  availability, so a short packet at the head of a bound listener could prevent every later
  broadcast from being consumed. `402548abc` fixed the separate initial parameter-load ordering,
  `8519ff5b9` correctly made inbound UDP persistent but exposed the inherited reader starvation as
  an open-yet-dead socket, and `c13d14962` fixed a separate successful-progress-dialog cancellation
  that released a newly opened transport. The current policy uses any positive byte count for a
  bound UDP listener while retaining the legacy threshold for TCP/UART/UDP client streams; it is
  used by both primary and secondary connection runtimes.
- Real hardware verification used the actual shared UDP port 14565 under Xvfb and physical XTest
  clicks through `SETUP -> NV Modem -> Refresh selected`. The socket stayed `Recv-Q=0`, `drops=0`
  for the 30-second observation and through repeated parameter refreshes. The page listed exactly
  five vendor-confirmed broadcasts and no autopilot: NV5 `10:18` (91/91), `255:11` (91/91), `6:6`
  (77/77), `255:10` (59/59) and `1:5` (77/77). Cached `MAVState.param` values are replayed only
  after an NV custom message, valid modem-info payload or strict legacy NV4 CAN passport confirms
  the exact system/component endpoint; parameter names alone can no longer classify an autopilot
  as a modem. Closing the live window produces no former `CancellationTokenSource` disposal stack
  or core dump and the final Xvfb/metacity run exits 0.
- A follow-up hardware report exposed a sixth false device at `255:190`, Mission Planner's own
  `MAV_COMP_ID_MISSIONPLANNER` endpoint. The captured tlog proved that the reflected custom frame
  was an outbound `NV5_RTSP_CONFIG` read request (`operation=0`), not modem status. Read/write
  operations 0/1 can no longer establish identity; only the modem report operation 2 can. This is
  payload-direction filtering rather than an ID blacklist: a real modem at `255:190` is still
  accepted when it sends a valid report. A second live dropdown check shows only the five physical
  modems and keeps `Recv-Q=0`.
- The X-Plane/SITL regression came from using a temporary readiness connection and then opening a
  second TCP session. External simulator models can accept/associate the first session, so the
  launcher now retains that proven connection and hands it directly to Mission Planner. The child
  PATH includes the SITL cache and working directory for companion runtime files. A real cached
  Linux `ArduPlane --model xplane` integration run passes, including legacy-cache migration and
  retained-stream consumption; no SITL process remains after the test.
- Verification at the code checkpoint: Release solution build **0 warnings / 0 errors**; complete
  suite **1457/1457**; focused connection/NV suite **94/94**; real X-Plane integration plus cache
  migration **2/2**; all six migration/inventory checks pass (1623 native rows with 0 blockers,
  708/708 source paths, no WinForms, and clean project/binary/key audits); all 28 active projects
  report no known vulnerable direct or transitive NuGet package. One earlier full-suite run had a
  transient Avalonia headless `Dispatcher.PushFrame` platform exception in a shapefile test; that
  test passed immediately in isolation and the following two complete builds/runs passed, so it is
  recorded as test-harness flakiness rather than hidden.
- Fresh clean local artifacts are
  `out/packages/missionplanner10_1.3.83-20260825.5e6c1c8f_amd64.deb` (60,115,816 bytes, SHA-256
  `81592f6cc4ac788fc20fdc94c459b7f97a69a7eb818459b571da51f6d147e71f`) and
  `out/packages/MissionPlanner10-1.3.83-20260825.5e6c1c8f-linux-x64.tar.gz` (75,376,646 bytes,
  SHA-256 `a6810274a4f79dd52a36cf6f3be6c707c54fc2ca995006c60face03f46cd81f0`). `lintian
  --fail-on error,warning`, gzip/TAR content checks, extracted DEB layout and a 12-second packaged
  Xvfb smoke pass; the expected smoke timeout is 124 with no stderr, unhandled exception or crash
  log.
- Remaining publication step is intentionally deferred: the user cancelled push/merge/release.
  No GitHub state was changed. When publication is explicitly re-authorized, the next executable
  step is to push this branch, review/merge it into master, require Linux/Windows/macOS CI plus
  CodeQL to pass, and only then create the tag/release so native Windows MSI and macOS DMG are
  generated on their required hosts. Claude remains disabled by user instruction.

## Shared UDP/NV connection-state fix

- Stacked branch `fix/udp-nv-connect-state` contains the earlier DroneCAN fix, initial UDP
  publication fix `402548abc11141016ec65dcd0a8543e196820ec7`, and persistent-listener fix
  `8519ff5b9`. Fresh hardware traces first proved that the inbound UDP listener on port 14565
  continued receiving NV broadcasts while the main button remained `CONNECT`: synchronous initial
  parameter loading had selected an arbitrary first endpoint and delayed logical connection
  registration until that endpoint answered.
- Inbound `UdpSerial` parameter loading is now always background work. A valid MAVLink open can
  therefore mark the shared transport connected, expose `DISCONNECT`, start the reader and publish
  the link to the NV Modem page without requiring the first broadcast `sysid:compid` to implement
  or answer `PARAM_REQUEST_LIST`. The configured policy for point-to-point UDP client, TCP and UART
  connections is unchanged.
- A second hardware run exposed the independent disconnect regression. Two fresh logs contained
  681 and 673 valid frames from multiple NV endpoints over about six seconds, including valid
  traffic immediately before teardown. The cause was the generic ten-second silent-link policy
  introduced in the old Avalonia port by `51cd6ca969861fa712742c23d04afc461bc2117b`
  (`feat: expand vehicle control and telemetry parity`) and imported into the native tree by
  `c68d7f36675748fa7f80f4f880ab71e9fe42c8a4`. It treated a bound UDP discovery socket as a
  point-to-point vehicle session and closed it when an exclusive parameter read ended after the
  selected target had been silent.
- Inbound `UdpSerial` is now a persistent listener: device silence and repeated packet-reader
  errors set link quality lost but never close the bound socket. A returning modem is discovered
  without reopening the port. Explicit Disconnect/shutdown and an actually closed OS socket still
  end the logical connection. The same rule covers primary and Connection List UDP listeners;
  `UdpSerialConnect`, TCP and UART retain the existing dead-link cleanup.
- This mattered beyond the NV page: teardown reset every vehicle parameter list, closed telemetry
  logs, stopped speech, removed the link from `MavLinkConnectionManager` snapshots, raised the app
  as disconnected and therefore hid the source from NV discovery, DroneCAN, map/multi-link and
  connection-gated services. The scoped policy prevents those cascades for a merely silent inbound
  listener without weakening point-to-point failure handling.
- Three deterministic regressions cover the silent listener policy, repeated reader errors and a
  real UDP socket that remains bound while a modem disappears and then accepts it when it returns.
  A temporary live-hardware test also held the real port 14565 open for 20 seconds past the former
  failure point. Focused connection tests pass **58/58**, the complete suite passes **1445/1445**,
  and the Release solution builds with **0 warnings / 0 errors**. Clean local packages are
  `out/packages/missionplanner_1.3.83-20260825.8519ff5b_amd64.deb` (60,115,152 bytes, SHA-256
  `5ebc58e044703140492ff3dc819127fd8df47db74473640b0caad80cc5fde18f`) and
  `out/packages/MissionPlanner-1.3.83-20260825.8519ff5b-linux-x64.tar.gz` (75,373,478 bytes,
  SHA-256 `0071fe7d249dcbb1dcd28bd4c422a85394ad3d073154a4a6566800a6545d11c2`). `lintian`, extracted
  payload assertions and the 12-second Xvfb smoke pass (`timeout` exit 124).
- The fixes and packages are local only. No push, merge, tag or release was performed, and Claude
  remains disabled by user instruction.

## DroneCAN refresh and parameter-response fix

- Branch `fix/dronecan-refresh-parameters` starts from the clean released `master` checkpoint
  `294968844e7dc703f87f75724a4a5c96e52ef46b`.
- Refresh now clears both the visible nodes/selection and the protocol `NodeList`/`NodeInfo`
  discovery caches. Every fresh `uavcan.protocol.NodeStatus` upserts the visible row, so a node
  cannot remain permanently hidden merely because its ID was already present in the library cache.
- Parameter enumeration preserves the existing `GetParameters(byte)` API and adds response-aware
  diagnostics. A node that never answers the optional `uavcan.protocol.param.GetSet` service is
  reported as a timeout/unsupported service, while a valid terminal empty response is reported as
  a responding node with no configurable parameters. The temporary response handler is always
  detached, including exceptional send paths.
- Three regression scenarios cover cache reset plus rediscovery, a status-only node that does not
  implement the optional parameter service, and a valid empty parameter catalogue. Focused
  DroneCAN tests pass **13/13**; the complete suite passes **1441/1441**. The Release solution
  builds with **0 warnings / 0 errors**, and all six migration/inventory checks pass (1623 native
  rows with 0 blockers, 708/708 source paths, no WinForms, and clean project/binary/key audits).
- Claude remains disabled by user instruction.

## Post-release code-quality audit round 4

- Branch `audit/code-quality-round-4` starts from clean released `master`
  `0b1456049351420ee925d0176c76d01722febf4d` and remains deliberately unmerged in draft PR #19.
  The current reviewed implementation checkpoint is `bb90162de`; this status update follows it.
- `b3796ba50` removes a completion/start race shared by the Python and Lua hosts. An exclusive
  operation lease now owns exactly one cancellation source, so an old script cannot dispose the
  source of a newer script; disposal cancels the active run, rejects restart and prevents the local
  Lua REPL from surviving its page.
- `e7f799629` makes ESP8266 setup activation-scoped and target-stable. It waits for a complete
  parameter response with cancellation and a bounded timeout, reports missing fields instead of
  throwing from an unobserved constructor task, validates IPv4 input, captures one system ID for
  the whole write and requires every `setParam` to succeed before EEPROM persistence/reboot.
- `4ec1d5f3e` removes unsolicited compass-motor calibration ACK packets when an idle page closes,
  serializes Start/Finish transitions, stops a calibration that completed while its page was being
  disposed and always releases its MAVLink subscription independently of transport failures.
- `1d9b50305` makes closing any progress window request cancellation before disposing its source.
  The cached token remains readable during late cleanup, avoiding invisible connection/update work
  and the corresponding disposed-source race.
- `fad728f47` constrains every signed update-manifest path to the install/staging roots, rejects
  duplicate/rooted/traversal paths and invalid sizes, enforces exact signed download lengths and
  removes partial or hash-invalid files. `23acf5b74` gives the two legacy TCP output pages a
  cancellation-aware host owner: a new client closes the superseded client and no connection can
  be attached after page shutdown.
- `caa77f8a7` replaces SharpKml's direct deprecated code-page provider 5.0 with the supported
  10.0.11 package. The remaining deprecation report is intentionally limited to xUnit 2 required
  by `Avalonia.Headless.XUnit` and legacy transitive `System.*` packages below the compatible
  netstandard2.0/net472 plugin graph; none is reported vulnerable and a major test/plugin ABI
  migration is not mixed into this reliability branch.
- `5b760281c` derives three distinct Secure AP key paths even for uppercase or missing extensions,
  writes private key material atomically and restricts its Unix mode to `0600`. `bb90162de` rejects
  secure-command data/signature combinations above MAVLink's 220-byte wire limit instead of
  truncating the signature while declaring its original length; concurrent commands now receive
  atomic sequence numbers.
- `6dd060f5a` owns and awaits the CoT output/accept tasks, closes accepted TCP clients on stop,
  ignores late UDP/TCP/UI callbacks, supports immediate same-port restart and makes disposal
  idempotent. `c316b0234` rejects non-HTTPS/downgraded SITL downloads, bounds binary, metadata and
  compressed/expanded manifest input, preserves the previous cache file on failure, removes
  partial files and bounds KML/KMZ document/resource decompression. `e9df0be66` passes Linux/macOS
  external-open targets as one literal argument and releases every short-lived launcher process
  handle, including updater helpers.
- Fresh GTU `master` and `origin/master` at `bfb03b24f3180fdcba91ac7f114e7287b7b2b359`
  were compared with the previously synchronized `ab6422d4` checkpoint. Its only new production
  NV5Settings delta is mirrored here: RX/TX role presets are disabled when already selected, and
  ENABLE/SUPPRESS TX follows the live `tx_state` instead of offering a redundant command. Manual
  staged role edits refresh both controls immediately, and direct command invocation cannot bypass
  the same-state guard. The two firmware reference repositories named by GTU are not available in
  the local AgroSky workspace, so this synchronization is intentionally based on the clean fetched
  GTU repository itself.
- The active application graph was also checked for TLS validation bypasses, unsafe
  deserialization, archive/path traversal, unbounded HTTP ownership, SSH trust-all behavior,
  process/socket lifetime and primary native GDAL/SimpleBLE/libVLC ownership. No certificate
  validation bypass or active unsafe formatter was found; SFTP remains fail-closed TOFU/pinned and
  revalidates remote regular BIN files before download/delete.
- Local verification of the current branch: Release solution build
  **0 warnings / 0 errors**, **1438/1438** tests, all six migration/inventory checks pass (1623
  native rows with **0 blockers**, 708/708 pinned source paths, no WinForms, and clean project,
  binary and key audits), all 28 active projects report no known vulnerable direct or transitive
  NuGet package. Clean local Linux TAR/DEB packages
  `MissionPlanner-1.3.83-20260825.bb90162d-linux-x64.tar.gz` and
  `missionplanner_1.3.83-20260825.bb90162d_amd64.deb` build; the DEB passes `lintian`, payload
  assertions and a 12-second Xvfb event-loop smoke (`timeout` exit 124).
- Claude remains disabled. The next step is to push the updated draft PR #19 and require its
  Linux/Windows/macOS package matrix and CodeQL checks to pass. Do not merge it or create a release
  until the user reviews and explicitly requests that action.

## Current state

- The complete in-place Avalonia migration and audited cleanup were merged to `master` through
  PR #1 at merge commit `eb6cfe28f`. The later GTU `NV5Settings` synchronization was merged through
  PR #2, the independent diversity-radio key correction through PR #4, and the clean CI identity
  correction through PR #5 at master checkpoint `d273ca8aa`. PR #6 carries the final GTU
  `NV5Settings` refinement (`639a19acc`), focused CodeQL/AES follow-up (`c3265e3e8`) and explicit
  secure dependency declarations (`2e26a52a3`). PR #8 removes the obsolete MetadataExtractor build
  graph and resolves the remaining CodeQL findings, while PR #9 adds verified macOS DMG packaging;
  the resulting release checkpoint is `9ed911535`. Native baseline `67a3c4f` remains the immutable
  rollback reference in Git history.
- Upstream safety/reliability PR #13 is merged at `b24238dbc`; its complete Linux, Windows and
  macOS package matrix and CodeQL run passed, and release `v1.3.83-20260824.b24238db` contains all
  19 expected DEB/TAR/MSI/ZIP/DMG, signed update-manifest and checksum assets. Round 2 was merged
  through PR #14 at master checkpoint `0266a2878`; its master package run `32764653113` and CodeQL
  run `32764653058` passed with zero open alerts. Round 3 remains isolated on
  `fix/upstream-issues-round-3` until its own CI and review complete; no new tag or release may be
  created until every round-3 check has passed on `master`.
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
  decisions and reproduction commands are recorded in `WARNING_AUDIT.md`. All **1379/1379**
  Avalonia tests pass on Linux. A 12-second Xvfb launch reaches the normal Avalonia event loop with
  no console errors.
- Informational version is derived from the current native Mission Planner version and formatted as
  `1.3.83+YYYYMMDD.<commit>`; dirty developer builds append `.dirty`.
- CI packages explicitly pin that clean commit identity before compilation. Runner-local files
  created by restore/build therefore cannot add a misleading `.dirty` suffix to Linux or Windows
  package names or to the application metadata embedded in any of the four platform builds; CI
  rejects a package if that suffix reappears.
- Native-identity packaging is integrated for Linux `.tar.gz`/`.deb`, Windows portable ZIP/MSI and
  macOS x64/arm64 ZIP/DMG. All four RID publishes pass their native GitHub runners; the Linux
  packages pass `lintian` and extracted-DEB Xvfb smoke, Windows CI performs a real MSI
  install/validation/uninstall, and each macOS job verifies, mounts and inspects its DMG. Both macOS
  outputs contain architecture-correct pinned VLC/SimpleBLE runtimes. Details and signing
  boundaries are in `RELEASE.md`.
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
- PR #8 CI and CodeQL passed before merge. Master CodeQL run `32730238333` passed and the repository
  code-scanning API reports **zero open alerts**: the obsolete EXIF source is no longer compiled and
  the WinZip AES transform retains its required counter-mode output without an ECB construction.
- PR #9 CI run `32730683963` passed the complete Linux, Windows and two-architecture macOS matrix;
  CodeQL run `32730683985` passed. Cross-platform release dry-run `32730719818` independently
  produced Linux DEB/TAR, Windows ZIP/MSI and macOS x64/arm64 ZIP/DMG artifacts successfully.
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

## Upstream safety and issue audit

- Branch `fix/upstream-safety-reliability` adapts the applicable parts of upstream PRs #3728,
  #3740, #3710, #3715, #3724, #3679, #3705, #3222, #3603, #3752, #3750/#3722, #3250 and #3646.
  The changes preserve guided altitude frames, bound serial enumeration, harden MAVFTP/MJPEG/HTTP
  parsing and resource ownership, use current guided commands, lease camera/gimbal message rates,
  correct mission ACK behavior, snapshot proximity state safely, detect Septentrio ports, expose
  compass-calibration failures, expire stale pre-arm failures and report Windows-blocked plugins.
  Each adaptation is native to the Avalonia/CoreCLR architecture and has focused regression tests;
  obsolete WinForms-only implementation details were not copied.
- The 59 open bug-labelled upstream issues and the 100 most recently updated open issues were
  triaged against the live port, including linked commits and PRs. Two additional reports were
  confirmed in current code and fixed: #3461 now tracks actual DataFlash byte ranges, keeps progress
  monotonic, repairs bounded gaps, times out cleanly and always ends the MAVLink log session; #3694
  atomically preserves MAVLink signing keys, migrates every available legacy MAC-derived identity
  to persistent `authkeys.key` material and refuses to overwrite an unreadable `authkeys.xml`.
- Rejected transfers are recorded by reason rather than silently copied. Examples: #3736 targets
  the retired ZedGraph/WinForms viewer; #3658 and #3601 target Mono RESX/GStreamer paths absent from
  Avalonia; #3516 targets the retired WinForms internet firmware picker; #3472/#3391 target old
  MAVFTP rename/drag-drop UI not exposed by the port; #3734 is already stricter because the current
  server is loopback-only and has no guided/raw endpoints. #3746 must be corrected in ArduPilot's
  parameter metadata itself: its current `AC_AttitudeControl_Heli.cpp` still declares
  `HOVR_ROL_TRM` as `0 1000`, so overriding it only in Mission Planner would create conflicting
  safety metadata.
- The project audit exposed an incomplete earlier cleanup: only the placeholder key and empty
  assembly file had been removed from `ExtLibs/MetaDataExtractorCSharp240d`. The remaining 117-file,
  2004-era source project had no solution, source or runtime consumer; GeoRef/Survey use the pinned
  maintained `MetadataExtractor` package. The complete obsolete tree is now removed and the project
  and key audits record that decision.
- First-round checkpoint verification: Release solution build **0 warnings / 0 errors**,
  **1344/1344** tests,
  all six porting/inventory checks pass, the native manifest has **0 blockers**, and every active
  project reports no known vulnerable direct or transitive NuGet package.

## Upstream issue audit round 2

- Branch `fix/upstream-issues-round-2` contains eleven atomic changes on top of clean master
  `b24238dbc`. Confirmed open reports are fixed in the native Avalonia paths: #3761 decodes the
  complete numbered `MAVn_DEVID` range; #3600 keeps `DO_LAND_START` as a marker but removes it from
  flown routes, distances and prefetch corridors; #3492 deduplicates MAVFTP `@SYS`; #3419 preserves
  DroneCAN string parameters safely; and #3447 removes the obsolete off-spec
  `VIDEO_STREAM_INFORMATION99` interval choice.
- Upstream PR #3455 was not copied literally: its display-unit-dependent storage would still make
  travelled distance and `mAh/km` change when users switch units. The adapted implementation keeps
  metres internally and converts only at the property boundary. Likewise, PR #3723's WinForms
  float-format workaround is unnecessary because Avalonia composes bitmasks as `long`/`double`; a
  regression test proves bit 24 remains exact. From the stale mixed PR #2648, only commit
  `bb97e9004d66ec83d2b0894fca9f041e7f71bcc5` was semantically adapted, with first-delimiter
  parsing, exact string preservation and rejection of invalid numeric writes.
- #3305 now hides and never writes the Plane-style global loiter radius for Copter; a zero-radius
  Copter `LOITER_TURNS` remains a panorama and draws no false circle. #3589 renders the enabled
  legacy Copter horizontal fence (`FENCE_ENABLE`, `FENCE_TYPE` bit 1 and `FENCE_RADIUS`) around a
  valid home. #954 adds the exact `LOITER_TURNS` orbit length to mission distance while deliberately
  avoiding an unsupported estimate for `LOITER_TO_ALT`. #3717 removes obsolete Rover
  `WP_OVERSHOOT`/`NAVL1_*` fields and exposes current deceleration, steering-angle and horizontal
  position/velocity controller parameters with metadata tooltips.
- Linked changes were rejected when their assumptions no longer held. Closed PR #3706 subscribed
  to compass failure STATUSTEXT that ArduPilot ultimately never emitted; this port already has the
  final `MAG_CAL_STATUS` values 8-10, human-readable diagnostics and tests. #3595, #3335, #3394,
  #3675, #2641, #2965, #2301 and #3241 are already fixed by the current lifecycle/read-only/raw
  forwarding/bounded-value paths. #3658, #3736, #3601, #3516, #3472 and #3391 target retired
  Mono/WinForms/ZedGraph/GStreamer surfaces. #3746 belongs in authoritative ArduPilot metadata;
  #3747 is vehicle telemetry scheduling; and reports without a reproducible protocol-safe change
  such as #3408 were not patched speculatively.
- Local verification for this round: Release solution build **0 warnings / 0 errors**,
  **1379/1379** tests, all six migration/inventory checks pass with **0 blockers**, and all 28 active
  projects report no known vulnerable direct or transitive NuGet packages. Cross-platform package
  and CodeQL gates remain required on the draft PR before merge.

## Upstream issue audit round 3

- Eleven atomic changes on `fix/upstream-issues-round-3` address confirmed reports and narrowly
  applicable upstream PRs. #3535 now sends the irreversible `PARACHUTE_RELEASE` action instead of
  disabling the parachute. #2923 hides Plane-only waypoint and loiter radii for both Copter and
  Rover and prevents those pages from writing the hidden parameters. #3358 prevents a closed
  mission-command selector from changing commands through navigation keys or the mouse wheel.
- Parameter handling now preserves protocol and file precision. #3284/#2842 comparisons use
  `MAV_PARAM_TYPE`: integer values compare exactly, while REAL32 values tolerate only C
  `FLT_EPSILON`; explicit compare dialogs also expose parameters missing from either side without
  allowing a missing row to be staged. #2884 honours explicit bytewise parameter encoding ahead
  of firmware-family heuristics, so values such as `UINT32` 60180513 survive both PARAM_SET and
  parameter-list decoding exactly. The applicable validation from upstream PR #2644 rejects
  DroneCAN numeric values outside the node-reported range while leaving string parameters intact.
- #3366 retains the latest non-empty SBS transponder squawk for a bounded 30-second interval.
  #2758 changes DataFlash parameter browsing from a final-value dictionary to the complete
  chronological `PARM` history while retaining final-value `.param` export. The functional part of
  upstream PR #3735 adds Pixhawk 6C Windows USB MAVLink and SLCAN interfaces to both driver
  architectures, with packaging authoring coverage.
- #2363 needs no port change: survey speed is already a `double` edited in 0.1 m/s increments and
  emitted unchanged as `DO_CHANGE_SPEED`. The pre/post concurrent batch allocation described by
  #2986 is also already represented by separate ISBH instance slots `0/1/3/4`, with an additional
  bounds guard absent from the legacy viewer; the issue's two old Dropbox links now return HTML
  rather than the logs, so no unverified spectral rewrite was made.
- A release-time GTU comparison found newer clean checkpoint `f196ea689`. Its NV5 signal semantics
  are now mirrored: an unlocked receiver displays current channel RSSI and suppresses stale packet
  RSSI/SNR; a locked receiver prefers packet RSSI and safely falls back to channel RSSI. SX127x and
  LR11xx cases plus locked fallback are covered by regression tests.
- Round-3 local verification: Release solution build **0 warnings / 0 errors**, **1400/1400**
  tests, all six migration/inventory checks pass (708/708 pinned source paths, 1623 native-manifest
  rows and **0 blockers**), and all 28 active projects report no known vulnerable direct or
  transitive NuGet package. Clean `linux-x64` TAR/DEB packages build, `lintian` emits no diagnostic,
  payload assertions pass, and the extracted DEB reaches the normal Avalonia event loop during a
  12-second Xvfb smoke test. Native Windows and macOS packaging plus CodeQL remain mandatory CI
  gates before merge and release.

## GTU synchronization checkpoint

- NV modem behavior was last compared with `/home/alex/src/AgroSky/GTU` at clean local and fetched
  `origin/master` `f196ea689ba0f30eb3b16a76bbf8041ee21a23de`. GTU refinement
  `77af510a47f8cbe7ea02fcc047019b07fb2c0c26` keeps selected-radio key targeting independent of
  `DIVERSITY`, and **Revert selected** restores one staged parameter locally without sending
  MAVLink. Both behaviors and their regression tests are ported. Checkpoint `f196ea689` additionally
  supplies current unlocked-channel RSSI semantics, now ported with matching tests. `REFRESH_SETTING`
  remains a typed `UINT32`; NV5 key words remain signed `INT32` values preserving the same raw bytes.
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
- The formerly open CodeQL findings were resolved without repository-wide suppression. The old
  unreferenced MetadataExtractor source graph containing the reported export flows was removed in
  favor of the already-used maintained NuGet dependency. SharpZipLib's WinZip AES transform now
  implements its state-cancellation primitive without constructing ECB while preserving fixed
  WinZip AES-128/AES-256 vectors and round trips. Master CodeQL run `32730238333` confirms zero open
  alerts.
- The two remaining Secret Scanning warnings were inherited Mapbox values in removed official
  Mission Planner/Xamarin/Cesium history. At the user's explicit request they are resolved as
  `wont_fix`, not falsely marked revoked; the audit comments preserve the origin and the decision
  not to rewrite published upstream-derived history.
- Every inbound UDP listener now enables address sharing/reuse before bind. This lets one modem
  broadcast reach Mission Planner and GTU/Hermes listeners bound to the same local port; a
  regression test opens two real sockets and proves that both receive the same datagram. Ordinary
  unicast fan-out remains explicitly unsupported without separate ports or a MAVLink router.
- `Scripts/`, localization RESX, NoFly data, the X-Plane/HIL bridge and independently meaningful
  remaining library/generator projects are deliberately retained; non-inclusion in the active
  solution alone is not deletion evidence. The former `ExtLibs/mono` submodule was removed only
  after every reference was shown to come from the retired WinForms project graph.

## Immediate next step

Push `fix/upstream-issues-round-3` and run its complete CI/package and CodeQL gates in a PR. Merge
only after Linux, Windows, both macOS architectures and CodeQL pass; then repeat those gates on the
actual master merge commit. Only after the master checks and zero-alert API audit pass may the
master commit be tagged and its full 19-asset release verified. The remaining hardware acceptance
work still requires
representative physical NV4/NV5 hardware: repeat UDP/TCP/UART switching, disconnect and
key-programming checks, and recheck GTU `NV5Settings` changes newer than clean checkpoint
`f196ea689` before declaring hardware acceptance complete.

## Acceptance baseline

- At least 1400 port tests retained and passing.
- Clean Release build has zero errors and zero warnings.
- `linux-x64`, `win-x64`, `osx-x64`, and `osx-arm64` publish gates pass.
- Linux `.deb`/portable archive, Windows ZIP/MSI and both macOS ZIP/DMG pairs build and pass their
  native package validation.
- CodeQL has no untriaged alerts.
- No live source/build/runtime reference to `external/MissionPlanner` remains.
- Parameter/session safety, NV modem behavior, speech serialization, airport alpha, movable Flight
  Data splitter, plugin lifecycle and localization coverage do not regress.
