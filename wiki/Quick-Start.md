# Quick Start

This page walks through a first slice with five components. No printer
is required. The beginner guide in the repository (`docs/user-guide/`)
covers the same steps in more detail.

## A few terms that make the canvas easier to read

- **Planar** layers are horizontal. **Conical** layers follow the configured
  cone mapping; use [[Non Planar Slicing]] and inspect the result carefully.
- A **heavy** component caches its expensive computation. Click its lower-right
  compute button after changing an input.
- **Contours** are the ordered curve tree from Slice. **Toolpaths** are the
  placed curves that G-code Generator turns into machine moves.
- A **Robot Definition** selects a machine profile. `none` keeps the workflow
  model-free; `generic-cartesian` checks a Cartesian frame; the Potterbot
  profile enables SCARA kinematics and its machine frame.

## 1. Wire the pipeline

1. Place an IAMLab Test Geometry component (panel `6 Calibrate`).
   Right-click the component, choose Cylinder, and set `height = 80`
   and `radius = 40`.
2. Place IAMLab Slice (`1 Slice`), IAMLab Place (`2 Place`), and
   IAMLab G-code Generator (`3 G-code`).
3. Connect `geometry` to `geo`, `contours` to `contours`, and `moved`
   to `crvs`.
4. Add IAMLab Robot Definition (`4 Robot`), choose `none` with its preset
   input, and wire its `robot` output to Generator's `robot` input. This makes
   the first exercise independent of the lab SCARA coordinate frame.
5. Attach a Panel to each `info` output. Keep the contour tree intact:
   do not flatten it, because its branches identify the layers.

<!-- IMAGE TODO: canvas screenshot of exactly this five-component setup,
     wires visible, info panels attached. -->
![The minimal pipeline](images/quickstart-pipeline.png)

## 2. Set the essential inputs

| Component | Input | Starting value |
|---|---|---|
| Slice | `layerHeight` | `3.0` mm |
| Slice | `firstLayer` | `3.0` mm |
| Slice | `spacing` | `10.0` mm for this preview exercise |
| Slice | `wallCount` | `1` |
| G-code Generator | `layerHeight` | the same value as on Slice |
| G-code Generator | `width` | start with the same value as Slice `spacing` |
| G-code Generator | `ramDiameter` | leave the default for this preview; before a ram print, enter the measured barrel bore in mm |
| G-code Generator | `robot` | the explicitly wired `none` definition for this exercise; select the actual machine profile before printing |
| G-code Generator | `savePath` | leave empty for preview; optionally supply a complete `.gcode` file path to save on Compute |

The Rhino document may use millimetres, inches, or metres. Geometry is
converted at the boundary, numeric inputs documented in mm stay in
millimetres, and the G-code is always written in millimetres.

For a first dry run, explicitly place Robot Definition, choose `none`, and
wire its `robot` output to G-code Generator (an unwired generator preserves
the historical Potterbot SCARA frame). Use the same `layerHeight` and use
Slice `spacing` as the starting G-code `width`. If `geo` is empty,
`spacing` is zero, or a heavy component has not been computed, the diagnostic
output explains why no paths appear.

## 3. Compute in order

Click Slice's lower-right Compute button, wait for its layer count, then click
G-code Generator's Compute button. Place updates between them. After changing
geometry or slicing settings, repeat both computations in that order; changing
only a Generator setting requires recomputing the Generator.

For this cylinder with planar defaults, expect 26 layer branches at Z = 3,
6, ... 78 mm in the Slice output. The exact number of curve points depends on
meshing and tolerance. The values above are an offline exercise, not a material
recipe. For adaptive spacing, continue with [[IAMLab Slice]].

<!-- IMAGE TODO: close-up of a Heavy component showing the compute button,
     with the cursor on the button. -->
![The compute button on a Heavy component](images/quickstart-compute.png)

## 4. Check the result before printing

1. Read every `info` output. The components report problems per object
   instead of failing silently.
2. Inspect `preview` (extruding moves) and `travels` (non-extruding
   moves) in the viewport.
3. Run IAMLab Print Stats (`5 Analyze`) on the G-code and check that
   time, volume, and mass are plausible.
4. On a robot arm, validate the program in IAMLab Robot Simulation
   first: `reachOK` must be true, `problems` must be empty, and the
   component must show no red error. See
   [[Robots and Machine Control]].
5. Watch the first layer in person.

To save after reviewing, connect Generator `gcode` to IAMLab Save G-code,
choose a `folder`, and click the Button wired to `write`. Save and Simulation
both read Generator output; neither needs to be wired through the other.

<!-- IMAGE TODO: viewport screenshot of preview (blue bead path) and travels,
     with a visible seam column. -->
![Preview and travel moves in the viewport](images/quickstart-preview.png)

## Next steps

- [[Components]]: every component and input
- [[Non Planar Slicing]]: conical mode
- [[Calibration and DoE]]: finding settings for a specific material
