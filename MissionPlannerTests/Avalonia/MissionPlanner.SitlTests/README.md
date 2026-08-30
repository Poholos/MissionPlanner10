# MissionPlanner.SitlTests

Manual end-to-end harness for the MAVLink log download protocol
(`MAVLinkInterface.GetLog`) against a real ArduPilot SITL (Software In The
Loop) simulated vehicle. Not part of any automated run - it needs SITL
running and is driven by hand. Ported from the upstream fork's harness
(userepo/MissionPlanner `tests/MissionPlanner.ArduPilot.SitlTests`).

What it verifies:

- log listing (`GetLogEntry`) against the vehicle
- full download, timed, with the byte count checked against `LOG_ENTRY.size`
- optional byte-for-byte comparison against an oracle file (the log SITL
  wrote to its own disk)
- optional mid-download cancellation, including that the link stays usable
  afterwards

## One-time SITL setup (WSL Ubuntu)

```bash
mkdir -p ~/sitl && cd ~/sitl
curl -fsSL -o arducopter https://firmware.ardupilot.org/Copter/stable/SITL_x86_64_linux_gnu/arducopter
curl -fsSL -o copter.parm https://raw.githubusercontent.com/ArduPilot/ardupilot/master/Tools/autotest/default_params/copter.parm
chmod +x arducopter
```

Generate a static dataflash log (boot once with disarmed logging, then exit
so the file is closed), then copy it out of WSL as the oracle. See the
upstream harness README for the full recipe.

## Running

Start the vehicle. Keep stdin open when backgrounding it - with stdin at
EOF the SITL input thread wedges the boot before any MAVLink flows:

```bash
cd ~/sitl && sleep infinity | ./arducopter --model + --speedup 1 --defaults copter.parm --home -35.363262,149.165237,584,353
```

Run the harness (from `bin/Debug/net10.0` after `dotnet build`):

```
MissionPlanner.SitlTests.exe <host> <port> [oraclePath] [cancel]
```

`cancel` also runs the mid-download cancellation check.

## Lossy-link run

`lossy_proxy.py` sits between the harness and SITL and drops every 20th
LOG_DATA frame (~5% loss), forcing the repair phase to recover the gaps.
In WSL, with SITL already listening on 5760:

```bash
python3 lossy_proxy.py    # listens on 5770, forwards to 127.0.0.1:5760
```

Then point the harness at port 5770.

## Reference results (2026-08-29, 2.3 MB log, WSL2 loopback)

- clean link: 2,306,048 bytes in 0.44 s (~5 MiB/s), byte-identical;
  cancel raises OperationCanceledException and the link lists logs again
- 5% LOG_DATA loss: byte-identical in 77.3 s, one streaming pass
  (~1,290 scattered single-block gaps recovered by chained repair
  requests; ~3 s over the earlier 72.9 s reference is the silence window
  that confirms an end-of-log packet arriving far past a frontier
  stalled at the first dropped block)

Before repair-request chaining was added to `GetLog`, the lossy run did
not complete within 10 minutes: the repair phase served one gap per ~3 s
silence window (`LogRetryDelayMs`), roughly 65 minutes projected for the
same log. The chaining sends the next missing-range request the moment
the current one is satisfied, with `LogRepairDelayMs` (500 ms) as the
silence fallback for repair responses that were themselves lost.
