# NV Modem setup

Setup > NV Modem is the Avalonia port of the `NV5Settings` widget from the local AgroSky GTU tree.
The implementation was compared with the current GTU `master` at commit
`5771e7b0c68f09b64c2407ad46bf5466ba275aee`. The relevant source specification is
`hermes-gui/include/nv5settings.h` plus `hermes-gui/src/nv5settings.cpp`.

## Connection and device identity

The page does not ask for an address or open another UDP, TCP or UART connection. It subscribes to
every open Mission Planner `MAVLinkInterface`, registers the private SkyComm MAVLink message
layouts with the shared parser and sends each request back through the exact interface on which the
modem was observed. A device key is therefore:

`MAVLink interface + system id + component id`

This keeps modems with identical MAVLink IDs on different network or serial links independent.
NV4 replies use the same observed Mission Planner link, which is the port equivalent of GTU's dirty
addressed-UDP-route fix. Discovery has no system-ID or component-ID range. Current NV4 and NV5
devices are identified by the periodic `NV_MODEM_INFO` passport (`53016`), including receive-only
or unconfigured hardware with no live radio traffic. Older NV5 firmware falls back to its private
live-status/configuration messages. Unmodified NV4 firmware falls back to `NV_RX_STAT` or either GTU
`UAVCAN_NODE_INFO` signature: current `NV5Settings` uses hardware and software major version 4 with a
name beginning `TX_` or `RX_`, while the legacy NVStat/RFM path accepts case-insensitive `NV_TX` or
`NV_RX` prefixes even when old firmware leaves the version majors unset. NV5 parameter-family
signatures are also accepted, while an NV4 parameter
can only refine an already identified device, matching GTU's false-positive protection. The page
replays all discovery packet types from the shared Mission Planner cache when it is opened after a
modem was already seen, and requests the passport, NV5 status, and CAN node information from every
observed address as well as by broadcast. The private SkyComm dialect is registered at application
startup, before any shared connection starts reading, so an early identity/status packet is not
lost while the setup page is still closed. An ordinary `AUTOPILOT_VERSION` is not enough to classify
a flight controller as a modem. The corrected singular NV4 apply parameter is `REFRESH_SETTING`.

## Settings behavior

The page includes:

- typed, bytewise MAVLink integer parameter decoding and encoding;
- complete NV4/NV5 parameter descriptions copied from `NV5Settings`, displayed in the
  **Description / values** column and retained in the port source catalog;
- explicit changed, invalid and read-only state in the parameter table;
- live NV4 or per-radio NV5 link status;
- LR2021/LoRa/FLRC, FHSS, FEC and role presets, staged locally until **Save**;
- channel-settings copy from another completely read NV5 modem;
- NV4 32-byte and NV5 16-byte encryption keys, printable or hexadecimal (with an optional `hex:`
  prefix), with generation and fingerprints;
- UINT32 representation for individual NV5 key-byte parameters plus atomic persistence of the
  complete selected one- or two-channel key snapshot through `NV_ENCRYPTION_KEYS_SET` (`53017`),
  with idempotent retries and final `NV_ENCRYPTION_KEYS_ACK` (`53018`) fingerprints;
- RTSP path get/set and transport presets for supported LR2021 configurations;
- transmitter enable/suppress diagnostics and standard MAVLink reboot;
- Mission Planner-compatible `.param` import/export. Exports carry a sensitive-data warning because
  they can contain readable encryption keys and network settings.

Parameter snapshots are deliberately not written to application settings. Selecting another modem
or refreshing the current one clears the visible list before requesting new values. A silent modem
is retried a bounded number of times and then reports an error without blocking connection or device
selection. Writes are serialized, acknowledged and retried; while one is in flight the target
selector is locked. A target/link change during confirmation prevents the operation.

## Acceptance boundary

The shared Mission Planner parser, custom CRC/layouts, multi-link target isolation, NV4 apply
transaction, atomic NV5 keys, RTSP dirty-state handling, preset staging, parameter-file roundtrip
and silent-device timeout are covered by automated tests. A representative physical NV4 and NV5
modem on UDP/TCP/UART still require an operator acceptance run, including reboot/reappearance and
real RF/RTSP behavior.
