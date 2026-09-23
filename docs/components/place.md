---
title: "Place"
nav_order: 15
parent: "Components"
description: "Place: component inputs and outputs."
permalink: /docs/components/place/
redirect_from:
  - /docs/components/iamlab-place/
---

# Place

Place moves the sliced layers to the print position on the bed
and decides where each layer starts and ends (the seam). The bottom
centre of the part lands on `printCenter`. The seam can be kept as it
is, aligned in one direction, rotated a little on every layer, placed
nearest to a point, guided along a curve, or kept above the seam of the
layer below.

Panel: `2 Place`. Nickname: `Place`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-place" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `contours` | Curve (tree) |  | Layer contour tree from Slice, or equivalent curves in Rhino document units |
| `printCenter` | Point |  | Part bottom-center position in Rhino document units, overriding the Print Bed default when wired |
| `zOffset` | Number | 0 | Additional vertical shift after placement, mm |
| `move` | Vector |  | Additional XYZ translation vector in mm after placement |
| `seamMode` | Integer |  | 0 = keep, 1 = align (default), 2 = stagger, 3 = nearest, 4 = guide, 5 = track |
| `bed` | Text |  | Print Bed definition text supplying the default printCenter |

### Optional group: Seam direction / target inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `seamAngle` | Number | 0 | Direction for mode 1, or the step per layer for mode 2, deg |
| `seamPoint` | Point |  | Target in Rhino document units for nearest seams, or the first layer in track mode |
| `seamCurve` | Curve |  | Guide curve at the placed part in Rhino document units, required for seamMode 4 |

## Outputs

| Name | Type | Description |
|---|---|---|
| `moved` | Curve (tree) | Placed contour tree in Rhino document units, preserving the input branch structure |
| `seams` | Point (tree) | Start point per contour in Rhino document units |
| `info` | Text | Placement and seam settings or warnings |

Back to [Components]({{ '/docs/components/' | relative_url }}).
