---
title: "G-code Generator"
nav_order: 5
parent: "Components"
description: "G-code Generator: component inputs and outputs."
permalink: /docs/components/g-code-generator/
redirect_from:
  - /docs/components/iamlab-g-code-generator/
---

# G-code Generator

G-code Generator turns the placed layers into the G-code file
that the printer runs. The component handles the parts that are
specific to clay and paste: how much material is pushed out per
millimetre of path, how the ram pulls back and pushes again at a seam,
how fast the nozzle moves, and how travel moves lift over material that
is already printed. Many settings live in optional groups that are
switched on with a right-click only when they are needed. The
component has a compute button.

Panel: `3 G-code`. Nickname: `G-code`. Compute button: yes (runs only when the button is clicked).

{% include figure.html src="components/iamlab-g-code-generator" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `crvs` | Curve (tree) |  | Toolpath curves in Rhino document units and print order, with one tree branch per layer |
| `layerHeight` | Number | 3 | Layer height in mm used with layer width to calculate extrusion |
| `width` | Number | 10 | Layer width in mm used to calculate extrusion |
| `ramDiameter` | Number | 94 | Ram cartridge bore diameter in mm used to calculate ram travel (IAMLab ram: 94) |
| `pressureCoeff` | Number | 1.2 | Multiplier for calculated ram extrusion (1 = unchanged), calibrated for the material |
| `printSpeed` | Number | 43 | Nozzle speed while extruding, mm/s |
| `travelSpeed` | Number | 30 | Nozzle speed during travel without extrusion, mm/s |
| `retractSpeed` | Number | 1 | Ram speed during retraction and priming, mm/s of ram travel |
| `firstLayerSpeedFactor` | Number | 0.5 | Multiplier for first-layer print speed (0.5 = half speed) |
| `retraction` | Integer |  | Retraction mode: 0 = none, 1 = distant seams only, 2 = every seam, with clearance hops on distant travels |
| `seamTol` | Number | 0 | Light retraction: largest seam XY distance connected without retraction, mm (0 = 2 x width) |
| `alternateInfill` | Boolean | false | Alternate layers with open infill: reverse infill and begin from the previous layer's infill endpoint |
| `firmware` | Integer |  | Firmware format: 0 = Duet RRF, 1 = Marlin, 2 = Klipper (unwired = robot profile default, or Duet for Potterbot) |
| `relativeE` | Boolean | true | Use relative ram travel (M83) when true, or absolute ram positions (M82) when false |
| `pressureOffset` | Number | 0.2 | Ram travel pulled back for each retraction, mm (0 = none) |
| `primeExtra` | Number | 0.2 | Extra ram travel added when priming after a retraction, mm |
| `coast` | Number | 2 | Stop extruding this far before a path end, mm |
| `zHop` | Number | 5 | Extra travel clearance above printed material, mm. Travels lift to the highest printed Z along their corridor plus this margin (0 = one layer height) |
| `hopSpeed` | Number | 10 | Z hop lift and descend feedrate, mm/s (0 = travel speed) |
| `footerZLift` | Number | 20 | Relative Z lift before the homing at the end of the print, mm (0 = none) |
| `dwell` | Number | 0 | Pause after prime, ms |
| `leadIn` | Number | 0 | Distance over which the starting flow boost fades, mm (0 = off) |
| `leadInBoost` | Number | 1 | Starting flow multiplier, at least 1, fading over leadIn |
| `minLayerTime` | Number | 0 | Minimum layer duration in s, enforced by slowing print moves (0 = off) |
| `zOffset` | Number | 0 | Nozzle-to-bed correction added to all printed Z positions in mm (normally 0), also shown in the preview |
| `tol` | Number | 0.05 | Maximum curve-to-polyline deviation in mm |
| `savePath` | Text |  | Optional local file or folder to save on Compute, overwriting the target (folder uses print.gcode) |
| `robot` | Text |  | Robot Definition text selecting the coordinate frame and default firmware (unwired = Potterbot SCARA) |

### Optional group: Pressure / linear advance inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `advanceK` | Number | 0.1 | Calibrated advance value for the selected firmware (Duet and Klipper: seconds, Marlin: K) |
| `accelPrint` | Number | 500 | Print acceleration, mm/s^2 (M204 P) |
| `accelTravel` | Number | 500 | Travel acceleration, mm/s^2 (M204 T) |

### Optional group: Per-axis accel/jerk (M201/M566) limits

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `accelXY` | Number | 1500 | Maximum XY acceleration in mm/s^2 (Klipper uses it as the overall limit when accelPrint is unset) |
| `accelZ` | Number | 1000 | Maximum Z acceleration in mm/s^2 (not emitted on Klipper, which uses printer.cfg) |
| `jerkXY` | Number | 600 | Maximum XY instant speed change in mm/s, converted to mm/min for Duet M566 |
| `jerkZ` | Number | 600 | Maximum Z instant speed change in mm/s (used with jerkXY, not emitted on Klipper) |
| `accelE` | Number | 500 | Maximum ram acceleration in mm/s^2 for retraction and priming (not emitted on Klipper) |
| `jerkE` | Number | 300 | Maximum ram instant speed change in mm/s, converted for Duet M566 (not emitted on Klipper) |
| `retractAccelFactor` | Number | 0.5 | Scale below 1 applied to acceleration and jerk during retraction and travel moves to tame current spikes |

### Optional group: Arcs (G2/G3) inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `arcTol` | Number | 0.05 | Maximum deviation from the toolpath when fitting arcs, mm |

### Optional group: ArcWelder post-process inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `arcPathTolPct` | Number | 5 | Maximum ArcWelder path-length change in percent |
| `arcMaxRadius` | Number | 1000 | Largest arc radius ArcWelder may fit, mm |
| `arcWelderExe` | Text |  | ArcWelder executable path required with ArcWelder enabled (also enable Arcs), with unwelded output on failure |

### Optional group: Skirt inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `skirtOffset` | Number | 3 | Distance from the part to the priming skirt in mm, used with Skirt enabled |

### Optional group: Floors / pause inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `floorLayers` | Integer | 0 | Initial layer count before the optional floor pause (0 = none), without adding new floor geometry |
| `pauseAfterFloor` | Boolean |  | Lift and pause for pauseTime after floorLayers when true |
| `pauseTime` | Integer | 0 | Timed floor pause in ms (0 = no dwell), used when pauseAfterFloor is true |

### Optional group: Header/footer override inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `header` | Text | G-code text replacing the entire generated header (empty = automatic header) |
| `footer` | Text | G-code text replacing the entire generated footer (empty = automatic footer) |

### Optional group: Pump extrusion mode inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `nozzleDiameter` | Number | 0 | Nozzle bore in mm for pump flow checks (0 = no diameter check) |
| `pumpCalibCsv` | Text |  | Measured pump calibration CSV path with setting and flow_lpm columns, required for a usable pump recommendation |
| `slipFactor` | Number | 1 | Multiplier for required pump flow (1 = no slip correction), calibrated for the material |

### Optional group: Variable layer-height flow (adaptive / non-planar slices)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `varhMin` | Number | 0.25 | Local layer height floor as a fraction of nominal, default 0.25 |
| `varhMax` | Number | 2.5 | Local layer height ceiling as a fraction of nominal, default 2.5 |

### Optional group: Slope-compensated flow (conical/non-planar, calibrated)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `eCompRefMul` | Number | 1 | Measured flow multiplier at eCompRefDeg, required for slope compensation (1 = no change) |
| `eCompRefDeg` | Number | 45 | Reference stacking angle from vertical in deg for eCompRefMul (use coneAngle for conical slices) |

## Outputs

| Name | Type | Description |
|---|---|---|
| `gcode` | Text (list) | Generated G-code lines in machine coordinates and mm |
| `preview` | Curve (tree) | Extruding-move curves in Rhino document units |
| `travels` | Curve (tree) | Travel-move curves in Rhino document units |
| `length` | Number | Total nozzle path length while extruding, mm |
| `volume` | Number | Extruded volume, cm3 |
| `time` | Number | Estimated job duration in min |
| `info` | Text | Generation settings, save status and warnings |
| `pump` | Text (list) | Pump mode summary from the pump toggle group: required flow in l/min, recommended setting and warnings |
| `start` | Point | First print point in Rhino document units, including zOffset |
| `startGcode` | Text | Machine-coordinate commands to approach the start at Z0, for manual review of homing and clearance before sending |

Back to [Components]({{ '/docs/components/' | relative_url }}).
