# Testing this build with SITL

SITL (Software-In-The-Loop) runs a **simulated ArduPilot autopilot** in software — no flight
controller, no aircraft, no risk. It is the correct way to check whether this GCS behaves before you
trust it near real hardware.

> **Safety first.** This is an unverified community port. SITL testing tells you whether a screen
> *works*; it does **not** make the app airworthy. For any real flight, use official Mission Planner
> or QGroundControl as the ground station. Bench-test a real Pixhawk only with **props removed** and
> **never armed**.

There are two ways to get a SITL, depending on the OS.

## A. Windows / Linux — built-in SITL (easiest)

The app can download and launch ArduPilot SITL itself.

1. Launch the unpacked release (`MissionPlanner.exe` on Windows or `./MissionPlanner` on Linux). If
   Windows SmartScreen warns, use *More info → Run anyway*; the development build is unsigned.
2. Click the **SIMULATION** tab in the top toolbar.
3. Pick the vehicle: **Plane**, **Copter**, **Rover**, or **Heli**.
4. **Model**: leave blank to use the vehicle default (fine for a first test).
5. **Channel**: choose **Stable** (most reliable). "Latest (Dev)" / "Beta" also work; "Skip
   Download" only if you already have a binary.
6. Optionally drag the **H** home marker on the map; set **Sim speed** = 1; tick **Wipe EEPROM** for
   a clean first run.
7. Click **Start**. First run downloads the SITL binary (needs internet); watch the status line.
8. On success it auto-connects over `tcp:127.0.0.1:5760` and jumps to **FlightData**. Status reads
   *"SITL running and connected."*
9. To stop: back to SIMULATION → **Stop**.

If Start fails, read the status/log line. Usual causes are no internet (download blocked), antivirus
quarantining the binary, or a busy port — Stop, retry, or pick a different channel.

## B. macOS, or an externally managed Linux SITL

The built-in launcher has no prebuilt binary on macOS. This method is also useful on Linux when you
want to build a particular ArduPilot branch yourself; start SITL externally and connect over UDP.

**Get SITL** (any one):

- Linux / WSL2 / macOS with the ArduPilot development environment:

  ```bash
  git clone --recursive https://github.com/ArduPilot/ardupilot
  cd ardupilot
  ./Tools/environment_install/install-prereqs-ubuntu.sh -y   # or install-prereqs-mac.sh
  ./Tools/autotest/sim_vehicle.py -v ArduPlane --out=udp:127.0.0.1:14550
  ```

  Use `-v ArduCopter` or `-v ArduRover` for another vehicle. `sim_vehicle.py` builds and runs SITL
  and forwards MAVLink to UDP `14550`.
- Or run SITL in Docker or on another computer and point its `--out` at this machine's IP.

**Connect this app:**

1. Launch the app and use the top-right connection bar.
2. Select **UDP** (listen) on port **14550**, or the port configured in `--out`.
3. Click **Connect**. Parameters download, then telemetry appears on FlightData.

## C. Validation checklist — what to actually exercise

Walk these with SITL connected. The goal is to find pages that read or write incorrectly before
anyone relies on them. Tick what behaves and report what does not.

**Connect and telemetry**

- [ ] Connects; full parameter list downloads without error.
- [ ] FlightData HUD attitude, altitude, speed and heading move and look sane.
- [ ] Mode shows correctly; arm/disarm from the GCS reflects in HUD (SITL, props irrelevant).
- [ ] Map shows the vehicle at the home location and follows movement.
- [ ] FlightData → Quick has no permanent row/column controls. Right-click the Quick grid, choose
  **Set View Count…**, change the layout, and verify the saved grid survives a restart.

**Mission**

- [ ] PLAN: draw waypoints, **Write** to the vehicle, then **Read** them back and compare.
- [ ] Survey (Grid): enable camera/speed/takeoff/finish options, accept, and inspect the generated
  `DO_*` and navigation commands before writing them.
- [ ] Auto mode flies the mission in SITL.

**Joystick**

- [ ] Setup → Joystick: enable the device, map roll/pitch/throttle/yaw, and verify raw values move.
- [ ] On Linux, if raw endpoints do not reach `0..65535`, press **Calibrate Range**, move every
  required axis to both endpoints, return throttle low and the other sticks to centre, then press
  **Finish Calibration**. Enable the joystick again and verify output reaches the configured RC
  limits monotonically (normally about `1000..2000`).
- [ ] Leave Setup: control remains active in Flight Data and moves SITL RC inputs at 20 Hz.
- [ ] Press **Disable Joystick** in Flight Data: control stops and all RC overrides are released.

**Configuration pages**

- [ ] **Radio Calibration** bars move with SITL RC; reverse checkboxes write `RCn_REVERSED`.
- [ ] **Flight Modes** reads the six mode slots and Save writes them back.
- [ ] **Failsafe / Battery Monitor** values populate and calibration writes expected parameters.
- [ ] **Compass** lists compasses; reorder and Use controls write `COMPASS_*`.
- [ ] **Serial Ports / ADSB** OPTIONS bitmask flyouts toggle the right bits.
- [ ] **Full Parameter List**: edit one parameter, write, refresh, and verify it persisted.
- [ ] **Antenna Tracker** only enables for ArduTracker firmware.

**Developer tools**

- [ ] Press **Ctrl+F**: Setup opens on **Developer Tools**; Ctrl+I/G/L open MAVLink Inspector, NMEA
  Output and DataFlash Spectrogram without closing the current connection.
- [ ] Setup → Advanced → **Mission Command List**: add a temporary vendor command such as `60000`,
  save it, and verify its name and P1–P7 labels appear immediately in Flight Planner.
- [ ] Tools → **Cursor-on-Target / TAK Output**: start UDP Client or TAK Multicast output and verify
  a CoT 2.0 event is emitted for the connected SITL system at the selected interval.
- [ ] Tools → **Tlog Convert / Extract**: export a tlog to CSV/text and extract parameters; for a
  log containing a mission transfer, verify the QGC WPL mission opens in Flight Planner.
- [ ] Keep SITL disarmed while checking QNH, recovery parameter restore, MAVFTP download, reboot or
  calibration-recovery actions. Do not use bootloader/DFU actions without disposable test hardware.

**Stability**

- [ ] Switch between all six top tabs repeatedly without a crash or frozen telemetry.
- [ ] Leave it connected for about ten minutes without link loss or runaway memory.

Anything that misbehaves is a real bug to fix before this port is trustworthy. Until the checklist
passes cleanly for the screens you depend on, fly with official Mission Planner, not this build.
