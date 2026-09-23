---
title: "Print Stats"
nav_order: 17
parent: "Components"
description: "Print Stats: component inputs and outputs."
permalink: /docs/components/print-stats/
redirect_from:
  - /docs/components/iamlab-print-stats/
---

# Print Stats

Print Stats reads a G-code file, or a toolpath, and reports the
printed length, the travel length, the amount of material in cubic
centimetres and grams, an estimated print time, and a table with one
row per layer. The report is a quick check before a long print.

Panel: `5 Analyze`. Nickname: `Stats`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-print-stats" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `gcode` | Text (list) |  | G-code as one string or a list of lines. Feeds per move are honored |
| `toolpath` | Curve (tree) |  | Toolpath curve tree in Rhino document units, used only when gcode is empty |
| `width` | Number | 10 | Layer width in mm for geometric volume estimates |
| `height` | Number | 0 | Layer height, mm (0 = auto from the Z steps) |
| `density` | Number | 2 | Material density, g/cm³ (clay ≈ 2.0) |
| `ramDiameter` | Number | 94 | Ram cartridge bore in mm for volume from G-code E travel (0 = estimate from layer width and height) |

### Optional group: Feed override inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `printFeed` | Number | 25 | Default print feed where the source has none, mm/s |
| `travelFeed` | Number | 60 | Default travel speed in mm/s where the source has no feedrate |
| `accel` | Number | 0 | XY acceleration in mm/s^2 for estimating acceleration and corner delays (0 = commanded speeds only) |

### Optional group: Robot Definition input

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `robot` | Text | Optional Robot Definition. With a kinematic profile the estimate also honors the robot's joint speed limits, not the Cartesian feed alone |

## Outputs

| Name | Type | Description |
|---|---|---|
| `report` | Text | Readable statistics report. Connect a panel |
| `timeMin` | Number | Estimated duration in min, using joint speed limits when available, otherwise acceleration or commanded speeds |
| `volumeCm3` | Number | Extruded volume, cm³ |
| `massG` | Number | Extruded mass, g |
| `extrudeLen` | Number | Total nozzle path length while extruding, mm |
| `travelLen` | Number | Travel length, mm |
| `layers` | Integer | Layer count |
| `perLayer` | Number (tree) | One branch per layer: Z in mm, extrusion length in mm, time in min |

Back to [Components]({{ '/docs/components/' | relative_url }}).
