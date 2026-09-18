---
title: "IAMLab Slice"
nav_order: 23
parent: "Components"
description: "IAMLab Slice: component inputs, outputs, and usage notes."
permalink: /docs/components/iamlab-slice/
---

# IAMLab Slice

IAMLab Slice cuts a shape into layers. The input can be a mesh, a Brep,
or a surface, or a list of them. The result is a tree of closed curves
with one branch per layer; each branch holds the wall curves first and
the infill after them. Two slicing modes are available: flat layers
(planar) and layers shaped like cones (conical). The component has a
compute button and runs only when the button is clicked.

Panel: `1 Slice`. Nickname: `Slice`. Compute button: yes (runs only when the button is clicked).

{% include figure.html src="components/iamlab-slice.png" alt="IAMLab Slice on the canvas" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `geo` | Geometry (list) |  | Meshes, Breps or surfaces to slice, one item or a list. Each object is sliced and printed in turn |
| `layerHeight` | Number | 3 | Layer spacing, mm |
| `firstLayer` | Number | 3 | Height of the first section, mm (0 = half the layer spacing) |
| `wallCount` | Integer | 1 | Number of perimeters, at least 1 |
| `spacing` | Number | 10 | Curve spacing, mm. Fallback for wallDistance and infillDistance when they are unset |
| `wallDistance` | Number | 10 | Distance between walls and from the last wall to infill, mm (defaults to spacing) |
| `infillDistance` | Number | 10 | Distance between infill passes, mm (defaults to spacing) |
| `mode` | Integer |  | 0 = planar, 1 = conical |
| `infill` | Integer |  | 0 = none, 1 = zigzag, 2 = concentric, 3 = spiral, 4 = fermat, 5 = sine wave, 6 = graph wave |
| `cornerRadius` | Number | 0 | Round sharp contour corners to this radius to correct overfill, mm (0 = off) |
| `tol` | Number | 0.001 | Slicing and linearization tolerance, mm |

### Optional group: Adaptive layer height inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `hMin` | Number | 1 | Adaptive minimum layer height, mm |
| `hMax` | Number | 3 | Adaptive maximum layer height, mm |

### Optional group: Conical (non-planar) inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `coneAngle` | Number | 20 | Conical half angle for mode 1, deg. On a surface of revolution the outer rings stay horizontal by construction. The cone shows in the layer spacing, the inner walls and the infill |
| `nozzleClear` | Number | 25 | Maximum printable surface angle, deg. Collision guard |

### Optional group: Zigzag angle input

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `angle` | Number | Zigzag direction, deg |

### Optional group: Wave infill inputs (infill 5 / 6)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `profile` | Text |  | Serialized curve from the map output of IAMLab Graph Mapper, the shape one wave period follows. Required by infill 6. Not connected falls back to a sine |
| `waveLen` | Number | 20 | Wave period along a row, mm |
| `waveTangent` | Boolean |  | Mirrored peaks meet exactly, with the amplitude at half of infillDistance. Off leaves a gap of waveClearance |
| `waveClearance` | Number | 0 | Gap left between mirrored peaks when waveTangent is off, mm (0 = 0.2 x infillDistance) |
| `waveRes` | Number | 0.5 | Sampling step along a wave row, mm |
| `waveRepeat` | Boolean |  | Play the graph profile forward every period. Off (the default) mirrors every other period, so any curve tiles continuously even when its ends differ |

### Optional group: Bricklayer

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `brickShift` | Number | 0.5 | Wall Z shift as a fraction of the local layer gap: the nominal height, or the actual gap to the layer above when adaptive is on (0.5 = running bond) |
| `brickOverlap` | Number | 0.5 | Fraction of the shorter loop that must run alongside its neighbour before the two interlock |
| `brickOuter` | Boolean |  | Shift the outermost wall instead of leaving it flat. This changes the visible surface |
| `brickRamp` | Integer | 1 | Layers left flat at the bottom and the top |

### Optional group: Non-planar infill

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `npAmp` | Number | 0.4 | Infill Z wave amplitude, mm (0 = off). Refused above 0.4 x the layer gap. With adaptive on that is the actual gap to each neighbor, so the limit can be tighter than 0.4 x layerHeight |
| `npWave` | Number | 20 | Infill Z wave period, mm |
| `npDir` | Number | 0 | Direction the Z wave travels, deg (default = the zigzag angle) |
| `npRes` | Number | 1 | Infill resampling step before modulation, mm |
| `npPhase` | Number | 0.5 | Phase advance per layer, turns (0.5 = next layer's trough over this layer's crest) |
| `npRamp` | Integer | 1 | Layers over which the amplitude ramps in and out |
| `npWallAmp` | Number | 0 | Wall Z wave amplitude, mm (0 = off) |
| `npLoopPhase` | Number | 0.25 | Phase advance per wall loop, turns |
| `npWallOuter` | Boolean |  | Modulate the outermost wall. This changes the visible surface |

## Outputs

| Name | Type | Description |
|---|---|---|
| `contours` | Curve (tree) | One branch per layer, walls then infill. With several objects the branch path is object, then layer |
| `layers` | Integer | Number of layers over all objects |
| `info` | Text | Diagnostics and warnings |

## Notes

- `layerHeight` and `spacing` must be repeated on IAMLab G-code
  Generator (`layerHeight`, `width`), because the G-code Generator does
  not read them from the curves.
- `mode`: `0` planar is the normal choice. `1` conical is the
  non-planar mode; see [Non-Planar Slicing]({{ '/docs/workflows/non-planar-slicing/' | relative_url }}) before using it, and
  enable the `varh` group on the G-code Generator afterwards. `mode = 2` slices planar
  and warns.
- `infill`: `4` (fermat) and `3` (spiral) produce one continuous path
  per layer; `1` (zigzag) and `2` (concentric) produce many separate
  lines or rings. Concentric, spiral and fermat start at the seam of the
  innermost wall, so the hop from wall to infill is one `spacing`. A
  layer with several islands falls back to separate rings.
- `infill` `5` and `6` (wave) lay the same parallel rows as zigzag, but
  each row follows a wave and neighbouring rows are mirrored, so one
  row's peaks face the next row's troughs. `waveTangent` makes them
  touch exactly; otherwise they stop `waveClearance` short. `5` uses a
  sine, `6` uses whatever curve you draw in [IAMLab Graph Mapper]({{ '/docs/components/iamlab-graph-mapper/' | relative_url }}) -
  wire its `map` output into `profile`. With `waveRepeat` off the curve
  is mirrored at every period end, so any shape tiles smoothly even if
  its two ends do not match.
- **Bricklayer** (group) shifts adjacent wall loops by half a layer
  height so the beads bond in a running bond instead of stacking into
  one continuous seam. Two loops interlock only when they are within one
  bead width **and** run alongside each other for more than
  `brickOverlap` of the shorter loop, so a ring that merely brushes past
  another is left alone. The outermost wall stays flat unless
  `brickOuter` is set, and `brickRamp` layers stay flat at the bottom
  and the top so the print sits flat on the bed and ends flat.
  **Enable the G-code Generator's `varh` group with it**: a shifted loop
  sits 1.5 layer heights above the one below on the ramp-in layer and
  0.5 on the ramp-out layer, and `varh` is what makes the flow follow
  that gap. The component says so whenever it actually shifts anything.
- **Non-planar infill** (group) waves the infill up and down in Z so
  successive layers mesh into each other. `npPhase` `0.5` puts the next
  layer's trough over this layer's crest. The amplitude ramps to zero
  over `npRamp` layers at the bottom and the top, and an amplitude above
  0.4 x the layer height is refused rather than quietly clamped -
  beyond that two consecutive layers would intersect. Walls are never
  modulated, only infill.
- Neither the bricklayer nor the non-planar pass has been printed yet.
  Treat the first run of either as an untested machine trial.
- Adaptive layer height (group): in planar mode the slicer chooses the largest
  gap compatible with the surface inclinations inside that gap. A distant dome
  crown therefore does not force the lower layers to use the same thin spacing.
  It chooses a smaller height near shallow or overhanging surfaces and a
  larger height on steep surfaces, bounded by `hMin` and `hMax`. Small features
  are retained rather than averaged away; horizontal base/top caps are ignored,
  while internal horizontal ledges remain active and force `hMin`. Each input object is analysed
  independently, but its layers share horizontal heights. `info` reports the
  realised range and first/last gaps between sections. A dome should show
  decreasing gaps toward its crown; the final full gap need not equal `hMin`
  exactly. This heuristic does not prove support or bridge stability;
  inspect the toolpath and run Overhang for those decisions.
- How to use it: enable the Adaptive group, choose `hMin` and `hMax` in mm,
  click Compute, and read the realised range in `info`. Then enable the
  Generator's `varh` group. If the nominal generator height is `H`, set
  `varhMin <= hMin/H` and `varhMax >= hMax/H`; the first section uses the
  existing `firstLayer` rule (when `firstLayer = 0`, the first section is
  half `hMin` above the geometry base). The Generator uses its nominal
  `layerHeight` for first-layer extrusion, so match it to that first-layer
  thickness. These settings control geometric spacing and local flow;
  they do not establish a printable or physically supported part.
- `cornerRadius` rounds sharp corners slightly so that the nozzle does
  not over-fill them.
- The Spiralize group prints the whole part as one continuous helix
  (vase mode). Every layer must then consist of exactly one closed
  curve; `wallCount` and `infill` are ignored and `info` says so.
- Brep and surface inputs are meshed internally with a tolerance-driven
  setting. Prepare the mesh upstream if you need control over it.
- With a list of shapes, the shapes are printed one after the other;
  the Multi-object group sets the order.

Back to [Components]({{ '/docs/components/' | relative_url }}).
