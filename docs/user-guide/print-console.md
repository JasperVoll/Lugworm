# Duet Console

`Duet Console` (7 Print, nick `Console`, since 3.0.0 — called *Machine Console* from 4.0.0 to
4.4.0) is the machine's control surface. One external
window does connection, homing, jogging, origin capture, job control, the terminal, go-to and
progress, while you keep working in the Grasshopper file: the canvas re-solves only when an
exposed output changed, never while `live` is off, so nothing flashes during a print. The arm
simulation stays in the Rhino viewport (Live View / Robot Simulation).

**Press the `Console` capsule on the component to open the window.** Press it again to bring an
open window to the front.

**Which machine it talks to comes from the wired `robot` profile**, not from a switch. The
profile must advertise Duet control (*potterbot-scara-v4*, *generic-duet-cartesian*, or an
unwired input); a profile that does not - *generic-klipper*, *generic-marlin* - is refused
with "use a Duet profile". 4.0.0's Moonraker transport was removed in 4.1.0.

> **Coming from 2.6.0?** *Duet Connect*, *Status*, *Jog*, *Terminal* and *Print* are now hidden
> from the ribbon. They are **not** removed — old files keep opening and working, and they stay
> Duet-only — but new work should use the console. See [UPGRADE.md](../../UPGRADE.md) for the two
> behaviours that stayed behind on the old components.

## Inputs

| Input | Group | Meaning |
|---|---|---|
| `gcode` | core | the job as a list of lines (G-code Generator `gcode`) |
| `host` | core | hostname or IP of the Duet board, `host[:port]` |
| `password` | core | empty = the firmware default `reprap`. Stored as plaintext in the `.gh` and sent as plaintext over the LAN - use a trusted network |
| `pollMs` | core | status poll interval, 250–4000 ms. The session clamps to 250–2000, and a focused window polls at 250 ms while the machine is active |
| `robot` | core | optional robot definition; unwired = Potterbot SCARA. **Must advertise Duet control**: *generic-klipper* and *generic-marlin* are dialect-only and are refused with "use a Duet profile" |
| `points` | core | optional calibration points in Rhino coordinates, for the go-to selector |
| `live` | core | default false; true re-solves the outputs on machine changes, at most once per second |
| `jobName` | job | default `print`; uploads to `0:/gcodes/<name>.gcode` |
| `dryRun` | job | Start uses `M37` (firmware simulation, zero motion) instead of `M32` |
| `arcSupport` | job | firmware arc support (G2/G3) |
| `blockWarnings` | job | refuse upload on **any** warning; mast clearance always blocks |
| `L1 L2 reachMax reachMin zMax xMin xMax yMin yMax checkEnvelope elbow` | limits | machine limit overrides, same names and defaults as before. Wired values feed **both** jog planning and job validation |
| `feed` | jog | jog feedrate in mm/s, capped at 50 |

## Outputs

Indices 0–7 are unchanged from 2.6.0, so existing wires keep working:

`status` (connection plus the pending or last action), `state` (raw machine status), `progress`
(0..1), `pos` (nozzle in Rhino coordinates), `homed` (all axes), `jobFile`, `log` (session log,
newest last), `info` (diagnostics built from state only, never a poll age).

New in 3.0.0, appended after them (indices 8–19):

`host` (wire this into Machine Files / Live View, exactly as you wired
Duet Connect), `connected`, `reply` (last firmware reply), `xHomed` / `yHomed` / `zHomed`,
`bedOrigin` and `zZero` (the captures, in Rhino coordinates and document units), `uploaded`
(remote path of the last verified upload), and the validation verdict: `danger`, `problems`
(violating points) and `messages`.

4.0.0 appended five heater/fan outputs (indices 20–24: `hotend`, `hotendTarget`, `bed`,
`bedTarget`, `fan`) after `speedFactor`/`flowFactor`. **4.1.0 removed those five again** together
with thermoplastic support - this is the console's only non-append output change, so re-check
its output wires once after loading a 4.0.0 document. What remains at indices 20–22:

| # | Output | Meaning |
|---|---|---|
| 20 | `speedFactor` | speed override, percent (`M220`, -1 unavailable) |
| 21 | `flowFactor` | flow override, percent (`M221`, -1 unavailable) |
| 22 | `gcode` | G-code lines of the running or about-to-start job (appended since 2026-09-09) |

Every wire on outputs 0–19 survives the upgrade untouched.

## The window

**Header** — connection lamp and host, board type, machine state, whether the arm configuration is
known; per-axis homed lamps, job name, progress and layer; and the position in the **machine**
frame, in mm. (The `pos` output is in Rhino coordinates instead.) On the right, a **Log:** button
shows the current job log's status (Temporary / Recording / Syncing / Waiting to sync / Saved /
Logging fault) and opens the Logs tab.

**Control tab** — jog step (0.1 / 1 / 10 / 50 mm), the jog pad, Home all and Hm X/Y/Z, Set bed
origin, Set Z=0, and the go-to point list. **The jog pad is press-and-hold**: hold a direction and
it keeps stepping, one step at a time, each one waiting for the machine to acknowledge the last.
Release to stop.

**Job tab** — Validate, Upload, Start / Start (dry M37), then **Go to print start** on its own
row above Pause, Resume and Cancel: the validated job's first extruding move at Z 0, so you can
see where the first layer will land before the job runs. The caption beside it names that point in
Rhino and in machine coordinates. Below: the upload record and the validation verdict.

