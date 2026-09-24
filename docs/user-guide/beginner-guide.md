# Lugworm: Beginner Guide

This guide is written for a first session with Lugworm. No
previous experience with Grasshopper or clay printing is assumed.
Lugworm is a Grasshopper plugin for slicing and printing clay and
paste materials (liquid deposition modeling, LDM) with a ram or piston
extruder, a robot arm, or both, and includes a calibration workflow.

The reference (`reference.pdf`) explains the reasoning behind each
setting and serves as a lookup once the basics are familiar. The
exercises (`exercises.md`) provide practice. A PDF version of this
guide is built from `beginner-guide.tex`.

---

## 1. What the plugin does

All components are located on one Grasshopper tab, Lugworm, in numbered
panels that follow the order in which the components are used:

```
1 Slice | 2 Place | 3 G-code | 4 Robot | 5 Analyze | 6 Calibrate | 7 Print
```

The core pipeline for a normal print consists of three components:

```
your 3D geometry
      |
      v
+-------------+  contours (one    +-------------+  moved contours   +--------------+
|Slice |- branch/layer) ->|Place |------------------>|Lugworm G-code |--> .gcode file
+-------------+                   +-------------+                   |   Generator  |
 "cut it into                      "put it on                       +--------------+
  layers"                           the bed,                         "write the machine
                                     fix the seam"                    instructions"
```

The pipeline runs without a robot selected, so the plugin can be used
as an ordinary slicer for any Marlin or Duet printer.

When a robot arm does the printing, the machine is selected explicitly
and carried on one wire. Robot Definition (panel 4 Robot) picks
the machine profile: the lab SCARA (Potterbot Scara v4), a generic
Cartesian machine, or `none`. Every robot-aware component reads the
profile from a single `robot` input. Robot Simulation visualizes
and validates the toolpath on the selected machine.

Three further groups of components exist alongside the pipeline:

- Analyze (Overhang, Print Stats) checks a slice or a
  finished G-code file before a print is committed.
- Calibrate (DoE Design, RSM Evaluate, Lugworm
  Desirability, Test Geometry) finds settings for a new material
  with designed experiments rather than trial and error. The workflow
  is an advanced topic and is only summarised here; the reference
  (section 6) and the exercises describe the full procedure.
- Print (the Lugworm Duet components, panel 7 Print) controls a
  Duet-based machine directly over WiFi from Grasshopper: connect, jog,
  upload, print, and monitor, behind fail-closed safety gates. The
  Print group is not needed for slicing and is documented in
  `duet-control.md` in `docs/reference/`.

One utility completes the tab. Graph Mapper (1 Slice) remaps a
number stream through an editable curve, which is useful for flow or
speed gradients.

---

## 2. Before starting

- Rhino 8 with Grasshopper, and Lugworm installed as a Yak
  package (package name `Lugworm`, author `Lugworm`). Components
  are dragged onto the canvas from the Lugworm tab. To check which
  version is installed, open Rhino's PackageManager (the Lugworm
  entry shows it) or the Details tab of the .gha file's properties.
- Slice, G-code Generator, G-code Restart, Lugworm
  Overhang, RSM Evaluate, and Desirability are Heavy
  components. Each
  has a manual compute button instead of recomputing on every change
  (see section 6). This behaviour is intended.
- The Rhino document may use millimetres, inches, or metres. The plugin
  converts geometry at the boundary. Numeric inputs documented in mm
  (`layerHeight`, `width`, `spacing`, and so on) stay in millimetres
  regardless of the document units, and the G-code is always written
  in millimetres.
- Some features depend on the printer or firmware, or on data that has
  not been measured yet, such as the pump calibration table. The plugin
  reports such cases in the `info` outputs rather than guessing. Read
  the `info` output of every component.

---

## 3. A first slice with a few inputs

On Slice:

| Input | Value |
|---|---|
| `geo` | The 3D shape: a mesh, Brep, or surface. A list works as well. |
| `layerHeight` | Start with the default of `3.0` mm. |
| `wallCount` | `1` to start. |
| `spacing` | The default of `10.0` mm (must be greater than 0). |
| compute button | Click once. Slice is a Heavy component. |

On Place, connect `contours`. The default `printCenter` of
(339, -193, 0) is the usual spot on the lab bed, and `seamMode = Align`
is a reasonable start. On another machine, set `printCenter` directly
or connect a Print Bed definition (see section 4).

On G-code Generator:

