---
title: "Interlayer Time"
nav_order: 8
parent: "Components"
description: "Interlayer Time: component inputs and outputs."
permalink: /docs/components/interlayer-time/
redirect_from:
  - /docs/components/iamlab-interlayer-time/
---

# Interlayer Time

Interlayer Time shows, for every point of the print, how long
ago the material directly underneath it was laid down. This interlayer
time governs adhesion and collapse in clay: too short, and the layer
below has not stiffened enough to carry the next one; too long, and it
has skinned over and the next layer will not bond to it. The clock
comes from the G-code's own feeds, or from the timestamps of a
connected print log when one is wired in. The toolpath, or the printed
layers, are coloured on a Plasma scale between your two thresholds.

Panel: `5 Analyze`. Nickname: `Interlayer`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-interlayer-time" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `gcode` | Text (list) |  | G-code as one string or a list of lines. Optional when a print log is wired, since the log's own samples become the path |
| `gcodePath` | Text |  | Optional G-code file path, read in the background. With a print log its exact bytes anchor the log's file positions to the moves |
| `log` | Text (list) |  | Print log CSV lines from a retained job log. When wired, the log's clock replaces the estimate |
| `logPath` | Text |  | Optional print log CSV file path, printlog_*.csv or a retained job log |
| `layer` | Integer | 0 | Highest visible layer, counted from 1 (0 = all), limiting both preview and output trees |
| `isolate` | Boolean | false | Show only the top visible layer instead of everything up to it |
| `minTime` | Number | 0 | Minimum acceptable interlayer time in s, calibrated for the material (0 = no lower warning) |
| `maxTime` | Number | 120 | Maximum acceptable interlayer time in s, calibrated for the material (0 = no upper warning) |
| `legendMax` | Number | 0 | Color scale maximum, s (0 = auto: 1.5 x maxTime, else the 95th percentile). Fixed per analysis so a color keeps its meaning |

### Optional group: Layer geometry inputs (width, height, radius)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `width` | Number | 0 | Layer width, mm (0 = the file's LINEWIDTH header, else 10) |
| `height` | Number | 0 | Layer height, mm (0 = the file's LAYERHEIGHT header, else the median Z step) |
| `radius` | Number | 0 | Search radius for the layer below, mm (0 = layer width, or 3 x the sample spacing for log only paths) |

### Optional group: Feed override inputs (estimated clock)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `printFeed` | Number | 25 | Print feed where a move has no F word, mm/s |
| `travelFeed` | Number | 50 | Travel feed where a move has no F word, mm/s |
| `accel` | Number | 0 | XY acceleration, mm/s² (0 = commanded feeds only). Adds the trapezoidal ramp model of Print Stats to the estimated clock |

### Optional group: Display inputs (lines/layers, layer shading)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `display` | Integer | 0 | 0 = toolpath lines, 1 = extruded layers as a vertex colored mesh |
| `shade` | Integer | 1 | Tell adjacent layers apart: 0 = none, 1 = alternate layers tinted, 2 = a bottom to top ramp. Preview only, the colors output stays the unshaded scale color |

## Outputs

| Name | Type | Description |
|---|---|---|
| `report` | Text | Readable report of the whole print: clock source, thresholds, totals and a table per layer. Connect a panel |
| `perLayer` | Number (tree) | Data for the visible layers, one branch per layer: layer, Z mm, start s, duration s, min s, mean s, max s, too fast samples, too slow samples |
| `times` | Number (tree) | Interlayer time per path vertex, s. Branches match the path output, cut to the visible layers (-1 = no layer below inside the radius) |
| `path` | Curve (tree) | Extrusion runs as polylines, one branch per layer and run, document units, cut to the visible layers |
| `colors` | Colour (tree) | Plasma color per path vertex, branches cut like the path output, for a custom preview. The unshaded scale color, whatever shade draws |
| `layers` | Integer | Layer count of the whole print, not of the visible cut |
| `info` | Text | Diagnostics |
| `legend` | Text | Legend definition text for Legend, matching this analysis's time scale and warning thresholds |

Back to [Components]({{ '/docs/components/' | relative_url }}).
