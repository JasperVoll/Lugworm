---
title: "Robot Simulation"
nav_order: 19
parent: "Components"
description: "Robot Simulation: component inputs and outputs."
permalink: /docs/components/robot-simulation/
redirect_from:
  - /docs/components/iamlab-robot-simulation/
---

# Robot Simulation

Robot Simulation shows a print before it runs. The component
draws the toolpath and the printed material, and, when a Robot
Definition with an arm is connected, poses the arm along the path and
checks that every point can be reached within the joint limits and
speeds of the arm. Dragging the `sim` slider from 0 to 1 plays the
print. With the profile `none`, the component shows the toolpath and
the material only.

Panel: `4 Robot`. Nickname: `Robot Sim`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-robot-simulation" alt="Robot Simulation: moving the sim slider steps the SCARA arm through the print" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `robot` | Text |  | Robot Definition. Empty or none shows the toolpath and extrusion without a robot model |
| `gcode` | Text (list) |  | G-code as one string or a list of lines |
| `toolpath` | Curve (tree) |  | Toolpath curves used when G-code is empty |
| `sim` | Number | 1 | Scrub from 0 to 1 through the accumulated print |
| `showRobot` | Boolean | true | Show the selected robot model when available |
| `showExtrusion` | Boolean | true | Show deposited material as a continuous extrusion mesh |
| `width` | Number | 10 | Extrusion width, mm |
| `height` | Number | 0 | Extrusion or layer height, mm (0 = infer from the layer metadata) |
| `bed` | Text |  | Print Bed definition, the only source of plate size, shape and position. Not connected draws no plate |

## Outputs

| Name | Type | Description |
|---|---|---|
| `joints` | Number (tree) | Joint samples, one branch per joint. Empty without robot kinematics |
| `arm` | Curve | Schematic arm at the scrub position |
| `tcp` | Curve | Displayed tool center path |
| `outline` | Curve | Bed outline |
| `reachOK` | Boolean | True when all targets pass the selected robot's reach checks, or when no kinematics are selected |
| `problems` | Point (list) | Flagged target positions |
| `messages` | Text (list) | Validation messages |
| `info` | Text | Profile and preview diagnostics |

Back to [Components]({{ '/docs/components/' | relative_url }}).
