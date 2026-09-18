---
title: "Robots and Machine Control"
nav_order: 3
parent: "Workflows"
description: "Robots and Machine Control: instructions and practical checks for IAMLab Slicer."
permalink: /docs/workflows/robots-and-machine-control/
---

# Robots and Machine Control

Choose IAMLab Robot Definition once and wire its profile text to each `robot`
input. Text panels containing `"scara"` or `"none"` also work.
See [No warranty]({{ '/' | relative_url }}#no-warranty) before use.

| Profile | Machine type | Program written | Direct control |
|---|---|---|---|
| `none` | no machine, toolpath only | RepRap G-code | no |
| `generic-cartesian` | printer with X, Y, and Z axes | RepRap G-code | no |
| `generic-duet-cartesian` | printer with X, Y, and Z axes and a Duet board | RepRap G-code | yes (7 Print) |
| `potterbot-scara-v4` | the SCARA arm of the lab | RepRap G-code | yes (7 Print) |
| `generic-klipper` | Cartesian printer running Klipper | RepRap G-code, Klipper dialect | no |
| `generic-marlin` | Cartesian printer running Marlin | RepRap G-code, Marlin dialect | no |

{% include figure.html src="workflows/robot-profile.png" alt="Selecting a robot profile" %}

## Coordinate frame

An unwired Generator `robot` input selects the lab SCARA's mirrored X/Y axes.
For an ordinary printer, wire `generic-cartesian`, `generic-duet-cartesian`,
or `none`. Check `frame:` in `info` to confirm the convention.

## Check in Robot Simulation

Connect the definition and G-code to IAMLab Robot Simulation.

- Move `sim` from 0 to 1 to review the print: beads show extrusion, ochre lines travel.
- SCARA checks cover reach, joint angles and speeds, and firmware-compatible
  movement between points.
- Unreachable points set `reachOK` false, appear in `problems`, and cause a red
  error. Do not run while that error is shown. Move the part, reduce speeds,
  or change the geometry.

{% include figure.html src="workflows/reach-violation.png" alt="An out-of-reach placement caught before it reaches hardware" %}

## Direct control (7 Print)

[IAMLab Machine Console]({{ '/docs/components/iamlab-machine-console/' | relative_url }}) controls Duet machines over WiFi: connect, home,
press-and-hold jog, capture origin, check/upload jobs, start/pause/cancel,
send commands, visit calibration points and keep logs. [IAMLab Live View]({{ '/docs/components/iamlab-live-view/' | relative_url }})
shows the machine and bead; [IAMLab Machine Files]({{ '/docs/components/iamlab-machine-files/' | relative_url }}) manages storage.

Before motion, the plugin checks machine state. Jobs are checked against machine
limits before upload; uploads are checksum-verified. Homing is verified, and the
firmware machine type must match the profile. Movement outside the plugin
invalidates the position and requires homing again. The first connection records
the board identity; a different board at the same address blocks actions until
you accept it with "Trust connected machine". Any failed check blocks the action
and displays its reason beside the disabled button. Emergency stop works in
every state.

Without a Robot Definition, control assumes the lab SCARA. For other Duet
printers, use `generic-duet-cartesian`; moves are checked against firmware travel
limits. [IAMLab Calib Points]({{ '/docs/components/iamlab-calib-points/' | relative_url }}) and [IAMLab Scara Fit]({{ '/docs/components/iamlab-scara-fit/' | relative_url }}) require the SCARA.

{% include figure.html src="workflows/live-control.png" alt="Live control from the canvas" %}

Jobs record nozzle positions, timestamps, layer changes and speed to a local
buffer. In Logs, choose *Save this job* or *Keep all job logs* and a destination
folder. If a network folder disconnects, the buffer is retained and copying
resumes on reconnection. Live View can colour deviation from the intended path.

## Klipper and Marlin

`generic-klipper` and `generic-marlin` set the coordinate frame and default
G-code dialect on [IAMLab G-code Generator]({{ '/docs/components/iamlab-g-code-generator/' | relative_url }}). Transfer the file yourself.
Klipper is a G-code target only; there is no live Klipper connection. Marlin
also has no live connection. Machine Console, Live View and Machine Files refuse
these profiles with "use a Duet profile".

Klipper emits `SET_VELOCITY_LIMIT`, `SET_PRESSURE_ADVANCE` and
`SET_PRINT_STATS_INFO`; the job validator accepts exactly these three Klipper commands.
