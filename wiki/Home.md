# IAMLab Slicer

Grasshopper plugin for clay and LDM (liquid deposition modeling) 3D printing.
Requires Rhino 8 for Windows. Developed at [IDAC](https://www.tuhh.de/idac),
Hamburg University of Technology.

<!-- IMAGE TODO: Grasshopper canvas with the full pipeline wired
     (Test Geometry, Slice, Place, G-code Generator, Robot Simulation)
     and the Rhino viewport showing the posed SCARA with a printed bead.
     About 1600 px wide. -->
![IAMLab Slicer pipeline and SCARA simulation](images/home-hero.png)

## Where to start

| Task | Page |
|---|---|
| Install the plugin | [[Installation]] |
| Slice a first part | [[Quick Start]] |
| Look up a component | [[Components]] |
| Learn common terms and checks | [[Quick Start]] |
| Print with a robot arm or control a Duet machine | [[Robots and Machine Control]] |
| Slice non-planar (conical) | [[Non Planar Slicing]] |
| Calibrate settings for a new material | [[Calibration and DoE]] |
| Solve a problem | [[Troubleshooting]], [[FAQ]] |

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
objects). See [[Quick Start]] for wiring and settings.

## License and contact

MIT license. Copyright 2026 Jasper Vollmert, IDAC, Hamburg University of
Technology. Use the [issue tracker](../../issues); see [[FAQ]] for report details.
