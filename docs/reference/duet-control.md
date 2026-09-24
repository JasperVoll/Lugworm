# Duet WiFi Control — "7 Print" tab

Direct control of the 3DPotter Potterbot Scara v4 (Duet 2 WiFi, standalone
RepRapFirmware) from Grasshopper, without Duet Web Control. Transport is the
standalone `rr_*` HTTP API, implemented clean-room in `Lugworm.Core/Duet/`
(the GPL DuetSoftwareFramework client was used as a protocol reference only).
Two firmware paths, auto-detected at connect:

- **RRF ≥ 3.2**: `rr_model` object-model queries (+ `X-Session-Key` sessions).
- **RRF < 3.2 (the path the lab machine used before its update to stock 3.5.1; it shipped as 3.1.1 "3DPotter Edition Rev10")**: legacy
  `rr_status?type=3` polling with the same snapshot/gate semantics.

Since 4.0.0 the Duet client is one implementation of a transport seam
(`Core/Machine/IMachineClient.cs`). 4.0.0 put a second transport (Klipper through
Moonraker) behind that seam; **4.1.0 removed it** - it was never connected to a real
printer and the lab prints LDM only. Duet is the only transport. The seam stayed,
because it is what keeps the safety gates independent of any one client.

## Coordinate frames (since 1.13.0)

**The Rhino design frame is ground truth.** The machine's own Cartesian frame — what
G-code X/Y words mean, what the Duet reports, what `M208`/`M669` are expressed in — is
the Rhino frame **mirrored about the line y = -x**:

```
machine (x, y, z)   <->   Rhino (-y, -x, z)
```

The post-home pose the firmware calls `X200 Y-500 Z400` is the point the operator sees at
Rhino `X500 Y-200 Z400` (established on the machine, 2026-08-26). The map is its own
inverse, and it is a **reflection**, so distances and radii are preserved but orientation
is not: arc direction (`G2` ↔ `G3`) and contour winding flip with it.

`Lugworm.Core/Geometry/MachineFrame.cs` is the only definition of the map. The split
is by purpose, not by project:

| Machine frame | Rhino frame |
|---|---|
| `ScaraKinematics`, `RobotChecks`, `J1`/`J3`/`J2`, joint ranges | Curve inputs, all viewport geometry |
| `RobotLimits.XMin…YMax` (the M208 box), `ReachMin/Max` (symmetric, so unchanged) | Bed plate, printable area, problem markers |
| `GcodeParser`, `JobGate`, `JogPlanner` | Jog capsule directions, `pos`, `bedOrigin`, `start` |
| G-code text, Duet telemetry, `homeall.g`, `robot.xml <HomePose>` | Posed robot meshes, trail, print-log `path` output |

Anything that validates or commands the machine keeps the machine's own numbers so it can
be read against `config.g` line for line; everything a person draws or reads is converted
at the component boundary.

Consequences worth knowing:

- **Bed inputs are Rhino.** The lab plate defaults to Print Bed's `center` `(468, -441)`,
  `width` 1000 along Rhino X, `depth` 700 along Rhino Y. (Before 4.1.0 these were Live
  View's own `bedCenter` / `bedWidth` / `bedDepth`; a saved document migrates them into a
  real Print Bed.) Definitions saved before 1.13.0 hold machine-frame values — re-enter
  them, or re-capture `bedOrigin` from the console.
- **`Live View` publishes the machine's own joint angles.** Only the drawn geometry and
  the `pos` point are converted, so `J1`/`J3` still match `M114`. The arm is drawn as the
  true machine configuration reflected into the Rhino frame — the viewport poses the
  assembly at reflected joint values (`J1 -> -90 - J1`, elbow angle negated), which puts
  every joint centre where the reflection puts it without mirroring any part.
