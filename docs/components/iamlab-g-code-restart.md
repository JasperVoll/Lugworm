---
title: "IAMLab G-code Restart"
nav_order: 5
parent: "Components"
description: "IAMLab G-code Restart: component inputs, outputs, and usage notes."
permalink: /docs/components/iamlab-g-code-restart/
---

# IAMLab G-code Restart

IAMLab G-code Restart rebuilds a G-code file so that a print can
continue after a failure. You tell the component where the print
stopped, as a height, as the percentage shown on the Duet, or as the
last position, and the component removes everything that was already
printed, adds a safe approach to the restart point, and keeps the rest
of the job. The component has a compute button.

Panel: `3 G-code`. Nickname: `Restart`. Compute button: yes (runs only when the button is clicked).

{% include figure.html src="components/iamlab-g-code-restart.png" alt="IAMLab G-code Restart on the canvas" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `gcode` | Text (list) |  | G-code of the failed print as one string or a list of lines |
| `gcodePath` | Text |  | Optional path of the original G-code file. Percent mode uses its exact bytes. Text only input is reconstructed as UTF-8 with LF and marked approximate |
| `z` | Number | 10 | Restart Z height, mm. Resume at the first extruding move at or above this Z. Wire one of z, percent or point |
| `percent` | Number | 50 | Duet file progress, 0 to 100 percent, the value the board or DWC displayed when the print failed |
| `point` | Point |  | Rhino XYZ point at the failure, for example a Live View or Machine Console position output. Convert raw Duet machine XYZ to Rhino coordinates first. Resumes after the nearest printed point |
| `primeE` | Number | 0 | Ram prime before resuming, mm of E. Refills the nozzle after an air bubble (0 = none) |
| `travelSpeed` | Number | 60 | Feedrate of the safe approach travels, mm/s |
| `hopSpeed` | Number | 0 | Feedrate of the final descent onto the part, mm/s (0 = travel speed) |
| `retractSpeed` | Number | 2 | Feedrate of the prime move, mm/s |
| `clearance` | Number | 10 | Clearance above everything printed so far for the approach, mm |
| `savePath` | Text |  | Optional .gcode file path to write on compute |

## Outputs

| Name | Type | Description |
|---|---|---|
| `gcode` | Text (list) | Restart G-code lines |
| `resume` | Point | Point the print resumes at |
| `printed` | Curve (list) | Already printed path that the restart skips. Check it matches the part on the bed |
| `remaining` | Curve (list) | Path the restart file prints |
| `info` | Text | Diagnostics |

## Notes

- Connect only one of `z`, `percent`, and `point`. When more than one
  is connected, `point` wins over `percent`, and `percent` wins over
  `z`.
- `percent` works exactly only when `gcodePath` points to the original
  file, because the Duet counts bytes of the file. Without the file,
  the position is approximate.
- `point` is in Rhino document coordinates. For the lab machine frame,
  raw Duet `(X, Y, Z)` maps to Rhino `(-Y, -X, Z)` in millimetres.
  A Live or Console position output avoids applying that conversion manually.
  For example, raw Duet `(200, -500, 400)` mm is Rhino `(500, -200, 400)` mm,
  or Rhino `(19.6850, -7.8740, 15.7480)` in an inch document.
- Check the `printed` output against the part on the bed before
  starting the restart file: the curves must match what is already
  printed.
- `primeE` pushes some material before the first move to refill the
  nozzle after an air gap.

Back to [Components]({{ '/docs/components/' | relative_url }}).
