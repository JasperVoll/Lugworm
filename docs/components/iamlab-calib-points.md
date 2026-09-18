---
title: "IAMLab Calib Points"
nav_order: 1
parent: "Components"
description: "IAMLab Calib Points: component inputs, outputs, and usage notes."
permalink: /docs/components/iamlab-calib-points/
---

# IAMLab Calib Points

IAMLab Calib Points generates a grid of marking positions for the SCARA
arm of the lab and drives the machine to each of them, one by one, so
that a mark can be made on the bed. Measuring where the marks actually
landed, and comparing with where they were commanded, is the input for
[IAMLab Scara Fit]({{ '/docs/components/iamlab-scara-fit/' | relative_url }}). Every move is checked in the same way as a jog
move.

Panel: `7 Print`. Nickname: `CalPts`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-calib-points.png" alt="IAMLab Calib Points on the canvas" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `host` | Text |  | Host string from the Machine Console host output. Optional, the grid is generated without a connection |
| `nRadii` | Integer | 3 | Number of radii. The fit wants at least 3 |
| `nAngles` | Integer | 5 | Points per radius |
| `rMin` | Number | 350 | Innermost radius, mm |
| `rMax` | Number | 900 | Outermost radius, mm |
| `angStart` | Number | -120 | Grid start angle in the machine frame, measured like J1, deg |
| `angEnd` | Number | 30 | Grid end angle in the machine frame, measured like J1, deg |
| `zSafe` | Number | 50 | Travel height, mm |
| `zMark` | Number | 2 | Marking height, mm |
| `feed` | Number | 20 | Move feedrate, mm/s |

### Optional group: Bed rectangle filter (keep marks on the physical bed)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `bedCenter` | Point |  | Bed center in RHINO coordinates |
| `bedWidth` | Number | 1000 | Bed width along RHINO X, mm |
| `bedDepth` | Number | 700 | Bed depth along RHINO Y, mm |

### Optional group: Machine limit overrides (L1/L2, reach, Z, elbow)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `L1` | Number | 549.06 | First link length, mm |
| `L2` | Number | 457.23 | Second link length, mm |
| `reachMin` | Number | 235 | Minimum reach, the dead zone radius, mm (0 = L1 minus L2) |
| `zMax` | Number | 400 | Build height, mm (firmware M208 Z maximum) |
| `xMin` | Number | -300 | Lowest allowed X, mm (firmware M208 X minimum) |
| `xMax` | Number | 10000 | Highest allowed X, mm (firmware M208 X maximum) |
| `yMin` | Number | -10000 | Lowest allowed Y, mm (firmware M208 Y minimum) |
| `yMax` | Number | 0 | Highest allowed Y, mm (firmware M208 Y maximum) |
| `checkEnvelope` | Boolean | true | Check moves against the travel limits stored in the firmware (M208) |
| `elbow` | Integer |  | Fallback arm mode, 0 up or 1 down, for generating mark poses and planning moves before a verified home exists. A verified home's endstop mode takes precedence |
| `bed` | Text |  | Print Bed definition. Marks off the plate are dropped so every mark can be measured. Not connected applies no bed filter |

## Outputs

| Name | Type | Description |
|---|---|---|
| `points` | Point (list) | Commanded mark positions in Rhino coordinates at marking height. Wire into Scara Fit |
| `current` | Point | Currently selected mark in Rhino coordinates |
| `index` | Integer | Current mark index |
| `sheet` | Text | Printable measurement protocol |
| `info` | Text | Diagnostics |

## Notes

- The marks are laid out on several radii and angles around the arm.
  At least three radii are recommended for the fit.
- `points` is connected to the `commanded` input of Scara Fit; the
  `sheet` output is a printable measurement protocol.
- Buttons on the component step through the marks and move the machine.

Back to [Components]({{ '/docs/components/' | relative_url }}).
