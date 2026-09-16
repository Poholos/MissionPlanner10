# AI flight diagnostics over MCP

Mission Planner embeds a Streamable HTTP MCP server in the existing Avalonia application.
Open **AI** in the navigation bar, or **AI diagnostics / MCP…** in the tools menu.
The server is off until requested. No separate server installation is needed.

## Use

1. Connect the aircraft normally, or use **Attach flight log…** to select a DataFlash
   `.bin`/`.log`. Offline log analysis works without an aircraft. The configured Mission
   Planner log directory is also discoverable by agents.
2. Click **Start server**. Each session gets a new random loopback port and bearer token.
3. With a locally installed, authenticated Codex CLI, edit the task and click **Run Codex**.
   Select its executable if it is not on PATH; on Windows use the native `codex.exe`.
   The launcher was checked against CLI 0.154.0. It uses per-process MCP configuration,
   passes the token in `MP_MCP_TOKEN`, and does not edit the user's agent configuration.
   Its working directory is Mission Planner's cache, with the CLI's read-only sandbox.
4. Alternatively, **Copy connection settings** supplies a Codex TOML configuration with
   URL and Authorization header. Other Streamable HTTP clients can use those same values.
   Configure a 720-second tool timeout for onboard downloads. These settings contain a
   session credential; **Stop / revoke access** invalidates it and stops the launched agent.
5. Read the agent's output and inspect **Parameter proposals**. Select a proposal to see
   evidence, expected/current values and proposed values. Export a `.param` file or use
   **Review / apply selected proposal**. An explicit UI action is required to apply.

An example task:

> Analyze the connected vehicle and the attached flight log. Identify firmware, frame,
> payload assumptions and flight-time parameters. Inspect flight segments for vibration,
> clipping, gyro resonance, rate tracking, PID terms, actuator saturation and EKF health.
> Report missing evidence. Propose only changes supported by this flight, with a staged
> validation flight and criteria for keeping or reverting each change.

The endpoint is local to this computer (`http://127.0.0.1:<port>/mcp`). Remote agents
or agents in separate network containers need a separate deployment design. The built-in
launcher supports Codex; any compatible local MCP client can connect independently.
Claude is not launched. Provider authentication and model billing belong to the agent.
Closing the diagnostics window stops its agent and revokes the server session.

## Available tools

| Tools | Data / behavior |
| --- | --- |
| `diagnostics_info`, `list_vehicles` | Workflow, capabilities, firmware, system/component and connection-bound target IDs |
| `telemetry_schema`, `read_telemetry` | Discover and read scalar CurrentState fields, display units and packet freshness |
| `vehicle_health` | Raw HEARTBEAT, SYS_STATUS, GPS, VIBRATION and EKF with separate receipt ages; fixed physical units, missing/unknown values preserved |
| `read_parameters`, `refresh_parameters` | Paginated typed parameters, completeness and metadata; refresh requires disarmed aircraft |
| `read_mission_draft` | Mission Planner's UI mission, explicitly distinguished from onboard mission |
| `list_onboard_logs`, `download_onboard_log` | MAVLink log directory and cancellable disarmed log download |
| `list_local_logs`, `log_schema`, `read_log_records` | Opaque log handles, all decoded message fields/units, time/instance filters and lossless pagination |
| `log_overview` | Full-log message counts, boot-time bounds, instances and available event types |
| `log_parameters_at` | Paginated last-known PARM values at a boot time, source lines and observed changes; never uses future or live values |
| `log_vibration_report` | Per-IMU VIBE means/maxima, samples above 30/60 m/s², observed clipping increments and counter resets |
| `log_field_statistics` | Streaming mean, RMS, deviation, extrema, first/last and times for each message/instance/field |
| `log_spectrum` | One-sided Welch PSD of a regularly sampled scalar field |
| `log_batch_spectrum` | Raw ISBH/ISBD IMU batch PSD with actual sample rate, scaling and sequence validation |
| `log_response` | Target/actual RMS error and cross-correlation lag in the same message/instance |
| `propose_parameter_changes`, `parameter_proposals` | Evidence-backed changes and operator review/application status |

PIDR/PIDP/PIDY, RATE, VIBE, IMU, ESC, RCOU, XKF/NKF, PARM, MSG and other available
messages are discoverable through the log schema. The interface is not restricted to a
fixed list of tuning parameters. Sensor instances remain separate, and `PID*.I` is the
integral term, not an instance number.

The server exposes **22 tools** directly backed by Mission Planner's MAVLink connections,
parameter metadata, mission draft and native DataFlash parser. For an offline investigation:

1. Obtain a handle with `list_local_logs`; call `log_overview` and `log_schema`.
2. Read available MODE/ARM/EV/ERR records to choose a flight segment in seconds since boot.
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
authenticated POST requests can receive SSE responses, notifications receive HTTP 202,
and standalone GET streams return HTTP 405. No transport session ID or resumable event
history is advertised. This is distinct from the obsolete separate `/sse` + `/messages`
transport; clients should use the displayed `/mcp` URL. Wire behavior is tested against
the [MCP transport specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
and with the official C# MCP client, including unsupported-version rejection.

## Interpretation and write behavior

- Establish frame, firmware, payload and flight conditions first. Current parameters may
  differ from flight-time PARM records. Mechanical vibration, clipping, estimator problems
  or actuator limits can invalidate a PID-only diagnosis.
- Log times are seconds since boot. CurrentState values use configured display units and
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

## Limits and validation

This is a complete local analysis/proposal/review workflow, not an autonomous flight-tuning
certification. Representative aircraft, simulator and flight acceptance remain necessary.
No model invocation or physical-vehicle write was performed during automated validation.
Tests use the official MCP client against real loopback Kestrel, synthetic DataFlash logs,
known signals, injected MAVLink exchanges and Avalonia layout at the minimum window size.
See [STATUS.md](STATUS.md) for exact results and packaging limitations.

The parser is the existing managed DFLogBuffer. Initial indexing is synchronous on a
worker thread and cannot be interrupted mid-constructor; very large logs can delay shutdown.
One indexed reader is cached per server. Flight telemetry `.tlog` analysis is not included.
Discovery is bounded to 500 files/five directory levels; explicit attachments can select
other logs, up to 1,000 handles per session. HTTP requests have size/concurrency limits.
Existing transport busy guards and parameter/log gates prevent overlapping supported
operations, but this is not a repository-wide transaction system for every legacy plugin.

The C# MCP SDK is pinned to 2.2.0. ASP.NET Core is included in self-contained distributions;
a framework-dependent developer run also needs the ASP.NET Core 10 runtime.
