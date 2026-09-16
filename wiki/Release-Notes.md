# Release Notes

This page summarises each release for users. The detailed changelogs
are in the repository under `docs/2-changelog/`.

## v4.1.0, LDM only, interlocking walls and wave infill (September 2026)

**Removed.** The plug-in is a clay / LDM slicer again.

- **Thermoplastic (FDM) printing is gone**: the G-code Generator's `fdm`
  group and all thirteen of its inputs, the `filamentMm` output, the
  temperatures, the part-cooling fan and the cooldown footer. A saved
  document that used them shows one "unknown feature group" warning and
  emits ram G-code. Keep a 4.0.0 install if you need filament output.
- **The console's Thermals page is gone**, with the heater strip, the
  presets, the heat/fan/extrude/tune controls, and the five outputs
  `hotend`, `hotendTarget`, `bed`, `bedTarget` and `fan`. `speedFactor`
  and `flowFactor` stay - they are M220/M221 overrides, not
  temperatures. This is the console's only non-append output change, so
  re-check its output wires once after loading a 4.0.0 document.
- **Klipper printers can no longer be controlled over the network.** The
  Moonraker transport was never connected to a real printer and the lab
  prints LDM only, so it could not be verified and served no machine.
  **The Klipper G-code dialect is untouched**: generate the file and
  transfer it yourself. `generic-klipper` remains as a dialect profile,
  `generic-marlin` replaces `generic-marlin-fdm`, and
  `generic-klipper-fdm` is removed.

**Added.** Three new Slice feature groups, all off by default, so an
existing document slices exactly as it did before.

- **Bricklayer**: adjacent wall loops are shifted by half a layer height
  so the beads bond in a running bond instead of stacking into one
  continuous delamination seam. Two loops interlock only when they are
  within one bead width **and** run alongside each other for more than
  half the shorter loop. The visible outer wall stays flat by default,
  and the first and last layers stay flat so the print sits flat on the
  bed. Enable the G-code Generator's `varh` group with it.
- **Wave infill** (`infill` 5 and 6): parallel rows that follow a wave
  instead of running straight, mirrored row to row so one row's peaks
  face the next row's troughs - nearly touching, or exactly touching
  with the tangency option. Mode 5 uses a sine; mode 6 follows any curve
  you draw in **IAMLab Graph Mapper**.
- **Non-planar infill**: wave-modulates the infill height so successive
  layers mesh into each other, ramped flat at the first and last layers.
- **IAMLab Graph Mapper** gains a fourth output, `map` (appended), which
  carries the curve itself so the slicer can evaluate it at whatever
  resolution it needs.

**Not yet printed.** The three new groups are covered by unit tests
only. Nothing has been drawn in a Rhino viewport or run on the printer;
treat the first run as a bring-up.

## v4.0.0, thermoplastic printing and Klipper (September 2026)

> **Superseded.** The thermoplastic and Klipper-control parts of this
> release were removed again in 4.1.0 (above).

- **UR retired**: IAMLab UR Program and the hidden IAMLab UR Robot are
  removed, along with the UR robot profiles and Robot Simulation's
  three UR-only inputs. Every other shipped component, GUID, and gate
  is unchanged.
- **IAMLab Machine Console** (renamed from Duet Console) now controls
  a Klipper printer through Moonraker's HTTP API as well as a Duet
  board: the same `host` and `password` inputs, with `password` holding
  the Moonraker API key on a Klipper profile. A new Thermals tab lists
  heaters and fans with presets (PLA, PETG, ABS, Off), a temperature
  chart, manual extrude/retract (Klipper only), and speed/flow/
  pressure-advance tuning; seven outputs report hotend, bed, fan, and
  override values. Klipper accepts only Cartesian kinematics and has no
  dry-run simulation. Machine Files and Live View (renamed from Duet
  Files and Duet Live) work with either machine kind. Not yet verified
  on a Klipper printer.
- **Thermoplastic G-code**: the G-code Generator gains a filament
  extrusion model (temperatures, retraction in filament mm, part
  cooling) alongside the existing ram/pump model for paste, a Klipper
  G-code dialect, and new profiles `generic-klipper`,
  `generic-klipper-fdm`, and `generic-marlin-fdm`. Marlin stays a
  G-code dialect only, with no direct control.
