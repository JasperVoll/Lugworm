---
title: "G-code Restart"
nav_order: 6
parent: "Components"
description: "G-code Restart: component inputs and outputs."
permalink: /docs/components/g-code-restart/
redirect_from:
  - /docs/components/iamlab-g-code-restart/
---

# G-code Restart

G-code Restart rebuilds a G-code file so that a print can
continue after a failure. You tell the component where the print
stopped, as a height, as the percentage shown on the Duet, or as the
last position, and the component removes everything that was already
printed, adds a safe approach to the restart point, and keeps the rest
of the job. The component has a compute button.

Panel: `3 G-code`. Nickname: `Restart`. Compute button: yes (runs only when the button is clicked).

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `gcode` | Text (list) |  | G-code of the failed print as one string or a list of lines |
| `gcodePath` | Text |  | Original G-code file path, overriding gcode and giving exact file positions for percent mode |
| `z` | Number | 10 | Restart at the first extruding move at or above this machine Z in mm, used if percent and point are unset |
| `percent` | Number | 50 | Restart file progress from 0 to 100 percent (multiply a Print component's 0 to 1 progress by 100) |
| `point` | Point |  | Failure point in Rhino document units, overriding percent and z to resume after the nearest printed point |
| `primeE` | Number | 0 | Ram prime before resuming, mm of E. Refills the nozzle after an air bubble (0 = none) |
| `travelSpeed` | Number | 60 | Feedrate of the safe approach travels, mm/s |
| `hopSpeed` | Number | 0 | Feedrate of the final descent onto the part, mm/s (0 = travel speed) |
| `retractSpeed` | Number | 2 | Ram speed while priming, mm/s of ram travel |
| `clearance` | Number | 10 | Clearance above everything printed so far for the approach, mm |
| `savePath` | Text |  | Optional local file or folder to save on Compute, overwriting the target (folder uses restart.gcode) |
| `robot` | Text |  | Optional Robot Definition - the coordinate frame the job's G-code was emitted in, exactly as the G-code Generator reads it. Unwired keeps the Potterbot SCARA frame (y = -x mirror) for the 'point' input and the resume / printed / remaining outputs; a generic Cartesian definition uses the machine's own coordinates |

## Outputs

| Name | Type | Description |
|---|---|---|
| `gcode` | Text (list) | Restart G-code lines |
| `resume` | Point | Restart point in Rhino document units |
| `printed` | Curve (list) | Skipped path in Rhino document units, to compare with the part already on the bed |
| `remaining` | Curve (list) | Path to print after restarting, in Rhino document units |
| `info` | Text | Restart selection, source accuracy and save status |

Back to [Components]({{ '/docs/components/' | relative_url }}).
