# AI flight diagnostics over MCP

Mission Planner embeds a Streamable HTTP MCP server in the existing Avalonia application.
Open **AI** in the navigation bar, or **AI diagnostics / MCP…** in the tools menu.
The server is off until requested. No separate server installation is needed.

## Use

1. Connect the aircraft normally, or use **Attach flight log…** to select a DataFlash
   `.bin`/`.log` or telemetry `.tlog`. Offline log analysis works without an aircraft. The configured Mission
   Planner log directory is also discoverable by agents.
2. On **Agent**, choose a detected **Codex CLI** or **Claude Code**. Discovery runs when
   the AI window opens; **Find agents** refreshes it. **Executable…** supports nonstandard
   installations; select the matching CLI type. Windows requires a native `.exe`.
3. Enter a task and click **Run agent**. Mission Planner starts a random loopback endpoint
   with a fresh bearer token automatically. Existing agent login is required. Codex receives
   process-only `-c` settings, uses `--ignore-user-config` and a read-only sandbox; authentication
   remains in its normal home. Claude Code receives `--mcp-config`/`--strict-mcp-config` pointing
   at a private temporary JSON, with built-in tools, hooks and other settings sources disabled;
   only Mission Planner MCP tools are preallowed. There is no permission-bypass flag.
   Neither launcher edits global agent configuration. Each process gets a unique temporary
   working directory; normal completion, cancellation and failed startup remove it.
   The CLI endpoint closes when the process finishes; attached logs/proposals remain in the UI.
4. Alternatively, **Start server → Copy connection settings** supplies a session URL and
   Authorization header for another Streamable HTTP client. Configure a 720-second tool timeout
   for onboard downloads. **Stop / revoke access** stops the CLI and both HTTP listeners.
5. Read the agent's output and inspect **Parameter proposals**. Select a proposal to see
   evidence, expected/current values and proposed values. Export a `.param` file or use
   **Review / apply selected proposal**. An explicit UI action is required to apply.

An example task:

> Analyze the connected vehicle and the attached flight log. Identify firmware, frame,
> payload assumptions and flight-time parameters. Inspect flight segments for vibration,
> clipping, gyro resonance, rate tracking, PID terms, actuator saturation and EKF health.
> Report missing evidence. Propose only changes supported by this flight, with a staged
> validation flight and criteria for keeping or reverting each change.

