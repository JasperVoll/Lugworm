---
title: "G-code Generator"
nav_order: 4
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

{% include figure.html src="components/iamlab-g-code-generator" alt="G-code Generator: input groups on the canvas and the generated G-code in a panel" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `crvs` | Curve (tree) |  | Curves or polylines in print order, any tree |
| `layerHeight` | Number | 3 | Layer height, the E cross section height, mm |
| `width` | Number | 10 | Layer width, mm |
| `ramDiameter` | Number | 94 | Cartridge bore, mm (IAMLab ram extruder: 94) |
| `pressureCoeff` | Number | 1.2 | Calibrated flow multiplier |
| `printSpeed` | Number | 43 | Extruding feedrate, mm/s |
| `travelSpeed` | Number | 30 | Travel feedrate, mm/s |
| `retractSpeed` | Number | 1 | Ram screw speed for retract and prime, mm/s. The screw moves in raw mm and a retraction is about 0.1 mm, so keep this small |
| `firstLayerSpeedFactor` | Number | 0.5 | First layer speed factor, for example 0.5 |
| `retraction` | Integer |  | 0 = none: one continuous path, the ram never pulls back, and far seams still hop safely over printed material. 1 = light: seams that are close or vertically aligned are connected, and the ram retracts only on distant jumps. 2 = normal: retract, hop and prime at every seam. A single continuous spiral path is detected automatically |
| `seamTol` | Number | 0 | Light retraction: largest seam XY distance connected without retraction, mm (0 = 2 x width) |
| `alternateInfill` | Boolean | false | Alternate layers with open infill: reverse infill and begin from the previous layer's infill endpoint |
| `firmware` | Integer |  | 0 = Duet RRF, 1 = Marlin, 2 = Klipper. Not connected takes the robot definition's default, which is Duet RRF for the Potterbot or an not connected robot |
| `relativeE` | Boolean | true | Relative E (M83) when true |
| `pressureOffset` | Number | 0.2 | Ram screw retraction, mm |
| `primeExtra` | Number | 0.2 | Extra prime on restart, mm |
| `coast` | Number | 2 | Stop extruding this far before a path end, mm |
| `zHop` | Number | 5 | Extra travel clearance above printed material, mm. Travels lift to the highest printed Z along their corridor plus this margin (0 = one layer height) |
| `hopSpeed` | Number | 10 | Z hop lift and descend feedrate, mm/s (0 = travel speed) |
| `footerZLift` | Number | 20 | Relative Z lift before the homing at the end of the print, mm (0 = none) |
| `dwell` | Number | 0 | Pause after prime, ms |
| `leadIn` | Number | 0 | Flow lead in length for the pressure ramp, mm |
| `leadInBoost` | Number | 1 | Flow multiplier at a line start, at least 1 |
| `minLayerTime` | Number | 0 | Slow a layer that would print faster, s |
| `zOffset` | Number | 0 | Added to every emitted Z, mm. For nozzle to bed calibration only. The slicer already places each layer at its true height, so leave this at 0 unless the machine's Z zero is offset from the bed. Any other value raises or lowers the whole print and shows in the preview |
| `tol` | Number | 0.05 | Linearization tolerance, mm |
| `savePath` | Text |  | Optional .gcode file path to write on compute |
| `robot` | Text |  | Optional Robot Definition. Selects the coordinate frame and the default firmware dialect. Not connected keeps the Potterbot SCARA frame |

### Optional group: Pressure / linear advance inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `advanceK` | Number | 0.1 | Linear or pressure advance K (Marlin M900, Duet M572) |
| `accelPrint` | Number | 500 | Print acceleration, mm/s^2 (M204 P) |
| `accelTravel` | Number | 500 | Travel acceleration, mm/s^2 (M204 T) |

### Optional group: Per-axis accel/jerk (M201/M566) limits

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `accelXY` | Number | 1500 | Maximum XY acceleration, M201 X and Y, mm/s^2. Soften from the firmware default, for example 3000 to 1500. On Klipper it is the SET_VELOCITY_LIMIT ceiling when accelPrint is unset |
| `accelZ` | Number | 1000 | Maximum Z acceleration, M201 Z, mm/s^2, for example 2000 to 1000. Not emitted on Klipper, where printer.cfg max_z_accel applies |
| `jerkXY` | Number | 600 | Maximum XY instant speed change: M566 X and Y on Duet, mm/min, or M205 on Marlin, mm/s |
| `jerkZ` | Number | 600 | Maximum Z instant speed change, M566 or M205 Z, for example 1200 to 600 |
| `accelE` | Number | 500 | Maximum E (ram) acceleration, M201 E, mm/s^2. Caps the step rate burst of retract and prime that makes the Duet 2 hiccup |
| `jerkE` | Number | 300 | Maximum E (ram) instant speed change, M566 or M205 E |
| `retractAccelFactor` | Number | 0.5 | Scale below 1 applied to acceleration and jerk during retraction and travel moves to tame current spikes |

### Optional group: Arcs (G2/G3) inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `arcTol` | Number | Arc fit tolerance, mm |

### Optional group: ArcWelder post-process inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `arcPathTolPct` | Number | 5 | ArcWelder path tolerance percent |
| `arcMaxRadius` | Number | 1000 | ArcWelder max arc radius, mm |
| `arcWelderExe` | Text |  | Path to ArcWelderConsole.exe. If it is missing or fails the output stays unwelded. Disable this group to use the internal fitter |

### Optional group: Skirt inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `skirtOffset` | Number | Skirt distance from the part, mm |

### Optional group: Floors / pause inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `floorLayers` | Integer | 0 | Number of base layers |
| `pauseAfterFloor` | Boolean |  | Pause once the floor is done |
| `pauseTime` | Integer | 0 | Pause length, ms |

### Optional group: Header/footer override inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `header` | Text | Override the auto header |
| `footer` | Text | Override the auto footer |

### Optional group: Pump extrusion mode (MAI Pictor 3D) inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `nozzleDiameter` | Number | 0 | Nozzle diameter, mm. Pump mode sanity check |
| `pumpCalibCsv` | Text |  | Pump calibration CSV path with columns setting and flow_lpm. Empty uses the embedded placeholder, which is deliberately unusable and must be replaced with measured data |
| `slipFactor` | Number | 1 | Volumetric efficiency or slip factor. Default 1.0, calibrate per material |

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
| `eCompRefMul` | Number | 1 | Measured flow multiplier at the reference slope (1.0 = no compensation). Calibrate it with an inclined wall print, there is no usable default |
| `eCompRefDeg` | Number | 45 | Reference slope for the multiplier, deg from vertical stacking. For conical slices use the cone angle |

## Outputs

| Name | Type | Description |
|---|---|---|
| `gcode` | Text (list) | G-code lines |
| `preview` | Curve (tree) | Preview of the extruding moves |
| `travels` | Curve (tree) | Travel moves |
| `length` | Number | Extruded length, mm |
| `volume` | Number | Extruded volume, cm3 |
| `time` | Number | Estimated time, min |
| `info` | Text | Diagnostics |
| `pump` | Text (list) | Pump mode summary from the pump toggle group: required flow in l/min, recommended setting and warnings |
| `start` | Point | First print location in Rhino coordinates, including the Z offset. Hang the extruder here before a run. The startGcode output carries the machine's own numbers |
| `startGcode` | Text | Block to paste into a terminal: jog to the start XY at Z0 so you can set the nozzle to just touch, then start the print |

Back to [Components]({{ '/docs/components/' | relative_url }}).
