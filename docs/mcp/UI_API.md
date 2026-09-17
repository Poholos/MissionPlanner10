# UI and mission draft API

Use `tools/list` for exact parameter names and JSON schemas. All UI tools require
session Allow. The three passive draft tools (`mission_draft_get`,
`mission_command_schema`, `mission_draft_validate`) also work on self-connected
read-only sessions. Unknown future tools default to requiring Allow.

## Available operations

| Tool | Purpose and limits |
| --- | --- |
| `ui_get_state` | Active screen, surface IDs, log view IDs/revisions, tuning fields, draft revision. |
| `ui_navigate` | Native DATA, PLAN or HELP navigation; HELP must be available in display profile. |
| `ui_capture` | Visible map or plot only; PNG MCP image, width 64..1600, height 64..1200, <=2 MiB. |
| `ui_set_map_view` | Surface ID, latitude -85..85, longitude -180..180, integer zoom 3..20; disables auto-pan. |
| `ui_set_tuning_fields` | 1..12 distinct CurrentState field names and enabled flag; current UI aircraft, 30 s graph. |
| `ui_open_log` | Catalogue log ID; reuses matching view, maximum eight MCP-created windows. Returns view ID/revision. |
| `ui_plot_log` | View ID and expected revision; replaces curves with 1..8 original numeric fields, explicit start/end seconds and sensor instance. Maximum 32768 points per field; rejects ambiguous sources, empty curves and changes made while parsing. |
| `ui_close_log` | Close one MCP-created log window; never deletes its file. |
| `ui_inspect` | Latest connection-local snapshot of registered diagnostic widgets in MCP log windows. |
| `ui_invoke` | Invoke inspected Clear graph action after checking snapshot, control, window, bounds and revision. |
| `ui_set_value` | Inspected numeric Scale/Offset (-1000000..1000000), boolean Map visibility. Scale/Offset affect native Graph actions; `ui_plot_log` always uses original values. |
| `ui_operation_status` | Connection-local receipt for a mutation ID. |
| `mission_draft_get` | Page 1..200 items at nonnegative offset, with content revision, home, units and Undo availability. |
| `mission_command_schema` | Known native command IDs/names, available labels and frames; does not establish aircraft support. |
| `mission_draft_validate` | Pure structural validation, <=1000 items; finite values, coordinates, frames, command IDs and DO_JUMP references. |
| `mission_draft_replace` | Compare current revision then replace Mission items/home as one native Undo group. |
| `mission_draft_undo` | Compare revision then undo one planner history entry. May undo an operator edit; inspect first. |

Map tiles load asynchronously; a capture immediately after navigation/centering may
show a partially loaded map. Capture again after the view has rendered if needed.

UI actions use native typed adapters, not simulated desktop input. There are no
arbitrary keystrokes, coordinates, property names, file paths or window handles.
UI inspection is a complete inventory of this registered subset, not a full desktop
tree. Consent/password controls and aircraft command widgets are never targets.
Captures may show operator-provided log/mission locations; request only relevant surfaces.

## Example: view a recorded flight

1. `list_local_logs` -> choose `logId`; `log_schema` -> choose real fields/instances.
2. `ui_open_log({"operationId":"open-1","logId":"<catalogue ID>"})` -> receipt
   `result.viewId`, `result.revision`.
3. `ui_plot_log({"operationId":"plot-1","viewId":"<view ID>",
   "expectedRevision":"<revision>","fields":[{"type":"RATE","field":"R",
   "instance":"<schema instance>","rightAxis":false}],"startSeconds":10,"endSeconds":20})`.
   Omit instance only when there is exactly one source. TLOG instances are `systemId:componentId`.
4. `ui_get_state` -> select visible surface, then `ui_capture({"surfaceId":"<surface ID>"})`.
5. `ui_inspect` -> choose control and its advertised action. Pass snapshotId/controlId
   unchanged to `ui_invoke` or `ui_set_value` with a fresh operationId.

Plot times are boot/sample seconds for DataFlash, elapsed receipt seconds for TLOG.
DataFlash physical multipliers are applied; TLOG values retain native MAVLink units.
No smoothing or resampling is applied. Narrow the window if the point limit is hit.

## Example: prepare a local mission

Read all pages of `mission_draft_get` while checking the same revision on every page.
The first array element is displayed/onboard item 1; home is separate. Item properties:
`command`, `frame`, `latitude`, `longitude`, `altitudeMetres`, `p1`, `p2`, `p3`, `p4`.
Home properties: `latitude`, `longitude`, `altitudeMetres` (AMSL). Frames: 0 absolute
AMSL, 3 relative to home, 10 terrain-relative. Coordinates are WGS84 degrees.

Validate proposed home/items, then call `mission_draft_replace` with operationId,
expectedRevision, home and the complete items array. An empty array clears the draft.
Editing is allowed only with Mission selected; Fence/Rally are readable but not editable
through this API. Result `uploaded:false` is intentional. Review the resulting draft
and explicitly explain validation limits; structural validity is not flight clearance.
Undo requires a fresh ID and the current revision.

## State, cancellation and recovery

Every mutation reserves a receipt **before** dispatch. Identical method/typed arguments
and operationId return the same receipt, including `running`. Reusing an ID with different
arguments produces `operation_conflict`. A connection holds at most 256 receipts, without
eviction. At capacity, inspect uncertain outcomes before starting a new connection.

Statuses: `running`, `completed`, `rejected`, `cancelled`, `outcome_unknown`;
`ui_operation_status` additionally returns `unknown_operation`. Rejected/cancelled/native
failure does not promise rollback: inspect state before forming a new operation.
Receipts survive revoke/allow on the same session, not disconnect or application restart.
Never blindly replay a mutation on a new session after losing its original connection.

UI mutations are serialized across both HTTP listeners. Concurrent mutation returns
`ui_busy`; no hidden queue of later actions. Revocation/close cancels pending operations and checks
access again before dispatching pending UI changes. DataFlash indexing may finish its current
read before observing cancellation; it cannot apply results afterwards. An action already dispatched may
complete; cancellation cannot undo native UI changes.

A new inspection replaces the previous snapshot. UI mutations, revoked access, log
revision changes, hidden/disabled/detached targets, changed geometry/value or modal
windows invalidate controls. Reinspect after any action. User edits during asynchronous
plot parsing win: a stale expectedRevision is rejected. Revisions are opaque, not paths.
Native desktop dialogs and Setup/Config navigation remain operator-only.