All endpoints are local to this computer (`http://127.0.0.1:<port>/mcp`). Remote agents
or agents in separate network containers need a separate deployment design. Provider login
and model billing belong to the selected agent. Closing the diagnostics window stops CLI
processes and both MCP listeners. It does not close an independently running desktop client.
The Codex command contract was checked with CLI 0.154.0; the Claude Code contract follows its
[CLI reference](https://code.claude.com/docs/en/cli-reference). Validation uses fake agent
processes, not a paid model invocation. Claude Code support does not enable Claude Desktop.

## OpenAI desktop connection

The **Desktop** tab appears only when an installed Codex/ChatGPT desktop application is found.
CLI executables and Claude URL handlers do not count as desktop installations. Linux discovery
uses XDG application entries and checks the launcher executable, macOS checks application bundles,
and Windows queries Start applications, including Store/MSIX installations. A missing or unsupported
installation can still use the copied HTTP connection settings; desktop discovery never installs software.

1. Select the desktop app and local port (MP10 default **47183**, configurable).
2. Click **Register MCP**. This adds only the managed `missionplanner10_desktop` section to
   `$CODEX_HOME/config.toml` or `~/.codex/config.toml`. It does not open a network listener.
   The [OpenAI MCP configuration](https://developers.openai.com/codex/mcp/) is shared with CLI;
   this registration is therefore also visible there. Restart the desktop app after registration.
3. Click **Launch desktop agent**. Mission Planner binds `http://127.0.0.1:47183/mcp` without a
   token, then asks the OS to launch/activate the selected app. The tab reports requests when
   they arrive; launching an app alone does not prove an MCP connection. Start a conversation in
   the desktop app and select/use the registered Mission Planner tools.
4. **Stop desktop access** closes only that listener, keeping CLI access and registration.
   **Remove registration** also removes the managed configuration section. The desktop app stays open.

This explicitly enabled desktop listener accepts any local process, not only the registered app.
It still enforces Host/Origin checks, request limits, exact aircraft targeting and operator-reviewed
parameter application. It never binds a LAN interface or opens a firewall rule. An occupied port
fails explicitly; there is no silent fallback that would invalidate the registered address. Only
one MP10 instance can own a given desktop port. The CLI listener keeps separate token authentication.

Registration uses a TOML parser, preserves other configuration text/comments and makes a unique
backup before atomic replacement. Repeated registration is idempotent. Invalid TOML, conflicting
unmanaged entries, manually changed managed blocks and symbolic-link config files are left untouched
with an error. It detects concurrent edits before replacement; the sidecar lock coordinates MP10
instances, not unrelated editors. Unsupported TOML table arrangements should be configured manually
through the client settings. Registration and startup are explicit UI actions, never startup side effects.

Claude Desktop local bridging is deliberately deferred. No cloud connector or public tunnel is created.

## Available tools

| Tools | Data / behavior |
| --- | --- |
| `diagnostics_info`, `list_vehicles` | Workflow, capabilities, firmware, system/component and connection-bound target IDs |
| `telemetry_schema`, `read_telemetry` | Discover and read scalar CurrentState fields, display units and packet freshness |
| `vehicle_health` | Raw HEARTBEAT, SYS_STATUS, GPS, VIBRATION and EKF with separate receipt ages; fixed physical units, missing/unknown values preserved |
| `read_vehicle_messages` | Last 256 exact-component STATUSTEXT packets, severity, original chunk IDs, sequence cursor and dropped-history indicators |
| `telemetry_packet_inventory` | Received packet types and individual receipt ages; no payloads, stream-rate changes or inferred packet-loss numbers |
| `read_parameters`, `refresh_parameters` | Paginated typed parameters, completeness and metadata; refresh requires disarmed aircraft |
| `read_mission_draft` | Mission Planner's UI mission, explicitly distinguished from onboard mission |
| `list_onboard_logs`, `download_onboard_log` | MAVLink log directory and cancellable disarmed log download |
| `open_log_analyzer` | Open a catalogued BIN/LOG/TLOG in the graphical Log Browser, with field graphs and record tables |
| `list_local_logs`, `log_schema`, `read_log_records` | Opaque log handles, all decoded message fields/units, time/instance filters and lossless pagination |
| `log_overview` | Full-log message counts, time bounds and sources; DataFlash boot time or TLOG elapsed receipt time |
| `log_events` | DataFlash flight-event records and TLOG state changes, text/chunks and command acknowledgments, with original evidence and pagination |
| `log_time_series` | Bounded per-source trends: all-sample means, extrema with times, invalid counts and observed gaps; no interpolation |
| `compare_log_parameters`, `compare_vehicle_parameters_to_log` | Recorded-to-recorded and recorded-to-current parameter differences, explicit source/time, missing/unknown/type-change distinctions |
| `log_parameters_at` | Paginated last-known PARM/PARAM_VALUE values at a log time, with source evidence; never uses future or live values |
| `log_vibration_report` | DataFlash VIBE per IMU or TLOG VIBRATION per source: means/maxima, threshold sample counts, clipping increments and resets |
| `log_field_statistics` | Streaming mean, RMS, deviation, extrema, first/last and times for each message/instance/field |
| `log_spectrum` | One-sided Welch PSD of a regularly sampled scalar field |
| `log_batch_spectrum` | Raw ISBH/ISBD IMU batch PSD with actual sample rate, scaling and sequence validation |
| `log_response` | Target/actual RMS error and cross-correlation lag in the same message/instance |
| `propose_parameter_changes`, `parameter_proposals` | Evidence-backed changes and operator review/application status |

PIDR/PIDP/PIDY, RATE, VIBE, IMU, ESC, RCOU, XKF/NKF, PARM, MSG and other available
messages are discoverable through the log schema. The interface is not restricted to a
fixed list of tuning parameters. Sensor instances remain separate, and `PID*.I` is the
integral term, not an instance number.

The server exposes **29 tools** directly backed by Mission Planner's MAVLink connections,
parameter metadata, mission draft, native DataFlash parser and MAVLink telemetry reader.
For a DataFlash investigation (TLOG differences are described below):

1. Obtain a handle with `list_local_logs`; call `log_overview` and `log_schema`.
2. Call `log_events` to find mode/arm/error events and choose a flight segment in seconds since boot.
3. Call `log_parameters_at(logId, atSeconds)` at the segment start. Use PARM records to
   inspect changes during the segment; absent parameters remain unknown.
4. Call `log_vibration_report(logId, startSeconds, endSeconds)`. Modern `VIBE[IMU].Clip`
   and legacy `Clip0/1/2` are both supported. Counts above thresholds are sample counts,
   not durations. Clipping increments exclude the first observed value and reset/wrap
   intervals; they are lower bounds, not lifetime totals. Missing fields are omitted.
5. Use `log_batch_spectrum` or `log_spectrum` for raw IMU resonance and `log_response`
   for rate tracking. Use `log_field_statistics` and records for battery, ESC, actuator
   and estimator evidence. On a connected aircraft, compare `vehicle_health` and
   `read_parameters` before submitting a proposal.

The vibration thresholds follow [ArduPilot's measurement guidance](https://ardupilot.org/copter/docs/common-measuring-vibration.html).
They are context for investigation, not an automatic flight-safety classification.
Timestamp reversals within parameter history or an IMU stream are rejected by these
summaries; separate logs from different boots before quantitative analysis.

HTTP uses the official SDK's stateless **Streamable HTTP** transport at `/mcp`:
POST requests (token-authenticated for CLI, explicitly unauthenticated for desktop) can receive SSE responses, notifications receive HTTP 202,
and standalone GET streams return HTTP 405. No transport session ID or resumable event
history is advertised. This is distinct from the obsolete separate `/sse` + `/messages`
transport; clients should use the displayed `/mcp` URL. Wire behavior is tested against
the [MCP transport specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
and with the official C# MCP client, including unsupported-version rejection.

## Download, open and analyze a flight log

The **AI → Flight logs** tab provides the complete operator workflow:

1. **Refresh vehicles**, select the exact MAVLink system/component, then **List onboard logs**.
2. Select the flight and **Download selected BIN**. The aircraft must remain disarmed;
   **Stop / revoke access** cancels a transfer. Completed downloads are saved in the configured
   log directory's `agent-downloads` folder and automatically registered with MCP.
3. Select the downloaded file and **Open selected in analyzer**. In Log Browser, select
   message fields to graph, show the record table, inspect messages/parameters or export data.
4. For existing files use **Discover local logs** or **Attach files…**. These accept
   `.bin`, `.log` and `.tlog`; attached files appear in the same list.

An agent can perform the same sequence with `list_onboard_logs`, `download_onboard_log`,
`log_overview`, `log_schema`, `read_log_records` and `open_log_analyzer`. The last tool
opens the actual Mission Planner viewer, not a separate server-side application.

[DataFlash logs are recorded onboard](https://ardupilot.org/planner/docs/common-downloading-and-analyzing-data-logs-in-mission-planner.html),
whereas [TLOGs are normally recorded by the ground station](https://ardupilot.org/planner/docs/mission-planner-telemetry-logs.html).
The onboard MAVLink log protocol downloads BIN; it does not turn those files into TLOG.
Use a completed local TLOG (disconnect/finish recording first if the active writer locks it).

For TLOG, `log_overview`/`log_schema` enumerate MAVLink message/source keys such as
`ATTITUDE[1:1]` and their wire units/descriptions. `read_log_records` accepts these keys
or a message name with `instance="1:1"`. Pagination counts decoded packets. Records include
UTC receipt timestamps; time-window arguments mean seconds from the first receipt, **not**
seconds since vehicle boot. `log_field_statistics` keeps each system/component separate.
`log_parameters_at` decodes PARAM_VALUE using preceding source-specific heartbeat/capability
evidence and returns null for unknown integer encoding. `log_vibration_report` processes
VIBRATION envelopes and clipping counters per source. Binary payload fields remain arrays.

The graphical TLOG viewer also separates types by system/component and supports scalar
graphs, text/array record tables, STATUSTEXT, source-selected parameter history and a map
track (first GLOBAL_POSITION_INT source, named in the summary). Values use MAVLink wire
units; graph scale/offset controls can convert them. DataFlash expression presets and raw
IMU FFT/response tools continue to require BIN/LOG: packet receipt intervals do not establish
the sensor's true sampling rate. The TLOG reader checks CRC and supports signed MAVLink 2,
but does not authenticate signatures offline. Corrupt/truncated/unsupported-dialect records
produce explicit errors rather than silently dropping evidence.
Receipt-clock reversals are also rejected so a negative elapsed time cannot silently
exclude packets; split such recordings before time-window analysis. Generic field
statistics retain protocol sentinel values (for example an unknown battery value);
consult the field descriptions returned by `log_schema` before interpreting them.

## Interpretation and write behavior

- Establish frame, firmware, payload and flight conditions first. Current parameters may
  differ from flight-time PARM records. Mechanical vibration, clipping, estimator problems
  or actuator limits can invalidate a PID-only diagnosis.
- DataFlash log times are seconds since boot; TLOG uses elapsed receipt time. CurrentState values use configured display units and
  are a best-effort snapshot; last-packet time does not establish each field's freshness.
- Record values are decoded DataFlash values before optional FMTU display multipliers.
  Schema reports these multipliers; numerical tools apply them once. Unknown multipliers
  remain explicitly unknown. Inspect units before comparing fields.
- PSD removes each window's mean, uses a Hann window with 50% overlap and returns field
  units squared/Hz. Scalar FFT refuses gaps, nonmonotonic timestamps, mixed instances or
  more than 32,768 samples. Use a shorter interval instead of decimating. Raw batch FFT
  reads one complete batch; missing/out-of-order data is rejected. A low-rate VIBE envelope
  is not raw gyro data suitable for identifying a notch frequency.
- Correlation lag is an observation, not causal radio/control latency, a settling-time
  measurement or an automatic PID design. Weak/constant signals and a search-boundary
  peak produce no lag estimate. Inspect angular wrap, modes, excitation and saturation.
- Proposals validate expected values, finite numbers, MAVLink type limits and available
  parameter ranges. They do not write through MCP. The UI verifies connection generation,
  fresh telemetry and a fresh disarmed heartbeat (read directly from the packet cache),
  plus writability. Each parameter is read again under the
  shared write gate, compared to its reviewed value, written and checked against its typed
  acknowledgement. State and cancellation are rechecked before sending/retrying a write.
- A JSON review, nonsecret `-before.param` snapshot and incremental `-result.txt` audit are
  saved under the application's state directory in `agent-parameter-audit`. Partial failure
  stops the batch. A lost acknowledgement may mean the value was applied; read it again.
  There is no automatic rollback, reboot or claim that a batch is atomic.
- Aircraft control/arming, arbitrary filesystem access, code execution and credential APIs
  are not exposed. Live parameters with KEY/PASS/SECRET/TOKEN in their names are omitted.
  Attached flight logs are shared as data, including their messages and PARM history;
  do not attach logs containing information you do not want the chosen agent to receive.

## Additional diagnostic context

These tools address six gaps in the original 23-tool surface: text warnings, telemetry availability,
flight-event selection, compact trends, comparisons between flights and configuration drift since a flight.
They are read-only and available through both CLI and desktop connections without new UI controls.

- **Why does arming fail?** Call `read_vehicle_messages(targetId)` and `vehicle_health`.
  Message severity follows [MAVLink STATUSTEXT](https://mavlink.io/en/messages/common.html#STATUSTEXT):
  zero is most severe, seven includes debug. Continue with `afterSequence=nextSequence`.
  `historyTruncated` means older packets have been evicted; `missedSinceCursor` means a polling
  client has fallen behind. The native cache retains 256 packets per component independently of
  the UI message queue. Receipts may predate MCP startup and include MAVLink 2 fragments; IDs and
  chunk sequence numbers are retained instead of claiming a complete reconstructed message.
- **Is a telemetry value current?** `telemetry_packet_inventory` lists actual received packet
  types and receipt ages. Its five-second freshness marker describes receipt only. An absent
  packet is unknown; the inventory neither measures stream rates nor requests them.
- **What happened during this flight?** `log_events` returns DataFlash MSG/MODE/EV/ERR/ARM/FAIL
  evidence, or TLOG first-observed/changed HEARTBEAT and landed states, STATUSTEXT and COMMAND_ACK.
  Firmware-specific DataFlash codes remain raw. A first observed state is not evidence that a
  transition occurred then. Pagination reconstructs earlier TLOG state before filtering, so
  unchanged heartbeats do not turn into artificial events on the next page. Untimed DataFlash
  messages retain null time and are included only for windows starting at zero. Text is data,
  never agent instructions. See the [ArduPilot log-analysis workflow](https://ardupilot.org/copter/docs/common-downloading-and-analyzing-data-logs-in-mission-planner.html).
- **Where are the short peaks or gaps?** `log_time_series(logId, type, field, startSeconds,
  endSeconds, bins)` scans all samples into at most 512 equal-duration bins per source (up to
  16 sources; use `instance` for more). It returns counts, mean, first/last and min/max with
  occurrence times. Empty bins are omitted, invalid values are counted and maximum observed gap
  is null when fewer than two timestamped records exist. Means are sample-weighted. DataFlash
  uses the native parser's scaling once; TLOG keeps MAVLink wire units and sentinel values.
  These summaries are for trends, never FFT or response-lag calculations.
- **What changed between flights or since this flight?** `compare_log_parameters` accepts two
  log handles and two times, including two times in one log. `compare_vehicle_parameters_to_log`
  compares a historical snapshot against one target's current cache and reports cache completeness
  and capture time. Neither tool infers that a log belongs to the target, stages proposals or
  writes parameters. TLOG comparisons require explicit `systemId:componentId` sources from
  `log_schema`; DataFlash source arguments must be omitted. Each log uses its own time origin.
  Values are last recorded at or before each requested time, with line/time evidence. Exact
  comparisons distinguish changed, unchanged, typeChanged, onlyBefore, onlyAfter and unknown;
  missing means unrecorded, not deleted. Unknown TLOG encodings stay null. Secret parameter names
  are omitted. Results are paginated to 200 entries; snapshots are bounded to 16,384 names.

Parameter reads, log download/analysis, vibration/PSD/response analysis and operator-reviewed
parameter proposals were already present. Automated flight commands, calibration, mission upload,
firmware flashing and arbitrary file/code access are outside this diagnostic extension.

## Limits and validation

This is a complete local analysis/proposal/review workflow, not an autonomous flight-tuning
certification. Representative aircraft, simulator and flight acceptance remain necessary.
No model invocation or physical-vehicle write was performed during automated validation.
Tests use the official MCP client against real loopback Kestrel, synthetic DataFlash logs,
known signals, injected MAVLink exchanges and Avalonia layout at the minimum window size.
See [STATUS.md](STATUS.md) for exact results and packaging limitations.

The DataFlash parser is the existing managed DFLogBuffer. Initial indexing is synchronous on a
worker thread and cannot be interrupted mid-constructor; very large logs can delay shutdown.
One indexed DataFlash reader is cached per server; TLOG analysis scans packets with cancellation
and bounded result pages rather than loading the whole recording into memory. TLOG graph/map
materialization is capped at two million points and schema at 4,096 message/source combinations.
Discovery is bounded to 500 files/five directory levels; explicit attachments can select
other logs, up to 1,000 handles per session. HTTP requests have size/concurrency limits.
Existing transport busy guards and parameter/log gates prevent overlapping supported
operations, but this is not a repository-wide transaction system for every legacy plugin.

The C# MCP SDK is pinned to 2.2.0. ASP.NET Core is included in self-contained distributions;
a framework-dependent developer run also needs the ASP.NET Core 10 runtime.
