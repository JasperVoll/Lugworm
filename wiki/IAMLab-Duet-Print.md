# IAMLab Duet Print

IAMLab Duet Print checks a G-code job against the limits of the
machine, uploads the job to the Duet board with a checksum, and starts,
pauses, resumes, or cancels the job from buttons on the component. A
job that violates the limits is not uploaded, and a job whose upload is
out of date is not started.

Panel: `7 Print`. Nickname: `Print`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Duet Print with the Upload and Start buttons and a panel on the messages output. A short screen recording saved as GIF can replace the image. -->
![IAMLab Duet Print on the canvas](images/comp-duet-print.png)

## Inputs

| Name | Type | Description |
|---|---|---|
| `gcode` | Text (list) | G-code job (item string or list of lines) |
| `host` | Text | Host string from the Duet Connect component |
| `jobName` | Text | Remote file name (stored as 0:/gcodes/<name>.gcode, re-upload overwrites) |
| `dryRun` | Boolean | Start with M37 firmware simulation instead of M32 (no motion, time estimate) |

### Optional group: Machine limit overrides (L1/L2, reach, Z, elbow, arcs)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `L1` | Number | 549.06 | First link length, mm |
| `L2` | Number | 457.23 | Second link length, mm |
| `reachMax` | Number | 0 | Max reach, mm (0 -> L1+L2) |
| `reachMin` | Number | 235 | Min reach / dead zone, mm (0 -> \|L1-L2\|) |
| `zMax` | Number | 400 | Build height, mm (firmware M208 Z maximum) |
| `xMin` | Number | -300 | Lowest allowed X, mm (firmware M208 X minimum) |
| `xMax` | Number | 10000 | Highest allowed X, mm (firmware M208 X maximum) |
| `yMin` | Number | -10000 | Lowest allowed Y, mm (firmware M208 Y minimum) |
| `yMax` | Number | 0 | Highest allowed Y, mm (firmware M208 Y maximum) |
| `checkEnvelope` | Boolean | true | Check moves against the travel limits stored in the firmware (M208) |
| `elbow` | Integer |  | Fallback arm mode (0 up / 1 down) for non-home-anchored paths; a home-anchored job or connected session uses the machine's tracked homing configuration |
| `arcSupport` | Boolean | true | Firmware arc support (G2/G3) |
| `blockWarnings` | Boolean |  | Refuse upload on ANY warning (overspeed, jerk, singularity) - mast clearance always blocks |

### Optional group: Robot definition (generic Duet machines)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `robot` | Text | Optional Robot Definition. Not connected = Potterbot SCARA (full check set). generic-duet-cartesian checks the job against the travel limits stored in the firmware only; limit inputs that are not connected are read from the firmware |

## Outputs

| Name | Type | Description |
|---|---|---|
| `danger` | Boolean | True when the job violates machine limits (upload refused) |
| `problems` | Point (list) | Locations of limit violations / warnings |
| `messages` | Text (list) | Validation messages |
| `uploaded` | Text | Remote path of the last verified upload |
| `state` | Text | Machine status |
| `progress` | Number | Job progress 0..1 |
| `info` | Text | Diagnostics |

## Notes

- `dryRun` starts a firmware simulation of the job instead of the real
  print, which gives a time estimate without moving the machine.
- `danger` is true when the job was refused. `problems` and `messages`
  show where and why.
- `blockWarnings` refuses the upload on any warning, not only on hard
  limit violations.
- Without a Robot Definition, the component checks the job for the
  SCARA arm of the lab. With a `generic-duet-cartesian` definition, the
  job is checked against the travel limits stored in the firmware.

Back to [[Components]].
