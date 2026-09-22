---
title: "Machine Console"
nav_order: 11
parent: "Components"
description: "Machine Console: component inputs and outputs."
permalink: /docs/components/machine-console/
redirect_from:
  - /docs/components/iamlab-machine-console/
---

# Machine Console

Machine Console is the one window for controlling a printer with
a Duet board from Grasshopper. It handles connecting, homing, jogging,
capturing the bed origin and the nozzle height, checking and uploading a
job, starting, pausing and cancelling it, sending single commands,
driving to calibration points, and keeping the job logs. The connected
`robot` profile must advertise Duet control; a profile that does not is
refused with "use a Duet profile".

Panel: `7 Print`. Nickname: `Console`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-machine-console" alt="Machine Console: opening the console window from the canvas and switching between its Control and Logs pages" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `gcode` | Text (list) |  | G-code job as one string or a list of lines |
| `host` | Text | 192.168.1.42 | Machine hostname or IP address with an optional :port, for example 192.168.1.42 |
| `password` | Text |  | Duet password (empty = the firmware default 'reprap'). The password is stored as plain text in the .gh file and sent as plain HTTP on the LAN, so use a trusted network. Intentional spaces are kept. |
| `pollMs` | Integer | 1000 | Status poll interval, ms, 250 to 4000. The session clamps it to 250 to 2000, and a focused window polls at 250 ms while the machine is active |
| `robot` | Text |  | Optional Robot Definition. Not connected means the Potterbot SCARA with the full check set and the go-to point. Wire a generic Duet Cartesian definition for other Duet machines, which get travel limits stored in the firmware (M208) checks and no go-to move |
| `points` | Point (list) |  | Optional calibration points in Rhino coordinates, listed in the console's go-to selector |
| `live` | Boolean | false | Solve the outputs again on machine changes, at most once per poll. When false the outputs refresh on the next ordinary solve only |

### Optional group: Job inputs (name, dry run, arcs, warnings)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `jobName` | Text | print | Remote file name, stored as 0:/gcodes/<name>.gcode. Uploading again overwrites it |
| `dryRun` | Boolean |  | Start with the M37 firmware simulation instead of M32: no motion, only a time estimate |
| `arcSupport` | Boolean | true | Firmware arc support (G2/G3) |
| `blockWarnings` | Boolean |  | Refuse the upload on any warning (overspeed, jerk, singularity). Mast clearance always blocks |

### Optional group: Machine limit overrides (L1/L2, reach, Z, envelope, elbow)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `L1` | Number | 549.06 | First link length, mm |
| `L2` | Number | 457.23 | Second link length, mm |
| `reachMax` | Number | 0 | Maximum reach, mm (0 = L1 + L2) |
| `reachMin` | Number | 235 | Minimum reach, the dead zone radius, mm (0 = L1 minus L2) |
| `zMax` | Number | 400 | Build height, mm (firmware M208 Z maximum) |
| `xMin` | Number | -300 | Lowest allowed X, mm (firmware M208 X minimum) |
| `xMax` | Number | 10000 | Highest allowed X, mm (firmware M208 X maximum) |
| `yMin` | Number | -10000 | Lowest allowed Y, mm (firmware M208 Y minimum) |
| `yMax` | Number | 0 | Highest allowed Y, mm (firmware M208 Y maximum) |
| `checkEnvelope` | Boolean | true | Check moves against the travel limits stored in the firmware (M208) |
| `elbow` | Integer |  | Fallback arm mode, 0 up or 1 down, for planning before a verified home exists. A verified home's endstop mode takes precedence |

### Optional group: Jog feedrate

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `feed` | Number | 20 | Jog feedrate, mm/s (capped at 50) |

## Outputs

The outputs are grouped into six categories. A new component shows only the three pinned
ones, `host`, `status` and `gcode`; right-click and use **Outputs shown** to switch a
category on. Switching a category on or off moves the socket indices of the ones below it,
so wire by name. A category that carries a wire is never hidden, by hand or on load, and an
older document keeps every category that had a wire when it was saved.

### Connection

| Name | Type | Description |
|---|---|---|
| `host` | Text | Session host to wire into the other Print components. Shown by default |
| `connected` | Boolean | True while connected, even if machine identity checks prevent control |

### Job

| Name | Type | Description |
|---|---|---|
| `status` | Text | Console status: the connection, then the pending action or last result. Shown by default |
| `state` | Text | Machine status |
| `progress` | Number | Job progress, 0 to 1 |
| `jobFile` | Text | Printing file (empty when idle) |
| `uploaded` | Text | Remote path of the last verified upload (empty when none) |
| `gcode` | Text (list) | Running or prepared G-code lines, including Recent prints selections (empty for jobs started outside this session). Shown by default |

### Motion

| Name | Type | Description |
|---|---|---|
| `pos` | Point | Reported nozzle position in Rhino document units, mirrored for Potterbot and unchanged for Cartesian profiles |
| `homed` | Boolean | True when all visible axes report homed |
| `xHomed` | Boolean | X axis homed |
| `yHomed` | Boolean | Y axis homed |
| `zHomed` | Boolean | Z axis homed |
| `bedOrigin` | Point | Captured plate origin in Rhino document units for Print Bed's center input, without changing firmware offsets |
| `zZero` | Number | Legacy captured Z in Rhino document units from saved documents, with no new captures |

### Validation

| Name | Type | Description |
|---|---|---|
| `danger` | Boolean | True when validation refuses the job |
| `problems` | Point (list) | Validation warning and error positions in Rhino document units |
| `messages` | Text (list) | Validation messages |

### Overrides

| Name | Type | Description |
|---|---|---|
| `speedFactor` | Number | Speed override, percent (-1 = not reported) |
| `flowFactor` | Number | Flow override, percent (-1 = not reported) |

`-1` is not `0`: a machine that does not publish a factor and a factor genuinely set to
zero must not read the same.

### Log and diagnostics

| Name | Type | Description |
|---|---|---|
| `log` | Text (list) | Session log, newest last |
| `info` | Text | Connection, machine identity, validation and last-action details |
| `reply` | Text | Last firmware reply |

Back to [Components]({{ '/docs/components/' | relative_url }}).
