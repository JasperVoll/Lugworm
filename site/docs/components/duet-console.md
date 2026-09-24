---
title: "Duet Console"
nav_order: 4
parent: "Components"
description: "Duet Console: component inputs and outputs."
permalink: /docs/components/duet-console/
redirect_from:
  - /docs/components/machine-console/
  - /docs/components/iamlab-machine-console/
---

# Duet Console

Duet Console is the one window for controlling a printer with
a Duet board from Grasshopper. It handles connecting, homing, jogging,
capturing the bed origin and the nozzle height, checking and uploading a
job, starting, pausing and cancelling it, sending single commands,
driving to calibration points, and keeping the job logs. The connected
`robot` profile must advertise Duet control; a profile that does not is
refused with "use a Duet profile".

Panel: `7 Print`. Nickname: `Console`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-machine-console" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `gcode` | Text (list) |  | G-code job as text or a list of lines, overridden by a job loaded from Recent prints until cleared |
| `host` | Text | 192.168.1.42 | Duet hostname or IP address, optionally with :port, required before connecting in the console |
| `password` | Text |  | Duet password (empty = 'reprap'), saved unencrypted in the .gh file and sent over HTTP |
| `pollMs` | Integer | 1000 | Status poll interval in ms, clamped to 250 to 2000, or 250 while the console is focused and the machine is active |
| `robot` | Text |  | Robot Definition text for the connected Duet machine (unwired = Potterbot SCARA, Cartesian profiles disable go-to) |
| `points` | Point (list) |  | Calibration points in Rhino document units for the SCARA go-to selector, used after connecting and homing |
| `live` | Boolean | false | Refresh changed visible outputs at most once per second when true (false = refresh on the next solve) |

### Optional group: Job inputs (name, dry run, arcs, warnings)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `jobName` | Text | print | Remote file name, stored as 0:/gcodes/&lt;name&gt;.gcode. Uploading again overwrites it |
| `dryRun` | Boolean |  | Use firmware simulation on Start when true, with no movement |
| `arcSupport` | Boolean | true | Allow G2/G3 arcs in job validation when true, or reject jobs containing arcs when false |
| `blockWarnings` | Boolean |  | Block upload and start on validation warnings when true (errors and mast clearance violations always block) |

### Optional group: Machine limit overrides (L1/L2, reach, Z, envelope, elbow)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `L1` | Number | 549.06 | First SCARA link length in mm |
| `L2` | Number | 457.23 | Second SCARA link length in mm |
| `reachMax` | Number | 0 | Maximum SCARA reach in mm (0 = L1 + L2) |
| `reachMin` | Number | 235 | Minimum SCARA reach in mm (0 = the absolute difference between L1 and L2) |
| `zMax` | Number | 400 | Maximum machine Z in mm, corresponding to the firmware M208 limit |
| `xMin` | Number | -300 | Minimum machine X in mm, corresponding to the firmware M208 limit |
| `xMax` | Number | 10000 | Maximum machine X in mm, corresponding to the firmware M208 limit |
| `yMin` | Number | -10000 | Minimum machine Y in mm, corresponding to the firmware M208 limit |
| `yMax` | Number | 0 | Maximum machine Y in mm, corresponding to the firmware M208 limit |
| `checkEnvelope` | Boolean | true | Check targets against the machine's Cartesian axis limits when true |
| `elbow` | Integer | 1 | Fallback arm mode, 0 up or 1 down, for planning before a verified home exists. A verified home's endstop mode takes precedence |

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
