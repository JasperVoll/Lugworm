---
title: "IAMLab Live View"
nav_order: 9
parent: "Components"
description: "IAMLab Live View: component inputs and outputs."
permalink: /docs/components/iamlab-live-view/
---

# IAMLab Live View

IAMLab Live View shows the machine while it prints: the bed plate, the
arm at the real nozzle position, and the printed bead growing as the
job progresses. The component
only reads from the machine and never sends commands. Optionally, the
nozzle path is recorded with timestamps to a CSV file.

Panel: `7 Print`. Nickname: `Live`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-live-view.png" alt="IAMLab Live View on the canvas" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `host` | Text | Host string from the Machine Console host output |
| `enable` | Boolean | Refresh this component on every poll |
| `gcode` | Text (list) | Fallback intended path, used only while the Machine Console has no job. The console's own job wins whenever it has one. G-code words are machine coordinates and the path is drawn in the Rhino frame |
| `bedWidth` | Number | 1000 | Bed plate width along RHINO X, mm |
| `bedDepth` | Number | 700 | Bed plate depth along RHINO Y, mm |
| `bedCenter` | Point |  | Bed plate center point in RHINO coordinates - the plate, grid and printable area follow it |
| `extWidth` | Number | Extrusion width, mm |
| `layerHeight` | Number | Layer height = extrusion height, mm |

### Optional group: Print Bed definition input

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `bed` | Text | Print Bed definition, the only source of plate size, shape and position. Not connected draws no plate |

### Optional group: Arm geometry overrides (L1/L2, reach, elbow)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `L1` | Number | 549.06 | First link length, mm |
| `L2` | Number | 457.23 | Second link length, mm |
| `baseHeight` | Number | 0 | Base Z height, mm |
| `reachMax` | Number | 0 | Maximum reach, mm (0 = L1 + L2) |
| `reachMin` | Number | 235 | Minimum reach, the dead zone radius, mm (0 = L1 minus L2) |
| `elbow` | Integer |  | Fallback arm mode, 0 up or 1 down, used to draw the arm only while the session has no tracked configuration |

### Optional group: Print path log (timestamps, speed, layers)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `logEnable` | Boolean | Arm the timestamped print path recorder |
| `logEveryN` | Integer | Interval samples per layer |
| `logFolder` | Text | Folder for the streaming print log CSV |
| `logPrefix` | Text | Print log filename prefix |
| `logLayerHeight` | Number | Layer height hint for the Z step fallback |
| `logExport` | Boolean | Export the in-memory print log to a new CSV |

### Optional group: Robot definition (generic Duet machines)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `robot` | Text | Optional Robot Definition. Not connected means the Potterbot SCARA with a posed arm and mirrored frame. A generic Duet Cartesian definition shows position, trail and bed in the machine's own frame, without an arm |

## Outputs

| Name | Type | Description |
|---|---|---|
| `pos` | Point | Live nozzle position in Rhino coordinates. The machine's own frame is mirrored about y = -x, so a Duet reading of X200 Y-500 appears here as X500 Y-200 |
| `J1` | Number | Live first joint angle, deg. The machine's own value, unaffected by the frame map |
| `J3` | Number | Live third joint angle, deg |
| `J2` | Number | Live Z carriage height, mm |
| `status` | Text | Raw machine status |
| `progress` | Number | Job progress, 0 to 1 |
| `info` | Text | Diagnostics: session, intended path, stale state and the display's measured counters |
| `trail` | Geometry (tree) | Observed toolpath in Rhino coordinates, one branch per continuous run as a polyline (a run of one observation is a point). A run ends where the continuity policy breaks the segment, and where a straight line would claim a movement the elapsed time cannot support. Never smoothed, never joined across a break, never projected onto the intended path |
| `layers` | Text (list) | Detected print-layer spans, one readable line each (layer, start -> end, duration, mean speed, samples); the CSV keeps the full record |
| `log` | Text (list) | Print-log CSV header and rows |
| `path` | Point (list) | Recorded print-log positions, in RHINO coordinates (the streaming CSV keeps the machine's own numbers) |
| `logInfo` | Text | Print-log diagnostics |

Back to [Components]({{ '/docs/components/' | relative_url }}).
