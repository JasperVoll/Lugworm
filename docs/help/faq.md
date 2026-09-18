---
title: "FAQ"
nav_order: 1
parent: "Help"
description: "FAQ: instructions and practical checks for IAMLab Slicer."
permalink: /docs/help/faq/
---

# FAQ

## Why does the canvas not update?

Slice, G-code Generator, Overhang, DoE Design, RSM Evaluate, Desirability,
G-code Restart and Scara Fit cache heavy work. Click Compute after input changes
and read `info`; caching avoids rerunning on every slider change.

## Why do wires produce no paths?

Check types and accidental flattening/grafting. Wire `Test Geometry.geometry`
to `Slice.geo`, `Slice.contours` to `Place.contours`, then Place to
`G-code Generator.crvs`. Slice accepts meshes, Breps and surfaces; unusable
geometry produces diagnostics. See [Quick Start]({{ '/docs/getting-started/quick-start/' | relative_url }}).

## Which robot profile should I choose?

Use `none` for model-free preview/files, a generic Cartesian profile for a
Cartesian machine, or `potterbot-scara-v4` for the lab SCARA. Wire the same
profile to Generator and Simulation so review matches the program.

## Can I check without hardware?

Inspect diagnostics, use dry-run where offered and validate G-code in Robot
Simulation. Offline checks do not prove a real Duet connection; verify hardware
in a controlled machine session.

## Why is G-code mirrored?

An unwired `robot` input uses the lab SCARA frame. Wire `generic-cartesian` or
`none` for normal printer axes; confirm the convention in `info`.

## Which firmware and control options work?

`firmware` selects Marlin, Duet RepRapFirmware or Klipper G-code; unwired, it
follows Robot Definition. Direct control supports Duet over WiFi only. Motion
is checked and blocked on failure. Control defaults to the lab SCARA; use
`generic-duet-cartesian` for a Cartesian Duet machine. Transfer Marlin/Klipper
files yourself. See [Robots and Machine Control]({{ '/docs/workflows/robots-and-machine-control/' | relative_url }}).

## Does the plugin connect to the network automatically?

Apart from Duet control, only IAMLab Version's optional update check uses the
network. It defaults off; `check = false` or no release source means no request.

## What about units and decimal commas?

Geometry is converted from document units; numeric mm inputs and G-code stay in
millimetres. G-code and reports use decimal points regardless of locale. CSV
accepts English or German numbers but refuses mixed formats within a file.

## Why does Overhang warn at default settings?

The 25° and 45° thresholds are uncalibrated placeholders. Calibrate with a
stepped-cone print in your material and enter the measured values.

## Are Desirability `alternatives` trade-offs?

They are other good settings for the same combined goal. Enable `pareto` for
trade-offs between individual goals.

## Is the Machine Console password stored?

`password` can be stored in the `.gh` file, readable by anyone with it. Clear
it before sharing. Connection state is never stored; reopening never touches
the machine.

## What belongs in a bug report?

Use the [bug template](https://github.com/JasperVoll/iamlab-slicer-documentation/issues). Include the installed version
from IAMLab Version, failing `info`, an error-balloon screenshot and a minimal
reproducing `.gh` file with the password cleared.