| Input | Value |
|---|---|
| `crvs` | Connect `moved` from Place. |
| `layerHeight` | The same value as on Slice. |
| `width` | The default of `10.0` mm. |
| `ramDiameter` | The inside diameter of the cartridge or barrel in mm (default `94`). |
| `printSpeed` | The default of `43` mm/s is a reasonable start. |
| `robot` | On a Cartesian printer, connect a Robot Definition with the profile `generic-cartesian` or `none`. Without a definition, the output keeps the legacy coordinate frame of the lab SCARA; see section 4. |
| `savePath` | A folder for the `.gcode` file, or use Save G-code instead (see below). |
| compute button | Click once. The G-code Generator is a Heavy component as well. |

The `gcode` output holds the finished file. The `info` output of every
component summarises what happened in one line. Read section 5 before
sending any file to a machine.

---

## 4. The pipeline in detail

### Slice: turns the shape into layers

| Input | Meaning | Default | When to change |
|---|---|---|---|
| `geo` | The shape or shapes to slice: mesh, Brep, or surface, single or list. | required | Always. |
| `layerHeight` | Layer thickness in mm. | `3.0` | Thicker layers print faster and are more visible. |
| `firstLayer` | Height of the first layer only; `0` means half a normal layer. | `3.0` | Raise for more first-layer squish. |
| `wallCount` | Number of outline passes per layer. | `1` | Raise for a thicker shell. |
| `spacing` | Legacy fallback for both distances below, in mm. | `10.0` | Existing definitions keep their spacing until an override is supplied. |
| `wallDistance` | Distance between walls and from the last wall to the infill, in mm; must be greater than 0. | `spacing` | Match to the intended wall layer pitch. |
| `infillDistance` | Distance between infill passes, in mm; must be greater than 0. | `spacing` | Increase for less infill; decrease for denser infill. |
| `mode` | `0` planar, `1` conical (non-planar). | `0` | Leave at `0` until section 7 has been read; mode 1 is advanced. |
| `infill` | `0` none, `1` zigzag, `2` concentric, `3` spiral, `4` fermat. | `0` | `4` (fermat) gives the smoothest and most continuous fill. |
| `cornerRadius` | Rounds sharp corners slightly to avoid over-fill blobs. | `0.0` | Raise a little if corners look overstuffed. |
| `tol` | Curve simplification tolerance in mm. | `0.05` | Rarely changed. |

Toggle groups can be added when needed: Adaptive (varies the layer
height with the slope), Conical, Zigzag, Bricklayer (interlocks wall
loops by half a layer), Non-planar (waves the infill Z, and since 4.3.0
also the inner wall loops with `npWallAmp`, a quarter-turn phase step
per loop in `npLoopPhase`, and `npWallOuter` to include the visible
wall), Spiralize (advanced, see section 7), Meshing (controls how Breps
are converted to a mesh internally), and Multi-object (`printOrder`, the
sequence when a list of shapes is supplied).

Every closed wall starts where the +X ray from the section's centre
crosses it, and every infill pattern starts next to that point, so a
round part sliced with one wall shows the same spiral, Fermat or zigzag
on every layer instead of a pattern that turns with the mesh's
triangles. When a spiral or Fermat fill has to fall back to separate
rings (islands inside the region), `info` says so once per object.

Outputs: `contours` (a curve tree with one branch per layer, or
`{object;layer}` for a list of shapes), `layers` (the count), and
`info` (diagnostics per object, reported without failing the whole
slice).

### Place: positions the part and sets the seam

| Input | Meaning | Default |
|---|---|---|
| `contours` | From Slice. | required |
| `printCenter` | Target point on the bed. | (339, -193, 0), the usual spot on the lab bed |
| `zOffset` | Extra height after placing. | `0.0` |
| `move` | An additional manual shift vector. | none |
| `seamMode` | `0` Keep, `1` Align, `2` Stagger, `3` Nearest, `4` Guide, `5` Track-below. | `1` (Align) |

The seam group (`seamAngle`, `seamPoint`, `seamCurve`) only matters for
modes 1 to 5. Outputs: `moved`, `seams`, `info`. Whether the placed
part is reachable is checked in one place only, Robot Simulation
(see below), not here.

Open curves (infill, open sections) are oriented before the wall seams
are set, in every mode except Keep: an end that sits far from every wall
(the centre of a spiral) never starts; between two wall-side ends the one
nearer the layer's seam target starts, a near-tie follows the same curve's
start on the layer below, and the first layer falls back to the +X side.
So the infill start sits on the same side of the part on every layer. In
`Nearest`, all closed walls then seam next to the first open curve's
start; a wired `seamPoint` is the target when choosing that start, and
the direct seam target when a layer has no open curve. `info` reports how
many open curves were flipped.

