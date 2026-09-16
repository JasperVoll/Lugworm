# IAMLab Duet Jog

IAMLab Duet Jog moves the machine in fixed steps from buttons on the
component, homes the axes, captures the bed origin and the nozzle
height, and provides an emergency stop. Every target is checked
against the limits of the machine before the command is sent.

Panel: `7 Print`. Nickname: `Jog`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Duet Jog with the jog buttons visible and the live position shown in a panel. A short screen recording saved as GIF can replace the image. -->
![IAMLab Duet Jog on the canvas](images/comp-duet-jog.png)

## Inputs

| Name | Type | Description |
|---|---|---|
| `host` | Text | Host string from the Duet Connect component |

### Optional group: Machine limit overrides (L1/L2, reach, Z, elbow)

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
| `elbow` | Integer |  | Fallback arm mode (0 up / 1 down) for planning before a tracked home configuration exists; a verified home's endstop-determined mode takes precedence |

### Optional group: Robot definition (generic Duet machines)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `robot` | Text | Optional Robot Definition. Not connected = Potterbot SCARA (full check set, mirrored frame). generic-duet-cartesian checks moves against the travel limits stored in the firmware only; limit inputs that are not connected are read from the firmware |

## Outputs

| Name | Type | Description |
|---|---|---|
| `pos` | Point | Nozzle position (XYZ, mm) in RHINO coordinates - the machine's own frame is mirrored about y = -x |
| `homed` | Boolean | All axes homed |
| `xHomed` | Boolean | X axis homed |
| `yHomed` | Boolean | Y axis homed |
| `zHomed` | Boolean | Z axis homed |
| `bedOrigin` | Point | Captured bed origin in RHINO coordinates (connect to Robot bedCenter / Mover) |
| `zZero` | Number | Captured Z zero (nozzle-on-bed machine Z) |
| `reply` | Text | Last firmware reply |
| `info` | Text | Diagnostics |

## Notes

- The step sizes and the maximum speed are fixed so that a wrong click
  cannot produce a large or fast move.
- Without a Robot Definition, the component assumes the SCARA arm of
  the lab. With a `generic-duet-cartesian` definition, moves are checked
  against the travel limits stored in the firmware, and the Limits
  group is only needed to override them.
- `bedOrigin` and `zZero` are captured positions that can be connected
  to other components, for example as the bed centre.

Back to [[Components]].