**Terminal tab** — one G-code line per Send. Diagnostics (`M115`, `M122`, `M114`, …) always work.
Motion, configuration and job commands need a **second press to confirm** and invalidate the
tracked arm configuration, because a raw line never went through the kinematic model. There is no
"expert" switch any more: opening this tab is the expert act.

**The window has five tabs, not six.** 4.0.0 added a Thermals tab (heater table, presets,
temperature chart, fan/extrude/retract controls, speed/flow/pressure-advance tuning) and a
Dashboard heater strip with a "Heat up" next-action. **4.1.0 removed all of it** together with
thermoplastic support and the direct Klipper connection; see
`docs/2-changelog/w10_v4.1.0.md`. No gate changed for the tabs that remain.

**Dashboard** - the single next-action button names the next job action (Validate, Upload,
Start, …) and its gate; it checks state only and sends nothing itself.

**Logs tab** — every job is recorded to a local buffer first, whether or not it is kept. Keep all
job logs keeps every job from now on; Save this job keeps just the one selected below. A kept job
is copied to the folder Change location picks (local or network); nothing chosen yet means kept
jobs have nowhere to go until you pick one. Retry resumes a copy that could not reach the
destination, and Export copy saves the selected job's CSV directly. Below that: this session's job
list, the selected job's record (state, started, staged/synced bytes, CSV and destination paths,
last error), and the live session event log with a warnings filter and Clear live view (which
empties the view only).

**Footer** — the last result, the active renderer, and two **native** buttons: **Reset (M999)** and
the red **E-STOP**. Those two are deliberately not drawn by the window's renderer, so they keep
working even if it fails.

**A disabled control tells you why, in text next to it** — not just in a tooltip. The reason is
the same one the on-canvas components show.

## Two rules that are stricter than the old components

- **Reset (M999) needs the machine halted or off.** The Jog component's armed any-state override
  was not carried over. If the board is wedged but not halted, press **E-STOP** first — that halts
  it — then Reset. Typing `M999` in the Terminal tab obeys the same rule.
- **Jogging while paused still takes two presses.** The first press is refused and arms a 5 s
  maintenance window; press again to proceed. Holding the button does **not** count as the second
  press — that has to be a deliberate one.

## What each action checks (the same gates, inside the same command transaction)

A fresh status snapshot is fetched under the command lock, the machine identity is re-checked, the
gate runs, and only then is anything sent. A Grasshopper solve never sends anything.

- **Connect / Disconnect** — Disconnect ends the session for *every* component holding it, not just
  this one. Closing the window does not disconnect.
- **Upload** — full parse and robot checks for the robot definition's mechanism (`JobGate`), then
  firmware safety (arm configuration known and unchanged since validation, `M669`/`M208` verified),
  the motion-limit scan and a CRC-verified upload. An empty `gcode` is refused.
- **Start** — an upload record whose G-code hash matches, firmware safety, idle and homed, remote
  content and size verified, a fresh re-gate, then `M32` (or `M37` for a dry run).
- **Home** — verified homing (observed Busy to Idle, reply check, per-axis homed flags; a full
  `G28` establishes the arm configuration). Refused only when the connect-time macro probe
  **finished** and found no macro for that axis; an unreadable macro never blocks homing, because
  homing is how you recover from an unknown state. Hm X/Y/Z asks first: the other axes stay
  unhomed and the arm configuration unknown until a full Home.
- **Jog / go-to** — the jog planner: whitelisted step, capped feed, envelope and reach checks, and
  a known arm configuration on the SCARA. Refused on a stale status. Go-to is SCARA-only and moves
  in one straight line without a lift, so jog Z up first.
- **Go to print start** — the same planner and gates as go-to, with the target taken from the
  wired job itself: the start of its first extruding move, exactly the machine-frame point the
  validation checked (no frame conversion happens on the way out), at Z 0. It needs a validation
  that did not refuse the job and a job that extrudes at all - the button says which is missing.
  The move is two planned legs, across at the current height and then straight down onto the
  bed, so jog Z clear of anything on the bed first.
- **Pause, Resume, Cancel** — the job-state gate.
- **E-STOP** — sends `M112` without confirmation whenever a session exists, bypassing the queue.

`Reset` is `M999`, halted or off only.

## Lifecycle

One window per component; closing it hides it. With `live` off and no action pending, hiding
releases this component's hold on the session; the connection ends only when no other 7 Print
component holds it, and a running print continues on the machine. With `live` on the subscription
stays. Deleting the component or closing the document closes the window and releases the hold.

Sessions are keyed by host, since the console speaks to Duet boards only.

The jog step, the captures, the upload record and the machine identity you have trusted are saved
in the `.gh`. The connection itself is never saved — reopening a definition never touches the
machine — and the first Start after reopening re-verifies the remote file before trusting it.

## Renderer

The window is drawn with Skia. **CPU rendering is the default and always available.** The
component's right-click menu offers *Render the console window with OpenGL*, which takes effect the
next time the window opens and falls back to CPU by itself if this machine cannot paint it. The
footer names whichever is actually running. If Skia cannot start at all, the window says so and
E-STOP and Reset still work — use the canvas components for the rest.