- **The base (Fundament) placement is model data, not a joint.** J1 swings the whole
  Z-rail about the pivot together with the proximal arm; the Fundament underneath is the
  one link nothing drives, so `robot.xml`'s `<BasePlacement rotDeg offsetX offsetY>` is
  its entire pose — positive rotation is counter-clockwise seen from above, the offsets
  are Rhino XY millimetres, and the whole thing is drawing-only. Confirmed on the machine
  2026-08-26 at `-180`, `(3, 82)`; corrected on it 2026-09-04 to `-180`, `(3, 44)` — with
  the J1 pivot on the Z-rail axis the column turns about the right point, but the
  Fundament sat 38 mm too far in +Y, so the base alone moved 38 mm in -Y. To re-calibrate, drive trial values through the
  `RobotModel.PosedMeshes`/`PosedFeatureEdges` overloads that take an explicit placement,
  then write the settled numbers into `robot.xml`.
- **`robot.xml` is pinned by `ShippedRobotModelTests`** and will fail the build on any
  edit. That is deliberate: the model has been corrected against the machine four times,
  and the FK self-check cannot catch a wrong joint chain — a vertical lift commutes with
  a vertical-axis rotation, so the nozzle lands in the same place either way. Change the
  numbers there only with a measurement behind them.
- **The print-log CSV keeps machine coordinates** — it is a machine record. The `path`
  point output beside it is Rhino.

## The actual machine (SN SCV4-2373 — verified against the live SD-card files, 2026-07-17)

- `M669 K4 P549.06 D457.23 A-41.1:319.1 B-151.2:151.2 C0:0:0 T1 R235`
  — L1/L2 and joint ranges differ from older documentation; all defaults in
  Core/components/robot.xml match this config.
- **Z travel is capped at 400 mm by `M208 Z400`** (vendor "Not Calibrated"
  config; the physical rail is ~57" ≈ 1448 mm). All gates use 400 until the Z
  axis is calibrated — then raise M208 in config.g AND the zMax/J2 defaults.
  Firmware axis limits: `M208 X-300:10000 Y-10000:0 Z0:400` — the workspace
  is the **negative-Y half-plane**.
- **Z homes UP** (high-end endstop, `M574 Z2`): after `G28` the head sits at
  the top. Per-axis homing macros match the Jog component's mapping:
  `G28 X` → `homeproximal.g`, `G28 Y` → `homedistal.g`, `G28 Z` → `homez.g`.
- `homeall.g` homes Z, then both arms, then parks with `G0 X200 Y-500 F4000`
  — X200 Y-500 Z400 is exactly the calibrated `<HomePose>` in `robot.xml`
  that job validation anchors to (Rhino `X500 Y-200 Z400`; see *Coordinate
  frames*). Since 2026-08-25 it delegates at least the Z axis with
  `M98 P"0:/sys/homez.g"`, and has also shipped fully delegated
  (`homez.g` + `homeproximal.g` + `homedistal.g`). **The slicer follows `M98`
  calls** (1.13.0): it downloads the whole include tree from `0:/sys` before
  parsing, so the homing trigger can live inside a callee. Before that fix the
  quoted macro path was rejected as an unsupported token and the arm
  configuration stayed `Unknown` after every `G28`.

- **`pause.g` does NOT home** — it lifts the head 30 mm (relative Z) and
  stays; `resume.g` returns via `G1 R1` restore points (2 mm above, then
  down) and re-primes 0.05 mm (ram-scaled). **`cancel.g` is the macro that
  homes**: `M400`, lift 30 mm, then `G28`. `stop.g` and `sleep.g` are empty.
- Motion limits from config.g: `M203 X3600 Y3600 Z1500 E75` (max speeds,
  mm/min), `M566 X800 Y800 Z600 E60` (jerk, mm/min), `M201 X800 Y800 Z500
  E100` (accel, mm/s²). The plugin reads this file at connect as the immutable
  motion baseline. At upload and start it scans the job text and queries the
  current M203/M201/M566 values inside the gated transaction, so an earlier job
  or DWC change cannot silently poison the comparison. A missing config.g is
  reported as unverified but never blocks printing.

- Firmware raises no heater/temperature objects (clay/cold-extrude config:
  `M302 P1`, relative extrusion `M83`, tool 0 = "clay").

The SCARA `elbow`/arm-mode input is a **fallback** arm mode, not a preview toggle and
no longer a "preferred" one. Three sources decide the arm mode, in this order:

1. **The session's tracked `{armMode, thetaTurns}`** from a verified home. It governs
   jog, upload, start validation, and the arm `Live View` draws. While the
   configuration is `Unknown`, configuration-sensitive actions are refused outright.
2. **The machine's homed arm mode** (`robot.xml` `<HomePose armMode>` →
   `RobotLimits.HomedArmMode`) whenever a path is anchored by a `G28`. This is what
   establishes the configuration after a verified full `G28` and what seeds an offline
   `RobotChecks`/`JobGate` walk over a job with a homing header.
