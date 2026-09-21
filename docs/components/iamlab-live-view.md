---
title: "IAMLab Live View"
nav_order: 9
parent: "Components"
description: "IAMLab Live View: component inputs and outputs."
permalink: /docs/components/iamlab-live-view/
---

# IAMLab Live View

IAMLab Live View shows the machine while it prints: the bed plate, the
arm at the real nozzle position, and the observed nozzle path drawn
behind it. The component only reads from the machine and never sends
commands.

It does not record the print to a file. Every job is logged by
[IAMLab Machine Console]({{ '/docs/components/iamlab-machine-console/' | relative_url }}),
which records on its own whether or not this component is on the canvas.

Panel: `7 Print`. Nickname: `Live`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-live-view.png" alt="IAMLab Live View on the canvas" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `host` | Text |  | Host output from Machine Console or Duet Connect, connected before live preview |
| `enable` | Boolean | true | Read the connected session and animate its live position when true (automatic output updates are a separate menu option) |
| `gcode` | Text (list) |  | Fallback G-code text or lines for the intended path, used only when the shared session has no job. G-code words are machine coordinates and the path is drawn in the Rhino frame |
| `bed` | Text |  | Print Bed definition text for the plate preview (unwired = no plate) |

### Optional group: Kinematics overrides (L1/L2, reach, elbow)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `L1` | Number | 549.06 | First SCARA link length in mm |
| `L2` | Number | 457.23 | Second SCARA link length in mm |
| `baseHeight` | Number | 0 | Base Z height, mm |
| `reachMax` | Number | 0 | Maximum SCARA reach in mm (0 = L1 + L2) |
| `reachMin` | Number | 235 | Minimum SCARA reach in mm (0 = the absolute difference between L1 and L2) |
| `elbow` | Integer |  | Fallback SCARA arm mode for drawing: 0 = up, 1 = down, used only without a tracked session configuration |

### Optional group: Robot definition (generic Duet machines)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `robot` | Text | Robot Definition text for the live preview (unwired = Potterbot SCARA, Cartesian profiles show the nozzle without an arm) |

## Outputs

| Name | Type | Description |
|---|---|---|
| `pos` | Point | Reported nozzle position in Rhino document units, mirrored for Potterbot and unchanged for Cartesian profiles. A Duet reading of X200 Y-500 appears here as X500 Y-200 |
| `J1` | Number | First SCARA joint angle calculated from reported position, deg (unavailable for Cartesian profiles) |
| `J3` | Number | SCARA elbow angle calculated from reported position, deg (unavailable for Cartesian profiles) |
| `J2` | Number | SCARA Z carriage height calculated from reported position, mm (unavailable for Cartesian profiles) |
| `status` | Text | Raw machine status |
| `progress` | Number | Job progress, 0 to 1 |
| `info` | Text | Connection, intended-path and live-display diagnostics |
| `trail` | Geometry (tree) | Observed path in Rhino document units, one branch per continuous run as a polyline (a run of one observation is a point), with gaps preserved. A run ends where the continuity policy breaks the segment. Never smoothed, never joined across a break, never projected onto the intended path |

## Compatibility with development files

A document saved with an earlier internal development build may still carry
these sockets. Opening it reports them once and drops them.

| Removed | Replaced by |
|---|---|
| `bedWidth`, `bedDepth`, `bedCenter` | the `bed` input and [IAMLab Print Bed]({{ '/docs/components/iamlab-print-bed/' | relative_url }}). Saved values migrate into a real Print Bed component |
| `extWidth`, `layerHeight` | the extrusion preview on [IAMLab G-code Generator]({{ '/docs/components/iamlab-g-code-generator/' | relative_url }}) and [IAMLab Robot Simulation]({{ '/docs/components/iamlab-robot-simulation/' | relative_url }}) |
| `logEnable`, `logEveryN`, `logFolder`, `logPrefix`, `logLayerHeight`, `logExport` | the Machine Console's always-on job logging |
| outputs `layers`, `log`, `path`, `logInfo` | the Machine Console's job logs and Recent prints |

Grasshopper drops wires to removed outputs without asking, and `trail` now sits at output
index 7, so an older document is asked to delete those four wires by hand.

Back to [Components]({{ '/docs/components/' | relative_url }}).
