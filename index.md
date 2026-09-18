---
title: "Home"
nav_order: 1
description: "Laboratory slicing, experiments, and machine control in Grasshopper."
permalink: /
---

# IAMLab Slicer

<p class="iamlab-intro">I am a laboratory slicer.</p>

We work with clay and cementitious materials in a materials laboratory. The focus is repeatable specimens and controlled experiments.

{: .warning }
> **Not released yet.** The first public package is being prepared; the
> Releases page is still empty. The documentation below is complete and
> describes the release as it will ship. For earlier access, write to
> [jasper.vollmert@tuhh.de](mailto:jasper.vollmert@tuhh.de).

<div class="iamlab-actions">
<a class="btn btn-primary" href="{{ '/docs/getting-started/installation/' | relative_url }}">Install the package</a>
<a class="btn" href="{{ '/docs/getting-started/quick-start/' | relative_url }}">Print a first specimen</a>
</div>

<div class="iamlab-cards">
<div class="iamlab-card experiment"><h2>Plan an experiment</h2><p>Test geometry, design of experiments, response surfaces, and Pareto selection.</p><a href="{{ '/docs/workflows/calibration-and-doe/' | relative_url }}">Calibration and DoE</a></div>
<div class="iamlab-card"><h2>Shape the parameters</h2><p>Map flow and speed with Graph Mapper; read the results with Legend.</p><a href="{{ '/docs/components/iamlab-graph-mapper/' | relative_url }}">Graph Mapper</a></div>
<div class="iamlab-card machine"><h2>Connect the machine</h2><p>Upload, jog, and monitor a Duet machine from Grasshopper.</p><a href="{{ '/docs/workflows/robots-and-machine-control/' | relative_url }}">Machine control</a></div>
</div>

Grasshopper plugin for clay and LDM (liquid deposition modeling) 3D printing.
Requires Rhino 8 for Windows. Developed at [IDAC](https://www.tuhh.de/idac/news),
Hamburg University of Technology.

{% include figure.html src="home/pipeline-and-simulation.png" alt="IAMLab Slicer pipeline and SCARA simulation" %}

## Where to start

| Task | Page |
|---|---|
| Install the plugin | [Installation]({{ '/docs/getting-started/installation/' | relative_url }}) |
| Slice a first part | [Quick Start]({{ '/docs/getting-started/quick-start/' | relative_url }}) |
| Look up a component | [Components]({{ '/docs/components/' | relative_url }}) |
| Learn common terms and checks | [Quick Start]({{ '/docs/getting-started/quick-start/' | relative_url }}) |
| Print with a robot arm or control a Duet machine | [Robots and Machine Control]({{ '/docs/workflows/robots-and-machine-control/' | relative_url }}) |
| Slice non-planar (conical) | [Non-Planar Slicing]({{ '/docs/workflows/non-planar-slicing/' | relative_url }}) |
| Calibrate settings for a new material | [Calibration and DoE]({{ '/docs/workflows/calibration-and-doe/' | relative_url }}) |
| Solve a problem | [Troubleshooting]({{ '/docs/help/troubleshooting/' | relative_url }}), [FAQ]({{ '/docs/help/faq/' | relative_url }}) |

## Capabilities

- **Slice:** planar, adaptive and conical layers; vase mode, running-bond walls,
  and planar or height-varying wave infill.
- **Generate:** Marlin, Duet RepRapFirmware and Klipper G-code with ram/pump
  flow and pressure controls; restart interrupted prints.
- **Simulate:** preview without a robot, or check SCARA reach, joint limits
  and speeds before running hardware.
- **Control:** connect, jog, upload, print, monitor, and control heaters and
  fans on a Duet machine over WiFi. Motion is blocked if machine-state or
  board-identity checks fail. Transfer Marlin and Klipper files yourself.
- **Calibrate:** plan test prints, fit measured results and select settings.

## Safety

This software produces toolpaths and can command real machinery.
Check every G-code file and robot program in Robot Simulation before
running the program, keep the emergency stop within reach, and do not
leave a running print unattended. The robot models on screen are
approximate. The reach and joint checks are the safety mechanism, not
the drawn model.

## No warranty

This plugin is provided as is, with no warranty of any kind. No responsibility is accepted for the firmware connection or for any problem with your robot, printer, or material, including damage, injury, or lost work.

Machine control has been tested only on our own machine. Other firmwares and other machines have not been tested extensively. Treat any machine other than the one this was developed on as untested.

If you would like to collaborate and have your machine made compatible with the plugin, contact me directly: jasper.vollmert@tuhh.de

## Workflow

**Geometry → Slice → Place → G-code Generator**. Send Generator output to
**Robot Simulation** for review and **Save G-code** for writing. Use Robot
Definition for the coordinate frame, kinematics, firmware dialect and extrusion
process; select `none` for model-free preview. Read each `info` report after
computing. Preserve Slice's layer branches (`{object; layer}` for multiple
objects). See [Quick Start]({{ '/docs/getting-started/quick-start/' | relative_url }}) for wiring and settings.

## License and contact

MIT license. Copyright 2026 Jasper Vollmert, IDAC, Hamburg University of
Technology. Use the [issue tracker](https://github.com/JasperVoll/iamlab-slicer-documentation/issues); see [FAQ]({{ '/docs/help/faq/' | relative_url }}) for report details.
