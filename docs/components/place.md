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

{% include figure.html src="components/iamlab-place" alt="Place: choosing a seam mode and turning the seam angle, with the seams drawn in the viewport" %}

## Inputs

| Name | Type | Description |
|---|---|---|
| `contours` | Curve (tree) | Layer contour tree to place |
| `printCenter` | Point | Where this part's bottom center lands, world XYZ. A placement, not a bed setting. When connected it overrides the bed's default print center |
| `zOffset` | Number | Extra Z after dropping, mm |
| `move` | Vector | Extra translation always applied to the part, mm |
| `seamMode` | Integer | 0 = keep, 1 = align (default), 2 = stagger, 3 = nearest, 4 = guide, 5 = track |

### Optional group: Print Bed definition input

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `bed` | Text | Print Bed definition. Supplies the default print center |

### Optional group: Seam direction / target inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `seamAngle` | Number | Direction for mode 1, or the step per layer for mode 2, deg |
| `seamPoint` | Point | Target point for mode 3 (nearest). Optional first layer target for mode 5 (track) |
| `seamCurve` | Curve | Guide curve for mode 4. Each layer's seam lands at the closest point of this world space curve, so draw it where the placed part stands, for example a helix or a vertical line up the back |

## Outputs

| Name | Type | Description |
|---|---|---|
| `moved` | Curve (tree) | Placed and reseamed contour tree |
| `seams` | Point (tree) | Seam start point per contour |
| `info` | Text | Diagnostics |

Back to [Components]({{ '/docs/components/' | relative_url }}).
