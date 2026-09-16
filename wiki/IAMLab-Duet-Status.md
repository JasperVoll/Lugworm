# IAMLab Duet Status

IAMLab Duet Status shows the current state of the connected machine:
the nozzle position, whether the axes are homed, the file being
printed, the progress, and the last reply from the firmware. The
component refreshes on every poll of the connection.

Panel: `7 Print`. Nickname: `Status`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Duet Status with panels on status, pos, and progress. A short screen recording saved as GIF can replace the image. -->
![IAMLab Duet Status on the canvas](images/comp-duet-status.png)

## Inputs

| Name | Type | Description |
|---|---|---|
| `host` | Text | Host string from the Duet Connect component |
| `enable` | Boolean | Live-refresh this component on every poll |

## Outputs

| Name | Type | Description |
|---|---|---|
| `status` | Text | Machine status |
| `homed` | Boolean | All visible axes homed |
| `pos` | Point | Nozzle position (XYZ, mm) in RHINO coordinates - the machine's own frame is mirrored about y = -x, so a Duet reading of X200 Y-500 appears here as X500 Y-200 |
| `jobFile` | Text | Printing file (empty when idle) |
| `progress` | Number | Job progress 0..1 |
| `reply` | Text | Last firmware reply |
| `log` | Text (list) | Session log (newest last) |
| `connected` | Boolean | Session is live |
| `info` | Text | Diagnostics |

## Notes

- `pos` is reported in Rhino coordinates. For the SCARA arm of the lab,
  the machine coordinates are mirrored; the description of the output
  gives an example.

Back to [[Components]].
