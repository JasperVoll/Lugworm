---
title: "Quick Start"
nav_order: 2
parent: "Getting started"
description: "Quick Start: instructions and practical checks for IAMLab Slicer."
permalink: /docs/getting-started/quick-start/
---

# Quick Start

Preview a first slice without a printer. Heavy components cache results;
click their lower-right Compute button after changing inputs. Planar layers
are horizontal; for conical layers, see [Non-Planar Slicing]({{ '/docs/workflows/non-planar-slicing/' | relative_url }}) and inspect
results carefully.

## 1. Wire the pipeline

1. Add IAMLab Test Geometry (`6 Calibrate`), right-click to choose Cylinder,
   and set `height = 80`, `radius = 40`.
2. Add IAMLab Slice (`1 Slice`), IAMLab Place (`2 Place`), and
   IAMLab G-code Generator (`3 G-code`).
3. Wire `geometry` → `geo`, `contours` → `contours`, and `moved` → `crvs`.
   Contours are Slice's layer tree; Place produces the toolpaths for Generator.
4. Add IAMLab Robot Definition (`4 Robot`), choose preset `none`, and wire
   `robot` to Generator's `robot`. An unwired Generator uses the lab SCARA
   frame. `generic-cartesian` checks a Cartesian frame; the Potterbot profile
   enables SCARA kinematics.
5. Attach Panels to `info`. Do not flatten the contour tree: branches identify layers.

{% include figure.html src="getting-started/minimal-pipeline.png" alt="The minimal pipeline" %}

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

Geometry is converted from document units; numeric mm inputs and G-code stay
in millimetres. Empty `geo`, zero `spacing`, or an uncomputed heavy component
produce diagnostics instead of paths.

## 3. Compute in order

Click Slice's Compute button, wait for the layer count, then compute Generator.
Place updates between them. Repeat both after geometry/slicing changes;
recompute only Generator after changing its settings.

Expect 26 planar branches at Z = 3, 6, ... 78 mm. Point counts depend on meshing
and tolerance. These settings are an offline exercise, not a material recipe.
See [IAMLab Slice]({{ '/docs/components/iamlab-slice/' | relative_url }}) for adaptive spacing.

{% include figure.html src="getting-started/compute-button.png" alt="The compute button on a Heavy component" %}

## 4. Check before printing

1. Read every `info` report for per-object problems.
2. Inspect `preview` (extrusion) and `travels` (non-extruding moves).
3. Use IAMLab Print Stats (`5 Analyze`) to check time, volume and mass.
4. For a robot arm, validate in IAMLab Robot Simulation: `reachOK` must be true,
   `problems` empty, and no red error shown. See [Robots and Machine Control]({{ '/docs/workflows/robots-and-machine-control/' | relative_url }}).
5. Watch the first layer in person.

After review, connect Generator `gcode` to IAMLab Save G-code, choose `folder`,
and click the Button wired to `write`. Save and Simulation each read Generator
output independently.

{% include figure.html src="getting-started/preview-and-travel.png" alt="Preview and travel moves in the viewport" %}

## Next steps

- [Components]({{ '/docs/components/' | relative_url }}): inputs and outputs
- [Non-Planar Slicing]({{ '/docs/workflows/non-planar-slicing/' | relative_url }}): conical mode
- [Calibration and DoE]({{ '/docs/workflows/calibration-and-doe/' | relative_url }}): material settings
