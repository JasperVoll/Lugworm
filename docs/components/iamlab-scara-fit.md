---
title: "IAMLab Scara Fit"
nav_order: 22
parent: "Components"
description: "IAMLab Scara Fit: component inputs, outputs, and usage notes."
permalink: /docs/components/iamlab-scara-fit/
---

# IAMLab Scara Fit

IAMLab Scara Fit calculates the real arm lengths of the SCARA from the
difference between the commanded and the measured mark positions of a
calibration run. The output includes corrected values for the firmware
configuration and for the robot description used by the plugin, plus a
report on how well the fit matches the marks. The component has a
compute button.

Panel: `7 Print`. Nickname: `Fit`. Compute button: yes (runs only when the button is clicked).

{% include figure.html src="components/iamlab-scara-fit.png" alt="IAMLab Scara Fit on the canvas" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `commanded` | Point (list) |  | Commanded mark positions from Calib Points |
| `measured` | Point (list) |  | Measured mark positions in your own measurement grid frame, in the same order as the commanded points |
| `L1` | Number | 549.06 | Nominal first link length, mm (the current M669 P) |
| `L2` | Number | 457.23 | Nominal second link length, mm (the current M669 D) |
| `elbow` | Integer |  | Arm mode the fit solves in, 0 up or 1 down. Defaults to down and must match the arm mode the marks were printed in |

### Optional group: Fit configuration (freeze / free parameters)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `fitL1` | Boolean | true | Fit L1 |
| `fitL2` | Boolean | true | Fit L2 |
| `fitJ1` | Boolean |  | Fit the J1 zero offset. It is exactly degenerate with the grid rotation, so enable it only with fitFrame off and a grid physically aligned to the machine |
| `fitJ3` | Boolean | true | Fit the J3 (elbow) zero offset |
| `fitFrame` | Boolean | true | Fit the measurement grid frame, origin and rotation |
| `fitZ` | Boolean |  | Fit a constant Z offset (needs measured Z values) |

## Outputs

| Name | Type | Description |
|---|---|---|
| `L1` | Number | Fitted first link length, mm. Connect it into the L1 inputs |
| `L2` | Number | Fitted second link length, mm. Connect it into the L2 inputs |
| `j1Offset` | Number | J1 zero offset, deg (0 unless fitJ1) |
| `j3Offset` | Number | J3 (elbow) zero offset, deg. Fold it into the homing macro |
| `frame` | Plane | Measurement grid frame in robot coordinates |
| `residuals` | Number (list) | Residual per point, mm |
| `rms` | Number | RMS residual, mm |
| `robotXml` | Text | Corrected robot.xml link snippet |
| `m669` | Text | Corrected M669 line for config.g, with homing notes |
| `report` | Text | Full fit report, including conditioning |

## Notes

- `commanded` comes from the `points` output of [IAMLab Calib Points]({{ '/docs/components/iamlab-calib-points/' | relative_url }});
  `measured` are the positions measured on the bed, in the same order.
- The Fit group chooses which values are fitted. `fitJ1` is normally
  off because a J1 offset cannot be told apart from a rotation of the
  measurement grid.
- Copy `m669` into `config.g` on the board and `robotXml` into the
  robot description of the plugin.

Back to [Components]({{ '/docs/components/' | relative_url }}).
