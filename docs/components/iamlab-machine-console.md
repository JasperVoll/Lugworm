---
title: "IAMLab Machine Console"
nav_order: 11
parent: "Components"
description: "IAMLab Machine Console: component inputs, outputs, and usage notes."
permalink: /docs/components/iamlab-machine-console/
---

# IAMLab Machine Console

IAMLab Machine Console is the one window for controlling a printer with
a Duet board from Grasshopper. It handles connecting, homing, jogging,
capturing the bed origin and the nozzle height, checking and uploading a
job, starting, pausing and cancelling it, sending single commands,
driving to calibration points, and keeping the job logs. The connected
`robot` profile must advertise Duet control; a profile that does not is
refused with "use a Duet profile".

Panel: `7 Print`. Nickname: `Console`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-machine-console.png" alt="IAMLab Machine Console on the canvas" %}

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

## Outputs

| Name | Type | Description |
|---|---|---|

## Notes

- `host` is `hostname[:port]`. `password` is the Duet password.
- The window has five pages: Dashboard, Control, Job, Terminal and Logs.
- `dryRun` runs a firmware simulation (M37) before the real print.
- `speedFactor` and `flowFactor` report the machine's M220/M221
  overrides; `-1` means the machine did not report that value. Check the component's output wires before use.
- Without a Robot Definition, the console assumes the SCARA arm of the
  lab. With a Cartesian Duet definition, it controls that printer
  instead and checks moves against its own travel limits.
- Right-click the component for "Trust connected machine", a raw
  object-model dump, and the console's renderer and motion-reduction
  settings.
- Every job is recorded to a local buffer first. Kept jobs (Keep all job
  logs, or Save this job) are copied to the folder chosen in the Logs
  tab's Change location picker. The header's **Log:** button shows the
  current job log's status and opens the Logs tab.

Back to [Components]({{ '/docs/components/' | relative_url }}).
