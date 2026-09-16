# Robots and Machine Control

The robot is chosen once and passed along one wire. The IAMLab Robot
Definition component produces a short profile text, and every component
that needs to know the machine reads that text from a `robot` input.
Typing `"scara"` or `"none"` into a text panel works as
well.

| Profile | Machine type | Program written | Direct control |
|---|---|---|---|
| `none` | no machine, toolpath only | RepRap G-code | no |
| `generic-cartesian` | printer with X, Y, and Z axes | RepRap G-code | no |
| `generic-duet-cartesian` | printer with X, Y, and Z axes and a Duet board | RepRap G-code | yes (7 Print) |
| `potterbot-scara-v4` | the SCARA arm of the lab | RepRap G-code | yes (7 Print) |
| `generic-klipper` | Cartesian printer running Klipper | RepRap G-code, Klipper dialect | no |
| `generic-marlin` | Cartesian printer running Marlin | RepRap G-code, Marlin dialect | no |

<!-- IMAGE TODO: canvas screenshot with the Robot Definition dropdown open,
     showing the profile list, wired into Robot Simulation; viewport with the
     posed SCARA. -->
![Selecting a robot profile](images/robots-definition.png)

## Which way is X? The coordinate rule

When nothing is connected to the `robot` input of the G-code Generator,
the G-code is written for the SCARA arm of the lab, whose X and Y axes
are mirrored compared with an ordinary printer. This default keeps the
older lab definitions printing correctly. On an ordinary printer,
connect a Robot Definition with `generic-cartesian`,
`generic-duet-cartesian`, or
`none`, and the axes are written the normal way. The `info` output
always shows which convention is in use (`frame:`). A mirrored print is
almost always caused by a missing definition.

## Checking a program in Robot Simulation

Connect the definition and the G-code to IAMLab Robot Simulation.

- Drag the `sim` slider from 0 to 1 to move the arm along the print.
  Printed material is drawn as a bead, travel moves as thin ochre
  lines.
- For the SCARA, the component checks that every point lies inside the
  ring-shaped area the arm can reach, that the joints stay within their
  angle and speed limits, and that the arm can pass from one point to
  the next in the same way the firmware would move it.
- A point that cannot be reached sets `reachOK` to false, lists the
  point in `problems`, and shows a red error on the component. Do not
  run a program while that error is shown. Move the part, reduce the
  speeds, or change the geometry instead.

<!-- IMAGE TODO: viewport screenshot of a deliberately out-of-reach placement:
     red error balloon on Robot Simulation, flagged problem points visible. -->
![An out-of-reach placement caught before it reaches hardware](images/robots-reach-error.png)

## Controlling a printer directly (panel 7 Print)

The 7 Print components talk to the machine from inside Grasshopper -
a printer with a Duet board, over WiFi. [[IAMLab Machine Console]] opens
a window that does the controlling: connect, home, jog (press and hold),
capture the origin, check and upload a job, start, pause and cancel it,
send single commands, drive to calibration points, and keep the job
logs. [[IAMLab Live View]] shows the machine and the growing bead in the
Rhino viewport, and [[IAMLab Machine Files]] manages the machine's file
storage. (The older Duet Connect, Status, Jog, Print and Terminal
components still open in saved files but are no longer on the ribbon.)

Before every action that moves the machine, the plugin runs a set of
checks. The job is checked against the machine limits before upload.
The upload is verified with a checksum. The machine must be homed, and
the plugin verifies this instead of assuming it. The machine type
reported by the firmware must match the selected profile. If the
machine was moved by something other than the plugin, the plugin
forgets the position and asks for a new homing. On the first
connection, the plugin remembers which board answered; if a different
board answers later at the same address, actions are blocked until you
accept the new board with "Trust connected machine" on the console
component. If any check fails, the action is blocked, and the console
writes the reason next to the disabled button. The emergency stop works
in every state.

Without a Robot Definition, the 7 Print components assume the SCARA arm
of the lab. With a `generic-duet-cartesian`
definition, they control that printer instead and check moves against
the travel limits stored in the firmware. The two calibration
components [[IAMLab Calib Points]] and [[IAMLab Scara Fit]] work only
with the SCARA.

<!-- IMAGE TODO: screenshot of the console window beside the Rhino viewport
     with the Live view: bed plate, posed arm, and the trail. -->
![Live control from the canvas](images/robots-duet-live.png)

Every job is recorded while it runs - the nozzle path with timestamps,
layer changes and speed - to a local buffer; on the console's Logs page
you keep a record with *Save this job*, or keep every record with
*Keep all job logs* and a folder of your choice (a network folder is
fine: if it disappears, nothing is lost and the copy resumes when it is
back). Live View can additionally colour the trail by how far the
machine's reported position deviates from the intended G-code path.

## Klipper and Marlin printers

Only Duet machines are driven over the network. `generic-klipper` and
`generic-marlin` are **dialect** profiles: they set the coordinate frame
and the default G-code flavour on [[IAMLab G-code Generator]], and you
transfer the resulting file to the printer yourself.

Wiring either one into the Machine Console, Live View or Machine Files
is refused with "use a Duet profile".

4.0.0 did drive Klipper printers directly, through Moonraker's HTTP API.
That was removed in 4.1.0: it was never connected to a real printer, and
the lab prints LDM only, so it could not be verified and served no
machine. The G-code side is untouched - the Klipper dialect still emits
`SET_VELOCITY_LIMIT`, `SET_PRESSURE_ADVANCE` and `SET_PRINT_STATS_INFO`,
and the job validator still accepts exactly those three.