3. **`elbow`**, only where nothing anchors the walk: a program with no `G28`, and
   `JogPlanner` before a home.

Until 1.12.0 step 2 did not exist and `elbow` filled its place, defaulting to *up*
while the machine rests in *down*. At the home pose both branches are inside `A`/`B`,
so nothing failed — the simulated arm was simply drawn as the mirror image, and a
verified home recorded that same wrong branch as the machine's configuration. The
default is now *down*, and a home-anchored path ignores the input entirely.

**Refusals are visible.** A disabled capsule carries its reason in a tooltip, which is
invisible to an operator looking at a component that has simply stopped responding.
Every control component now also raises the active refusal as a runtime message —
machine-state refusals (notably "arm configuration is Unknown; complete a verified home
first") as warnings, connection/busy and rejected terminal input as remarks, and routine
prerequisites not at all. The message clears on its own once the refusal lifts, so a
successful `Home all` both re-enables the buttons and removes the warning.

## Components

Since 3.0.0 there are **five** components on the `7 Print` ribbon. Machine control lives
in one window. In 4.0.0 *Duet Console*, *Duet Files* and *Duet Live* became
**Machine Console**, **Machine Files** and **Live View**. 5.0.0 named the console **Duet Console**
again, because only Duet boards are controlled directly. Only the ribbon name and the
nickname changed: the GUIDs, sockets and wires are untouched.

| Component | Purpose |
|---|---|
| **Duet Console** | The single control surface. Press the **Console** capsule to open its window (press again to front it). Connect / Disconnect, Home all and per-axis, press-and-hold jogging with whitelisted steps (0.1/1/10/50 mm) and capped feed (≤ 50 mm/s), bed-origin capture, Validate → Upload → Start/Pause/Resume/Cancel, a terminal, go-to point, and the session log. E-STOP and Reset are native buttons in the window footer. Outputs `host` for the other components, plus status, position, per-axis homed flags, captures, upload record, the validation verdict and the status readings. |
| **Machine Files** | Remote file management on the board (`/sys` and `/firmware` protected). |
| **Live View** | Read-only live view of the machine and its observed toolpath. It records nothing: the print path log is the console's, always on. |

### Hidden compatibility components

**Duet Connect** (`…0015`), **Duet Status** (`…0016`), **Duet Jog** (`…0017`), **Duet
Terminal** (`…0018`) and **Duet Print** (`…0019`) were superseded by the console in 3.0.0.
They are `GH_Exposure.hidden` — off the ribbon but still loaded, with GUIDs, sockets, gates
and persistence byte-identical — so existing definitions open and solve unchanged. Two
behaviours survive **only** on them and were deliberately not carried into the console:
the Terminal's `expert` toggle, and the Jog component's armed any-state Reset override
(the console's Reset requires Halted/Off).

### What the console does differently

- **Reset (M999)** is allowed only while the machine is halted or off, including from the
  Terminal tab, so the tab cannot bypass the button. Recovering a wedged-but-not-halted
  board from the console means pressing E-STOP first — that halts it — then Reset.
- **Terminal** has no expert unlock: opening the tab is the expert act. The diagnostic
  allowlist and the two-press confirmation for motion/config/job commands are unchanged,
  and an advanced line still invalidates the tracked arm configuration.
- **Jogging is press-and-hold**, with at most one jog in flight at any time; the repeat
  waits for a poll that began after the previous step was accepted.
- **Validation** runs at solve, cached by content hash, and publishes `danger`,
  `problems` and `messages` exactly as the Duet Print component did. The **Validate**
  button has its own gate (gcode present, profile resolved): it needs no session and no
  machine trust, because `JobGate` is pure - upload and start keep every transaction gate.
- **Start** is labelled from the projected `dryRun` mode (`Start (dry M37)` / `Start`) and
  really sends M37 in dry-run mode; the go-to list is **paged**, a replaced point list clears
  the selection so an index can never name a different target; a held jog is released on every
  way the mouse-up can be lost (leave, capture loss, page change, deactivation, hide, renderer
  swap, dispose) and, independently, on the component side; the Skia surfaces are guarded
  around the whole paint lifecycle (a GL-less host falls back to CPU with the reason recorded);
  a console-entered `M999` is re-checked against the transaction's own fresh snapshot.

### The console shell (since 3.0.0, 2026-09-07)

The window is a persistent command shell: a left navigation rail (**Dashboard - Control -
Job - Terminal - Logs**), a status header (connection lamp and host, board, machine state and
readiness, active job with progress and layer, a logging badge, Connect/Disconnect) and a
native footer with the last result, logging health when it needs attention, **Reset** and the
rightmost, largest **E-STOP** - both still WinForms buttons outside the Skia surface. Dark
graphite palette, body text 14 px, buttons 38/42 px, icon + label on every principal control,
the refusal reason in the tooltip of every disabled control, hover/press/navigation
transitions of 90-160 ms that never delay a command, an animation timer that runs only while
a transition or pending indicator is live and the window is shown, and a persisted **reduced
motion** preference. Default 960×700, minimum 820×620 at 100 % display scaling, scaled with
the monitor's DPI and never larger than the screen's working area; window bounds persisted;
the page never is (every session opens on Dashboard).

- **Dashboard**: readiness, position, active-job and logging cards, recent warnings first, and
  ONE state-derived primary action (Connect → Home all → Validate → Upload → Start / Resume /
  Reset / Open Job) that names the same action id and gate as the full page and never sends
  anything by itself (`ConsoleNextAction`, Core, tested).
- **Control**: XY cross with a separate Z column, segmented step, live position with homed
  lamps, Home all dominant over per-axis homing, origin group, paged calibration list + Go to.
- **Job**: explicit Validate → Upload → Start stages with their evidence, Pause/Resume together,
  Cancel apart, validation messages expandable.
- **Terminal**: native input, amber confirmation strip naming the exact armed command and its
  timeout, last reply, readable history.
- **Logs**: see the next section.

### Job-log retention (since 3.0.0, 2026-09-07)

Every observed job is recorded **temporarily** to a local staging buffer first
(`%LOCALAPPDATA%\Lugworm\PrintLogBuffer\<job-id>\` with `print.csv`, `events.log` and an
atomic `manifest.json` that never holds a password). It is always on and needs no
configuration; Live View's own `log` group, which this once ran independently of, was
removed in 4.1.0. The buffer is canonical until a destination copy is
verified. States: *Temporary recording*, *Permanent recording*, *Temporary complete*,
*Syncing*, *Waiting to sync*, *Saved*, *Recovered temporary log*, plus a *Logging fault* for
a failing local disk that can never be reported Saved.

- **Keep all job logs** (persisted, user-global, asks for a local or UNC folder first) makes
  every job permanent from its first record; **Save this job** promotes one active or completed
  job. Kept jobs mirror append-only to `.partial` files at the destination; the destination
  length is the acknowledged offset, so a retry after an ambiguous failure resumes from what
  landed and never duplicates a row. **Change location** resets the offsets and replays the
  intact buffer from byte zero; a name collision gets a deterministic suffix. Finalisation
  renames `.partial` to the final names, verifies the lengths and only then removes the staging
  buffer.
- A vanished share shows amber *Waiting to sync*; local recording continues; **Retry** or
  **Change location** recover without losing a record. A corrupt manifest is quarantined, never
  deleted. A clean Rhino shutdown waits up to 3 s for the sessions to finish, so a running
  job's last records land, then removes completed, unsaved temporary buffers; anything else
  is offered as a *recovered* log at the next launch. **Export copy** writes the staged CSV
  elsewhere, and is disabled for a *Saved* job, whose buffer is gone once its kept copy is
  verified; **Discard buffer…** is a separate confirmed action. *Clear live view* only clears
  the displayed session tail.
- All staging appends and destination writes run on one worker thread with a bounded queue;
  the poll loop only enqueues, so a stalled destination cannot touch machine control.

### Live viewport (Live View, since 3.0.0, 2026-09-07)

Live View no longer re-solves the Grasshopper canvas to animate. A controller turns every
completed poll into an immutable observed sample, animates a **delayed interpolation** between
observed samples on a 60 fps pump (one coalesced UI callback, no catch-up, no perpetual repaint
at rest, 30/15 fps fallback under cost pressure) and draws through a Rhino display conduit
using the shared base meshes under per-link transforms. The frame is labelled
*smoothed - delay N ms, sample age M ms*, *observed*, or *STALE*; homing, halt, disconnect, a
branch change or a new job start a new epoch that is never interpolated across; the delay is
about the poll interval plus 20 ms (100-500 ms), so the picture is smooth but is an estimate
of a delayed position, never a measurement. The trail, print log, `pos`/joint outputs and every
gate read observed telemetry only.

Context menu: **Automatically update GH outputs** (new instances off = outputs are a snapshot
of the last solve; archives that predate the key keep automatic mode, now at most once per
second), **Smooth live animation**, **Colour the observed trail by deviation from the intended
path (Viridis)** with a fixed legend maximum (1-20 mm). Deviation is the nearest geometric
distance from each observed position to the wired G-code path (machine frame), computed at
telemetry cadence on a bounded spatial index; crossings and repeated passes are flagged
ambiguous (drawn dimmer), unavailable comparisons are grey, above-range saturates to yellow.
Controller-reported position is not independent metrology, and path distance is not following
error in time.

## Generic Duet machines (since 2.2.0)

The control components default to the lab's Potterbot SCARA when their `robot`
input is unwired — every saved document keeps its exact pre-2.2.0 behaviour. To
drive a **Cartesian** Duet machine, wire a Robot Definition set to
*generic-duet-cartesian* into the **Duet Console** and **Live View**:

- Gating switches from the SCARA check set to `CartesianChecks`: M208 envelope
  + Z bounds + feed, with arc interiors sampled. No reach annulus, no arm
  configuration requirement, no mast check — and no `y = -x` frame mirror, the
  machine's own coordinates are shown.
- **Unwired limit inputs defer to the firmware's own M208 report.** Until the
  first report arrives (or if the board doesn't provide one), jogging and
  validation refuse with "machine envelope unknown" — the Potterbot's envelope
  literals are never applied to a generic machine. Wired limit inputs still win.
- Upload/start additionally require per-axis M208 verification, and refuse if
  the board's `move.kinematics` parsed as SCARA (the definition then does not
  match the connected machine).
- State gating (fresh snapshot, idle, homed), the explicit-action rule, CRC
  upload verification and the config.g motion-baseline checks apply unchanged.
- A profile that does not advertise Duet control (`none`, *generic Cartesian*)
  blocks every planned action with the reason; E-STOP/Reset stay live.
- Per-axis `G28 X/Y/Z` in an
  uploaded job file follows the kinematics (since 2.3.0): refused for the SCARA,
  where joint homing moves both Cartesian axes, allowed for a Cartesian profile,
  where each axis homes independently.

## Safety model

1. **Nothing is ever sent on recompute.** Solves only *read* the latest poll
   snapshot; every send starts from an explicit capsule press.
2. **Every motion is validated host-side before transmission.**
   - Jogs/go-to-point: `JogPlanner` (Core, unit-tested) refuses stale status
     (> 3 s), non-idle machine, unhomed axes, non-whitelisted steps, over-cap
     feeds, and any absolute target that fails the machine's check set — the
     full `ScaraKinematics` + `RobotChecks` set (reach annulus, dead zone, Z
     range, joint ranges, mast clearance) for the Potterbot, or the
     `CartesianChecks` envelope for a generic Cartesian profile.
   - Jobs: `JobGate` re-runs the exact `RobotChecks.Run` the Robot simulation
     component uses (or `CartesianChecks.Run` under a Cartesian profile).
     There is deliberately **no wireable `danger` input** — the
     gate cannot be bypassed by leaving a wire off.
3. **E-STOP ignores every gate.** Red capsule, always enabled while a session
   exists; sends `M112` outside the command queue with a forced fresh-session
   retry. Recovery = armed double-press Reset (`M999`); the session auto-reconnects.
4. **One session per host** (the Duet 2 has a tiny session pool); it survives
   WiFi drops via exponential-backoff reconnect and keeps the last snapshot
   stale-stamped — the jog gate then refuses to act on it.
5. **Simulation before execution:** validate the Potterbot profile in Robot Simulation
   (`reachOK`, `problems`, and `messages`), then let Duet Print re-verify; `dryRun`/M37 adds a firmware-side
   simulation pass with a time estimate before the first real run.

6. **Motion limits are checked at the hardware gate.** `config.g` is the intended
   baseline, normalized once from G-code units (`M203`/`M566` mm/min; `M201`/`M204`
   mm/s²; `M205` mm/s). `M203` is the exception that bites: **`M203 S1` switches it to
   mm/sec**, matching RepRapFirmware's own `S1` test, and both the config side and the
   job side honour it — a scanner that always divided by 60 would read `M203 S1 X100` as
   1.67 mm/s and miss a job raising the ceiling 60-fold. A job raising
   M203 is also a warning because it removes firmware clipping; a requested
   per-axis feedrate above an intact M203 is informational because it affects
   timing only. `blockWarnings` applies to these warnings; informational findings
   never refuse an upload or start. The live query is read-only, and a failed
   baseline or live probe is surfaced as unverified rather than becoming a new
   refusal path.

## Hardware bring-up checklist (run in this order)

### M1 — connect / status / jog
1. Find the printer's IP (Duet 2 in AP mode: `192.168.1.1`; on the lab LAN check
   the router or `M552` via USB). Enter host + password, press **Connect** with
   DWC open in parallel; verify board type/status match. Right-click →
   *Dump raw object model* to capture test fixtures.
2. Verify homed flags and position against DWC. **Check `M115`** in the console's Terminal tab
   (the lab board shipped with RepRapFirmware 3.1.1 "3DPotter Edition" and now runs
   stock 3.5.1 with that configuration; the Connect info line names the API path it
   detected). Check whether `machinePosition` equals DWC's
   machine coordinates (work offsets — see risks).
3. **Home** from the console's Control tab with the area clear.
4. Single **1 mm jog near the workspace centre** at low feed; confirm direction
   and magnitude against DWC.
5. **E-STOP drill**: start a slow 50 mm jog, press E-STOP mid-move (nothing near
   the bed). Confirm the halt, then Reset (double-press) and watch the session
   reconnect. Do not skip this drill.

### M2 — job control
6. Upload a known-good file — confirm byte size + CRC verification in the log.
7. `dryRun` (M37) the job; compare the firmware time estimate with Print Stats.
8. **Air print**: re-generate the job with +50 mm Z offset, no clay; exercise
   Pause/Resume/Cancel and watch progress.
9. First wet print, small object, hand on the physical E-stop.

### M3 — calibration
10. **Stage 1 (origin):** jog the nozzle to the bed reference point → *Set bed
    origin*; feeler-gauge the nozzle onto the bed → *Set Z=0*. Use the measured
    plate centre and print centre in Print Bed, then wire its definition to Place and
    Robot Simulation.
11. **Stage 2 (kinematics) is no longer part of the plugin.** The Calib Points and
    Scara Fit components that generated the mark grid and solved for `L1`/`L2`, the
    joint offsets and the grid frame were removed, together with
    `Core/Calibration/CalibrationSequence.cs`, `ScaraFit.cs` and
    `CalibrationReport.cs`. To repeat a kinematic fit, drive the marking points
    through the Duet Console's own `points` input and *Go to point*, and do the
    least-squares fit outside the plugin.
12. Apply a fit made that way as before: paste the corrected link lines into
    `Resources/robot.xml` and update the shipped Potterbot profile values in
    `RobotCatalog.PotterbotLimits`; apply the corrected `M669` to config.g and fold
    the J3 offset into the homing macro. Command 3 fresh verification points and
    re-measure.

## Known limitations / risks

- Legacy path notes (RRF < 3.2, the path the lab board used before its 3.5.1 update): `rr_upload`'s `crc32` parameter may be ignored
  by old firmware — the byte-size verification via `rr_fileinfo` still runs.
  `M37 P"file"` dry-run must be verified on hardware.
- The sign convention of the firmware J1 range flipped between config revisions
  (A-41.1:319.1 now vs A-311.1:49.1 documented earlier) — both span 360.2°, so
  J1 range checks are permissive either way; confirm jog directions at bring-up.
- **J1 zero offset is fundamentally unobservable** from TCP marks measured in a
  grid of unknown orientation (exact gauge freedom with the grid rotation) — the
  fit freezes it by default; a pure J1 zero error only rotates the workspace and
  is absorbed by the Stage-1 bed-origin capture.
- **Work offsets:** jog validation assumes `move.axes[].machinePosition` is the
  robot/IK frame. If config.g applies G10/M208 offsets, compare
  `machinePosition` vs `userPosition` during bring-up step 2.
- `rr_connect` err 2 = the Duet's session pool is full — close DWC tabs.
- The Duet drops idle sessions after ~8 s; the ≤ 4 s poll keeps it alive and the
  reconnect path covers stalls (debugger breakpoints land here by design).
- Passwords internalized in a .gh are stored in the file (LAN lab device).

## Print path log (the recorded CSV; since 1.10.0)

While a job runs, every status poll is fed to a recorder that writes a timestamped row of
the real nozzle path. **It is always on and needs no configuration**: `MachineSession`
drives it, so it records whether or not a Live View is on the canvas. Where the file goes,
when it becomes permanent and what the Logs page offers is
[Job-log retention](#job-log-retention-since-300-2026-09-07) above; this section is the
**content** of that file.

One `print.csv` per job run, in the run's own staging folder
(`%LOCALAPPDATA%\Lugworm\PrintLogBuffer\<job-id>\`) and mirrored to the destination
folder once the run is kept.

**Retired sockets (4.1.0, 2026-09-09).** Live View used to arm this through a `log`
toggle group (`logEnable`, `logEveryN`, `logFolder`, `logPrefix`, `logLayerHeight`,
`logExport`) and report it on the `layers`, `log`, `path` and `logInfo` outputs. All ten
are gone, together with the right-click group. There is nothing left to arm; a document
that carried them reports them once on load and drops them.

CSV columns: `seq, kind, job_run, job_file, layer, layer_source, utc_iso, local_iso,
job_elapsed_s, dt_s, x, y, z, speed_observed_mms, speed_requested_mms, speed_top_mms,
speed_factor_pct, raw_extrusion_mm, fraction_printed, file_position, status,
boundary_uncertainty_s, finish_reason, terminal_status`.

- `kind` is one of `JobStart, LayerStart, Interval, LayerEnd, JobEnd`.
- `layer_source` records whether the layer came from the firmware counter or the
  Z-step fallback.
- `speed_observed_mms` is derived from consecutive positions (the true poll-window
  average); `speed_requested_mms` / `speed_top_mms` come from the firmware and are
  instantaneous at poll time. They answer different questions and can disagree.
- `raw_extrusion_mm` is the firmware's *raw* total (legacy `extrRaw`, RRF3
  `job.rawExtrusion`) - not `coords.extr`, which has the extrusion factor applied.
- `finish_reason` is empty on ordinary rows and names why a run was cut short
  (`SessionStopped`, `ConnectionLost`, `LoggingDisabled`) on its terminal rows.
- `status` and `terminal_status` carry the *parsed* state name (`Processing`, `Idle`,
  `Cancelling`) on every board. The legacy API reports protocol letters (`P`, `I`) and
  RRF3 reports words; the log normalizes both so a filter written against the CSV works
  the same on either.
- `terminal_status` is the machine state that ended the run, on the run-end rows only.
  It is the column to filter on: `status` on those rows is the state of the last
  *printing* poll, because that is where their position and timestamp come from.
  **A run whose `terminal_status` is not `Idle` has an unreliable final layer** —
  exclude it.
- **Legacy caveat (RRF < 3.2, including the Potterbot): a cancelled run cannot be
  recognised from `terminal_status` alone.** The legacy `rr_status` API has no
  cancelling state letter, so an operator cancel lands on `Idle` exactly like a clean
  finish. On that board use `fraction_printed` on the run-end rows — a cancel stops
  short of 1.0 — or the layer count against the sliced job. RRF 3.2+ reports
  `Cancelling` properly.

### Accuracy limits — read before using this for analysis

- The poll runs at ~1 Hz, so **layer timestamps are accurate to ±1 poll**. Every
  boundary row carries its own `boundary_uncertainty_s`; use it.
- `utc_iso` is stamped host-side when the response is parsed and therefore includes
  network and parse latency. `job_elapsed_s` comes from the firmware and is the better
  *relative* clock; `utc_iso` is the right column for correlating with external
  instruments.
- Positions are the firmware's current position sampled asynchronously; under
  lookahead they can lag the G-code line being consumed.
- At 1 Hz and 30 mm/s consecutive samples are ~30 mm apart. Interval rows trace the
  path coarsely; they do not reconstruct it.
- **This is not a substitute for a hardware trigger.** It is good enough to know which
  minute a layer was printed in, not which millisecond.

### Operational notes

- Recording is read-only: it sends nothing to the machine and passes no gate. A CSV
  write failure disables the file sink and shows as a *Logging fault* on the console's
  Logs page; it never disturbs the connection or the tracked arm configuration.
- **All disk work happens on a dedicated background thread.** The poll loop only
  enqueues. A file write has no timeout, and the poll loop is what keeps the session
  alive and keeps gate snapshots fresh, so a folder on a disconnected network share
  would otherwise stall machine control. If the sink cannot keep up, rows are dropped
  and counted rather than allowed to push back — losing log rows is acceptable,
  stalling the session is not.
- **One recorder per session, not per component.** `MachineSession` owns it and
  configures it itself (every 5th poll, 50 000 records, no layer-height hint), so there
  is no arming, no ownership contest and no way for two components on one machine to
  produce two logs.
- A reconnect shorter than 60 s does not split a run (the gap shows as a long `dt_s`
  and `speed_observed_mms` is left empty). Past 60 s the run is closed with
  `finish_reason = ConnectionLost` and a later reconnect starts a new `job_run`.
- The in-memory buffer holds 50 000 records and then drops the oldest; the streaming
  CSV keeps the earlier rows.
- **The CSV is complete only while no rows have been dropped.** If the sink cannot keep
  up with the poll loop its 20 000-entry queue fills and rows are discarded; the
  retention status line then names the count (`N row(s) dropped`). A non-zero count means
  gaps in the file, so check the Logs page before using a run for analysis.
