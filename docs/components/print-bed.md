---
title: "Print Bed"
nav_order: 16
parent: "Components"
description: "Print Bed: component inputs and outputs."
permalink: /docs/components/print-bed/
redirect_from:
  - /docs/components/iamlab-print-bed/
---

# Print Bed

Print Bed describes the print bed once: how large the plate is,
where the plate is in the Rhino document, and where a part is placed by
default. The `bed` output is connected to [Place]({{ '/docs/components/place/' | relative_url }}),
[Robot Simulation]({{ '/docs/components/robot-simulation/' | relative_url }}), and [Live View]({{ '/docs/components/live-view/' | relative_url }}), so that all of
them use the same bed.

Panel: `2 Place`. Nickname: `Bed`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-print-bed" alt="Print Bed: the bed outline in the viewport and the component on the canvas" %}

## Inputs

| Name | Type | Description |
|---|---|---|
| `width` | Number | Bed plate width along Rhino X, mm (the full axis for an ellipse) |
| `depth` | Number | Bed plate depth along Rhino Y, mm (the full axis for an ellipse) |
| `center` | Point | Bed plate center in Rhino coordinates. The plate, grid and printable area follow it |
| `printCenter` | Point | Default print center, where a part's bottom center lands. This is not the plate center, so keep them separate |
| `shape` | Integer | Plate shape: 0 = rectangle (default), 1 = ellipse. Equal width and depth make a circle |

## Outputs

| Name | Type | Description |
|---|---|---|
| `bed` | Text | Print Bed definition. Connect it into the bed input of Place, Robot Simulation and Live View |
| `width` | Number | Bed width, mm |
| `depth` | Number | Bed depth, mm |
| `center` | Point | Bed plate center |
| `printCenter` | Point | Default print center |
| `outline` | Curve | Bed plate outline |
| `info` | Text | Bed summary |

Back to [Components]({{ '/docs/components/' | relative_url }}).
