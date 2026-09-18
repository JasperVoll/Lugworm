---
title: "IAMLab Print Stats"
nav_order: 17
parent: "Components"
description: "IAMLab Print Stats: component inputs and outputs."
permalink: /docs/components/iamlab-print-stats/
---

# IAMLab Print Stats

IAMLab Print Stats reads a G-code file, or a toolpath, and reports the
printed length, the travel length, the amount of material in cubic
centimetres and grams, an estimated print time, and a table with one
row per layer. The report is a quick check before a long print.

Panel: `5 Analyze`. Nickname: `Stats`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-print-stats.png" alt="IAMLab Print Stats on the canvas" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `gcode` | Text (list) |  | G-code as one string or a list of lines. Feeds per move are honored |
| `toolpath` | Curve (tree) |  | Placed curves, an alternative to gcode. Default feeds apply |
| `width` | Number | 10 | Extrusion width, mm, for the volume |
| `height` | Number | 0 | Layer height, mm (0 = auto from the Z steps) |
| `density` | Number | 2 | Material density, g/cm³ (clay ≈ 2.0) |
| `ramDiameter` | Number | 94 | Ram bore, mm. With G-code input the volume comes exactly from the E values through this ram area (0 = geometric width x height fallback) |

### Optional group: Feed override inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `printFeed` | Number | 25 | Default print feed where the source has none, mm/s |
| `travelFeed` | Number | 60 | Travel feed, mm/s |
| `accel` | Number | 0 | XY acceleration, mm/s² (0 = off). Adds a trapezoidal ramp model with junction speed slowdown at corners. The timeMin output then reports the acceleration aware estimate (Duet default M204 ≈ 500) |

### Optional group: Robot Definition input

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `robot` | Text | Optional Robot Definition. With a kinematic profile the estimate also honors the robot's joint speed limits, not the Cartesian feed alone |

## Outputs

| Name | Type | Description |
|---|---|---|
| `report` | Text | Readable statistics report. Connect a panel |
| `timeMin` | Number | Time estimate, min, from the most constrained available model: joint speed limited when a kinematic Robot Definition is connected, else the trapezoidal acceleration estimate when accel is above 0, else the kinematic lower bound |
| `volumeCm3` | Number | Extruded volume, cm³ |
| `massG` | Number | Extruded mass, g |
| `extrudeLen` | Number | Extrusion length, mm |
| `travelLen` | Number | Travel length, mm |
| `layers` | Integer | Layer count |
| `perLayer` | Number (tree) | Data per layer, one branch each: z, length mm, time min |

Back to [Components]({{ '/docs/components/' | relative_url }}).
