---
title: "Slice"
nav_order: 23
parent: "Components"
description: "Slice: component inputs and outputs."
permalink: /docs/components/slice/
redirect_from:
  - /docs/components/iamlab-slice/
---

# Slice

Slice cuts a shape into layers. The input can be a mesh, a Brep,
or a surface, or a list of them. The result is a tree of closed curves
with one branch per layer; each branch holds the wall curves first and
the infill after them. Two slicing modes are available: flat layers
(planar) and layers shaped like cones (conical). The component has a
compute button and runs only when the button is clicked.

Panel: `1 Slice`. Nickname: `Slice`. Compute button: yes (runs only when the button is clicked).

{% include figure.html src="components/iamlab-slice" alt="Slice with its standard inputs and the wave infill group" %}

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
| `profile` | Text |  | Serialized curve from the map output of Graph Mapper, the shape one wave period follows. Required by infill 6. Not connected falls back to a sine |
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

Back to [Components]({{ '/docs/components/' | relative_url }}).