### Print Bed: describes the plate once

Instead of entering the bed size and print centre into several
components, Print Bed (2 Place) describes the plate once (width,
depth, centre, and default print centre) and emits a definition string
for Place, Robot Simulation, and Live View. With a
bed connected, Place uses the print centre of the bed instead of
the built-in default. Explicitly connected inputs still take
precedence.

### G-code Generator: writes the machine instructions

The G-code Generator is the largest component. The inputs fall into
three groups: speed and shape, flow and pressure (the levers specific
to paste), and optional feature groups that are added only when needed.

Speed and shape: `crvs` (from Place), `layerHeight`, `width`,
`ramDiameter` (default `94` mm; a warning appears outside 5 to 200 mm,
as a sanity check rather than a hard limit), `pressureCoeff` (`1.2`,
the overall flow multiplier), `printSpeed` (`43`), `travelSpeed`
(`30`), `retractSpeed` (`1`), `firstLayerSpeedFactor` (`0.5`),
`firmware` (Duet-RRF or Marlin; selects the G-code dialect),
`relativeE` (`true`), `zOffset` (a nozzle-to-bed calibration shim with
a default of `0`; the slicer already applies the layer height, so leave
this at 0 unless the Z zero of the machine needs a correction), `tol`,
and `savePath`.

Machine frame (`robot` input): without a definition, the output keeps
the legacy coordinate frame of the lab SCARA, a mirror about `y = -x`,
so that the saved lab definitions keep printing correctly. On a
Cartesian printer, connect a Robot Definition with the profile
`generic-cartesian`, `generic-duet-cartesian`, or `none` to obtain the
identity frame. The active frame is always reported in `info`
(`frame:`), so the choice is visible.

Flow and pressure (specific to paste): `pressureOffset` (plunger
pull-back, the paste equivalent of retraction), `primeExtra`, `coast`,
`zHop`, `hopSpeed`, `footerZLift`, `dwell`, `leadIn`, `leadInBoost`,
`minLayerTime`, `retraction` (an on/off dropdown), and `seamTol`.

For layers with closed walls and open infill, set `alternateInfill = true`
to print the infill backwards on every second layer, starting above the
previous infill endpoint and returning to the walls. The default is
`false`. Alternation restarts for each object. Retraction and safe travel
settings continue to control the connections.

Optional feature groups, added only when needed:

- advance and retractaccel: firmware acceleration and pressure-advance
  overrides.
- arcs (and arcwelder, which requires `arcs`): smooth curved output
  instead of many straight segments.
- skirt: a purge ring before the print.
- floors: solid base layers.
- headeroverride: replaces the generated header.
- pump: for a screw pump instead of a ram or plunger;
  see section 6.1 of the reference. The pump group only calculates a
  recommended pump setting. The E values of the G-code are not changed.
- varh (variable layer-height flow): enable this group after adaptive
  or conical slicing, because the layer height then varies per point.
- ecomp (slope-compensated flow): an experimental correction for extra
  flow on inclined walls. The group is not calibrated yet; leave the
  group off until an inclined-wall calibration print has been made (see
  section 6).

Outputs: `gcode` (one item per line), `preview` and `travels` (curve
trees), `length` (mm), `volume` (cm³), `time` (min), `info`, `pump`
(a summary when the pump group is on), `start` (the first point), and
`startGcode` (a jog-to-start block).

### G-code Restart and Save G-code

- G-code Restart resumes a print that failed part-way. The
  G-code is cut at a Z height, a Duet percentage, or a Rhino
  document-coordinate XYZ point (for example a Live or Console position output).
  Point wins over percentage, which wins over Z, when more than one is given; the
  remainder is rebuilt. Outputs are `resume` (the restart position) and
  the `printed` and `remaining` curve lists. Wire the same Robot Definition the
  G-code Generator used into `robot`: the point and the previews are converted between
  the document frame and the job's frame the way that definition says (the Potterbot
  mirror, or the identity of a Cartesian machine). Unwired keeps the Potterbot frame.
- Save G-code writes `gcode` to a folder with a timestamped file
  name. The component writes only when the `write` input becomes True.
  Connect a Button rather than a permanent toggle, so that one click
  produces one file. The write runs in the background to keep the
  canvas responsive: the component first reports "writing" and sets
  `written = true` on the following solve once the file is on disk.

### Robot Definition and Robot Simulation

