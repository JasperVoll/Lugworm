---
title: "Troubleshooting"
nav_order: 2
parent: "Help"
description: "Troubleshooting: instructions and practical checks for IAMLab Slicer."
permalink: /docs/help/troubleshooting/
---

# Troubleshooting

For installation failures, see [Installation]({{ '/docs/getting-started/installation/' | relative_url }}).

## Slicing and G-code

| Symptom | Check |
|---|---|
| A component with a compute button shows an old or empty result | Click the compute button. These components do not recompute on their own. |
| `spacing` raises a hard error | The value must be greater than 0. |
| Volume or time is implausible | Compare `layerHeight` on Slice and on the G-code Generator. The values must match. |
| The part prints at the wrong height | `zOffset` on the G-code Generator is a small correction for the nozzle height, with a default of 0. The layer height is already included, so do not add the layer height here again. |
| The G-code is mirrored on an ordinary printer | Connect a Robot Definition to the `robot` input of the G-code Generator. Without a definition, the axes are written for the SCARA arm of the lab. Check `frame:` in `info`. |
| Spiralize falls back or fails | A layer contains more or fewer than one closed contour. Simplify the shape. |
| The volume looks wrong after conical or adaptive slicing | Enable the `varh` group on the G-code Generator. |
| The pump group changes nothing in the G-code | Expected. The pump group only calculates a recommended pump setting and never changes the extrusion values. |

## Units and files

| Symptom | Check |
|---|---|
| Unexpected results after changing the document units | The cached results of the compute-button components are discarded when the units change. Click the compute button again. Numeric inputs documented in mm stay in millimetres in every document unit. |
| A measurement table or pump CSV is refused | The named line mixes English (`1.5`) and German (`1,5`) numbers, uses thousands separators, or contains broken quoting. Export the file in one consistent format. |
| Save G-code never writes | The `write` input needs a Button, which fires once, not a Toggle. |
| Save G-code reports "writing" but `written` stays false | The file is written in the background; the confirmation arrives on the next solve. A failure is reported as a warning. If the file was locked or the network share was unreachable, click the Button again. |

## Robots and machine control

| Symptom | Check |
|---|---|
| Robot Simulation shows the red error "targets are outside robot limits" | Do not ignore the error. Move the part with `printCenter`, reduce the speeds, or change the geometry, then simulate again. |
| A machine action is refused with a message about the machine identity | A different board answers at this address than the one the definition was set up with, or the board could not be identified. Verify the address, then use "Trust connected machine" in the context menu of the Machine Console component. |
| A Duet action is refused with "machine envelope unknown" | For an ordinary printer, the plugin reads the travel limits from the firmware. Home the machine, check `config.g` (Duet), or connect the limits explicitly. `printer.cfg` is the Klipper configuration; Klipper has no live connection. |
| Jog or print is refused after commands were sent to the machine by other means | Expected. When the machine was moved by something other than the plugin, the plugin no longer trusts the position. Home the machine or reconnect. |

For persistent problems, follow [FAQ]({{ '/docs/help/faq/' | relative_url }})'s bug-report checklist: installed version
from IAMLab Version, failing `info`, an error screenshot and a minimal `.gh` file.