- **IAMLab Legend**: a configurable legend for any data range -
  continuous, classes, groups, or size - reading a wire from Overhang
  or Interlayer Time or your own values, with named or custom colour
  schemes and viewport, plane, or floating placement.
- **IAMLab Load Settings and settings files**: every IAMLab component
  can save and load its inputs as a small JSON file; Load Settings
  points a document at a folder of them so the `⇤ inputs` button builds
  from the matching file instead of the built-in defaults.
- **Graph Mapper**: Spline points gain tangent handles - hover to
  reveal them, drag to keep a point smooth, Shift for one side only,
  Ctrl to snap the slope, double-click to reset.
- **Interlayer Time**: the `layer` input now scrolls the preview and
  cuts the emitted data to the visible layers, with alternate-layer
  shading to tell adjacent layers apart; the in-viewport legend is
  replaced by a `legend` wire for IAMLab Legend.

## v3.0.0, one console (September 2026)

- IAMLab Duet Console: one window replaces the five Duet control
  components (which still open in old files). It opens on a dashboard
  that says whether the printer is connected, ready and printing and
  proposes the next step; Control, Job, Terminal and Logs pages follow.
  Every disabled button shows its reason; E-STOP and Reset are always
  in the footer.
- Press-and-hold jogging with one move in flight at a time; a
  stricter Reset (only while halted); Validate works offline.
- Job logs: every job is recorded automatically; keep one with *Save
  this job* or all of them with *Keep all job logs* and a folder,
  including a network folder that may disappear and come back.
- Duet Live animates the machine at up to 60 fps in the viewport
  without re-solving the canvas, with an optional colouring of the
  trail by deviation from the intended path.
- IAMLab Pareto Select: pick a recipe on the trade-off front by clicking;
  the choice stays with the document and is flagged when the front changes.
- DoE: the plan workbook records the power estimate on its own sheet;
  the Desirability report no longer overstates what the intervals show.
- Calib Points and Scara Fit work in inch and metre documents.

## v2.6.0, release readiness (September 2026)

The first public release. Changes on top of 2.0 to 2.5:

- Document units: documents in millimetres, inches, and metres slice
  correctly. Inputs documented in mm stay in millimetres, and G-code is
  always written in millimetres.
- Locale independence: G-code and reports always use decimal points.
  CSV inputs are detected per file as English or German format and
  refused when the formats are mixed.
- Responsive canvas: all file reads and writes run in the background,
  so a slow network share no longer freezes Grasshopper.
- IAMLab Version component: shows the installed version and offers an
  update check that is off by default.
- Machine identity: Duet control remembers which board answered on the
  first connection and blocks actions when a different board answers
  at the same address, until the operator accepts the new board with
  "Trust connected machine". Two saved definitions that remember
  different boards for the same address are refused rather than
  resolved by the last one loaded.

## v2.0 to v2.5, one robot selection for everything (August 2026)

- 2.0: the robot became a selection on one wire. Robot Definition and
  a single Robot Simulation replace the separate per-machine
  components. Use without a robot is fully supported.
- 2.1: Print Bed definition, print-time estimates that respect the
  joint speeds of the robot, agreement between preview and output
  height, and separate colours for travel and printed material.
- 2.2: direct control of ordinary printers with a Duet board
  (`generic-duet-cartesian` profile): jog, print, and live view, checked
  against the travel limits stored in the firmware.
- 2.3: calibration additions (uncertainty of predictions, diagnostics of
  the fit, best trade-offs between goals, a check whether a plan has
  enough runs), one consistent plot style, and a draggable goal editor
  (prototype).
- 2.4 and 2.5: IAMLab Graph Mapper with draggable custom curves and
  additional curve types.

## v1.x, foundation (July and August 2026)

Planar, conical, and heat-method slicing; G-code generation for paste
extrusion; SCARA simulation; Duet WiFi control with safety
checks; print path log; workspace limits and verified homing; the DoE
calibration workflow.