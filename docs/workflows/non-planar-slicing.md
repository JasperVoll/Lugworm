---
title: "Non-Planar Slicing"
nav_order: 2
parent: "Workflows"
description: "Set up conical or adaptive layers and match extrusion to their spacing."
permalink: /docs/workflows/non-planar-slicing/
---

# Non-Planar Slicing

Use conical layers to change how beads stack, or adaptive planar layers to vary their vertical spacing. Both workflows begin with [IAMLab Slice]({{ '/docs/components/iamlab-slice/' | relative_url }}) and continue through Place and G-code Generator.

| Layer type | Slice setting | What changes |
|---|---|---|
| Planar | `mode = 0` | Flat layers at a constant height. |
| Adaptive planar | `mode = 0`, with Adaptive enabled | Flat layers with spacing chosen between `hMin` and `hMax`. |
| Conical | `mode = 1` | Cone-shaped layers printed with a vertical, non-tilting nozzle. |

`mode = 2` falls back to planar slicing and reports a warning.

## Set up conical layers

1. Set `mode` to `1` and enable the conical input group on Slice.
2. Set `coneAngle`. A typical starting range is 15° to 25°; the appropriate angle depends on the geometry and material.
3. Compute the slice and read `info`. If the `nozzleClear` guard reports a clearance warning, reduce `coneAngle` and inspect the path. Raising `nozzleClear` can hide a collision risk.
4. Pass the contours through Place, then configure variable-height flow in Generator as described below.

Conical layers can improve bead alignment, but they do not establish that a part is supported or that the nozzle will clear it.

{% include figure.html src="workflows/planar-vs-conical.png" alt="Planar and conical slicing of the same part" %}

## Set up adaptive planar layers

In planar mode, enable Adaptive and set `hMin` and `hMax`. Slice chooses the largest gap within those bounds allowed by the face inclinations in that gap. Shallow or overhanging regions receive thinner layers; steep regions can receive thicker ones.

Each object is calculated independently and shares horizontal layer heights within that object. `firstLayer` still applies. Global horizontal caps are ignored, while internal horizontal ledges force `hMin`. Small features can therefore change the spacing of a whole layer.

Adaptive spacing is a geometric heuristic. It does not validate printability or implement a validated published method.

## Match extrusion to the layer gaps

After adaptive or conical slicing, enable Generator's **Variable layer-height flow** (`varh`) group. The actual vertical gaps can differ from the nominal height; without this adjustment, extrusion remains approximate.

For adaptive layers:

- Set `varhMin` no higher than `hMin / nominalHeight`.
- Set `varhMax` no lower than `hMax / nominalHeight`.
- Use the first-layer/base extrusion height as `nominalHeight` when calculating those ratios, and match Generator's nominal layer-height setting to it.

Recompute Generator after changing these settings and inspect the resulting toolpath.

## Review support and clearance

Connect the contour tree from Slice or Place to [IAMLab Overhang]({{ '/docs/components/iamlab-overhang/' | relative_url }}). It shows bead overhang per segment using a continuous magma scale or safe/warning/critical classes. Calibrate the placeholder thresholds with a stepped-cone print in your material.

Inspect near-horizontal surfaces, bridges, and nozzle clearance, then run a controlled test print. Flow scaling and overhang colours help you assess a path; they cannot guarantee support, bridge stability, or a successful print.

{% include figure.html src="workflows/overhang-nonplanar.png" alt="Overhang analysis on a non-planar part" %}