The robot is a selection, not an assumption. Robot Definition
(4 Robot) picks the machine profile and emits a definition string that
every robot-aware component accepts on the `robot` input. The shipped
profiles are `none` (toolpath only), `generic-cartesian`,
`generic-duet-cartesian`, and `potterbot-scara-v4` (the lab SCARA). A
text panel with a short alias such as `"scara"` or `"none"` works as
well.

- Robot Simulation (4 Robot) visualizes the G-code or toolpath
  with the printed layer and the travel moves in different colours.
  When the profile supplies kinematics (SCARA), the component
  poses the robot model and checks reach and joint limits while the
  simulation is scrubbed. An invalid move sets `reachOK` to false,
  lists the positions in `problems`, and raises a red error on the
  component ("targets are outside robot limits"). Never run a program
  that shows that error on a real arm. With the profile `none`, the
  component is a plain toolpath and extrusion viewer.

Definitions that still use the pre-2.0 component Robot continue
to open and solve, but that component is no longer on the ribbon. New
work should use Robot Definition and Robot Simulation.

### Overhang and Print Stats (Analyze)

- Overhang flags layers and segments whose local angle exceeds a
  warning threshold (`warnDeg`, default 25°) or a failure threshold
  (`failDeg`, default 45°), colour-coded in the viewport. Both numbers
  are placeholders and are not calibrated for a specific material; see
  section 6.
- Print Stats reports the total length, the extruded volume and
  mass, and an estimated print time with a per-layer breakdown. The
  report is a useful check before a long print. With a `robot`
  definition connected, the time estimate also accounts for the
  joint-speed limits of the machine (a SCARA near the reach boundary is
  slower than the Cartesian numbers suggest). The robot-limited time is
  reported next to the Cartesian estimate.

### DoE Design, RSM Evaluate, Desirability, Pareto Select, and Test Geometry (Calibrate)

The calibration components find print settings for a new material by
designing a small set of test prints, measuring the results, and
fitting a model. The reference (section 6) describes the full workflow
and the exercises contain a guided first pass. In short: DoE Design
creates a run plan, the runs are printed with Test Geometry coupons and
measured by hand, RSM Evaluate fits a model to the measurements, and
Desirability recommends the best settings.

Measurement tables can be CSV or Excel files. CSV files in English
(`1.5`) or German (`1,5`) number format are detected per file. A file
that mixes both conventions is refused with the offending line named
and is never read as wrong numbers.

---

## 5. Checking the output before printing

1. Read the `info` output of every component that was changed. The
   components report problems per object rather than failing silently.
2. Inspect `preview` (extruding moves) and `travels` (non-extruding
   moves) in the viewport.
3. Run Print Stats on the finished G-code and check `timeMin`,
   `volumeCm3`, and `massG` against the expected values.
4. On a robot arm, run Robot Simulation with the matching Lugworm
   Robot Definition connected. `reachOK` must be true, `problems` must
   be empty, and no red error may be shown before anything runs on the
   machine.
5. Only then save the file (Save G-code or the `savePath` input)
   and send the file to the machine. Watch the first layer in person.

---

## 6. Placeholder values

A few defaults are not calibrated. They exist so that the plugin works
out of the box, not because they are the right numbers for a specific
material:

- `warnDeg` and `failDeg` on Overhang (25° and 45°).
- The pump calibration CSV of the pump group. The shipped file is a
  placeholder; the bucket-test protocol in section 6.1 of the reference
  produces the real table for a material and pump.
- `eCompRefMul` of the ecomp group (slope-compensated flow). Leave the
  group off until an inclined-wall calibration print has been made.

None of these values damage anything when left at the default. They are
not accurate for a specific material until calibrated.

---

## 7. Non-planar modes and robot arms

The conical mode of Slice, and robot simulation, are advanced
topics. The reference covers the failure modes and caveats. Two points
matter from the start:

- Conical mode assumes that the nozzle never tilts: the layers bend,
  the tool stays vertical. `nozzleClear` is the collision guard;
  raising it hides a real risk instead of removing one.
- Robot Simulation raises a red error for moves outside the
  limits of the machine, on the SCARA profile. Never override or
  ignore that error. (The pre-2.0 robot component exposed the same
  check as a `danger` output.)

---

## 8. Further reading

- The exercises (`exercises.md`) build up the components one at a time
  in a Grasshopper-only dry run, ending in a shared print project.
- The reference (`reference.pdf`) serves as a lookup during work and
  explains the reasoning behind each setting and the edge cases that
  this guide does not cover.
