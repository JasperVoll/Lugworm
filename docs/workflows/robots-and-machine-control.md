---
title: "Robots and Machine Control"
nav_order: 3
parent: "Workflows"
description: "Choose a machine profile, review motion, and run or export a job."
permalink: /docs/workflows/robots-and-machine-control/
---

# Robots and Machine Control

Choose a machine profile, review the generated motion, then transfer or run the job. Direct control is available for supported Duet machines; Marlin and Klipper use exported G-code files.

## 1. Choose the machine profile

Add [IAMLab Robot Definition]({{ '/docs/components/iamlab-robot-definition/' | relative_url }}) and connect its profile text to each component's `robot` input. Text panels containing `scara` or `none` also work.

| Profile | Use it for | G-code dialect | Direct control |
|---|---|---|---|
| `none` | Toolpath preview without a machine model | RepRap | No |
| `generic-cartesian` | A printer with X, Y, and Z axes | RepRap | No |
| `generic-duet-cartesian` | A Cartesian printer with a Duet board | RepRap | Yes |
| `potterbot-scara-v4` | The laboratory SCARA arm | RepRap | Yes |
| `generic-klipper` | A Cartesian printer running Klipper | Klipper | No |
| `generic-marlin` | A Cartesian printer running Marlin | Marlin | No |

An unwired Generator `robot` input uses the laboratory SCARA's mirrored X/Y axes. For a Cartesian printer, connect the appropriate Cartesian profile; use `none` for a model-free preview. Read `frame:` in Generator's `info` output to confirm the coordinate convention.

{% include figure.html src="workflows/robot-profile.png" alt="Selecting a robot profile" %}

## 2. Review the motion

Connect the same Robot Definition and the generated G-code to [IAMLab Robot Simulation]({{ '/docs/components/iamlab-robot-simulation/' | relative_url }}). Move `sim` from `0` to `1` to inspect the print. Beads show extrusion; ochre lines show travel.

For a SCARA profile, the checks cover reach, joint angles and speeds, and firmware-compatible movement between points. Unreachable targets set `reachOK` to false, appear in `problems`, and produce a red error. Correct the placement, speed, or geometry as appropriate, then regenerate and review the job before running it.

{% include figure.html src="workflows/reach-violation.png" alt="An out-of-reach placement detected in Robot Simulation" %}

## 3. Connect a Duet machine

The **7 Print** group contains three components:

- [Machine Console]({{ '/docs/components/iamlab-machine-console/' | relative_url }}) connects over WiFi and provides homing, press-and-hold jogging, origin capture, job checking and upload, print controls, commands, calibration positions, and logs.
- [Live View]({{ '/docs/components/iamlab-live-view/' | relative_url }}) shows the machine, nozzle position, and deposited bead.
- [Machine Files]({{ '/docs/components/iamlab-machine-files/' | relative_url }}) manages files on the connected machine.

Connect the Robot Definition before operating the console. Without one, control assumes the laboratory SCARA. For other Duet Cartesian printers, select `generic-duet-cartesian`; moves are checked against firmware travel limits. Calib Points and Scara Fit require the SCARA.

### Read the checks before moving

The plugin checks machine state and verifies homing before motion. The firmware machine type must match the profile. Movement outside the plugin invalidates the position and requires homing again.

The first connection records the board identity. If a different board appears at the same address, actions are blocked until you verify the machine and choose **Trust connected machine**. A failed check displays its reason beside the disabled button. The emergency-stop control remains available in every state.

### Upload and run a reviewed job

Use Machine Console to check and upload the job. Jobs are checked against machine limits before upload, and uploads are checksum-verified. Review the reported result before starting the print; use the console to start, pause, or cancel it.

{% include figure.html src="workflows/live-control.png" alt="Live control from the Grasshopper canvas" %}

### Keep the print log

Jobs record nozzle positions, timestamps, layer changes, and speed in a local buffer. In **Logs**, choose **Save this job** or **Keep all job logs**, then select a destination folder. If a network folder disconnects, the buffer is retained and copying resumes when the connection returns. Live View can colour deviation from the intended path.

## Transfer files to Klipper or Marlin

The `generic-klipper` and `generic-marlin` profiles set the coordinate frame and default dialect in [G-code Generator]({{ '/docs/components/iamlab-g-code-generator/' | relative_url }}). Write the reviewed file with [Save G-code]({{ '/docs/components/iamlab-save-g-code/' | relative_url }}) and transfer it using your machine's usual workflow.

There is no live connection for either firmware. Machine Console, Live View, and Machine Files refuse these profiles with the message *use a Duet profile*.

For Klipper, Generator emits `SET_VELOCITY_LIMIT`, `SET_PRESSURE_ADVANCE`, and `SET_PRINT_STATS_INFO`. These are the three Klipper-specific commands accepted by the job validator.

## Before running hardware

Machine control has been tested on the laboratory machine; treat other machines as untested. Review every job, keep the emergency stop within reach, and stay with a running print. Read the [safety and warranty information]({{ '/' | relative_url }}#safety) before use.
