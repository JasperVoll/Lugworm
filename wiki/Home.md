# IAMLab Slicer

IAMLab Slicer is a Grasshopper plugin for clay and LDM (liquid deposition
modeling) 3D printing.
It slices geometry with flat and non-planar layers, writes G-code for
Marlin, Duet RepRapFirmware, and Klipper, and can control a printer
with a Duet board directly over WiFi. A Klipper or Marlin machine is a
G-code target: generate the file and transfer it yourself.

It is developed at the Institute of Digital and Autonomous Construction
([IDAC](https://www.tuhh.de/idac)) at Hamburg University of Technology.

<!-- IMAGE TODO: Grasshopper canvas with the full pipeline wired
     (Test Geometry, Slice, Place, G-code Generator, Robot Simulation)
     and the Rhino viewport showing the posed SCARA with a printed bead.
     About 1600 px wide. -->
![IAMLab Slicer pipeline and SCARA simulation](images/home-hero.png)

Requires Rhino 8 for Windows with Grasshopper. The current release is
version 4.0.0.

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
| Run or record a component test | [[Component Test Round 2026-09-08]] |
| See what changed between versions | [[Release Notes]] |

## Capabilities

**Slicing.** Flat layers, adaptive planar spacing, and conical non-planar
layers. Adaptive layer height, six infill patterns, and a vase mode.
Walls can be laid in a running bond - adjacent loops shifted by half a
layer height so the beads interlock - and the infill can follow a wave,
in the plane or in height, using a sine or any curve drawn on the canvas.

**G-code.** Marlin, Duet RepRapFirmware, and Klipper dialects, with the
flow and pressure controls that ram or pump extrusion needs. A restart
component covers prints that failed part-way.

**Robots.** One Robot Definition component selects the machine: the SCARA
arm of the lab, an ordinary printer with X, Y, and Z axes, or none.
Robot Simulation then checks whether the arm can
reach every point of the print, and whether the joints stay within their
limits and speeds, before anything runs on hardware. With no robot
selected, the plugin works as an ordinary slicer.

**Machine control.** Printers with a Duet board can be driven from the
canvas: connect, jog, upload,
print, monitor, and control the heaters
and fans. Before every action that moves the machine, the plugin checks
that the machine is in the expected state and that the connected board
is the one the definition was set up with. If a check fails, the action
is blocked.

**Calibration.** A planned series of test prints replaces trial and
error: the plugin generates a run plan, fits a formula to the measured
results, and recommends the settings that best meet the stated goals.

## Safety

This software produces toolpaths and can command real machinery.
Check every G-code file and robot program in Robot Simulation before
running the program, keep the emergency stop within reach, and do not
leave a running print unattended. The robot models on screen are
approximate. The reach and joint checks are the safety mechanism, not
the drawn model.

## A useful mental model

Follow the data from left to right: **geometry → Slice → Place → G-code
Generator**. Connect Generator output separately to **Robot Simulation** for
review and **Save G-code** for writing a file. Add a
Robot Definition when the coordinate frame, kinematics, firmware dialect, or
extrusion process matters. Leave it at `none` for a model-free toolpath
preview.

The `info` output is a diagnostic report. Read it after each heavy component
computes. A tree is Grasshopper's grouped list: Slice returns one
branch per layer (and `{object; layer}` for multiple input objects), so keep
the tree structure when wiring contours into Place and G-code Generator.

## License and contact

MIT license. Copyright 2026 Jasper Vollmert, IDAC, Hamburg University of
Technology. For questions and bug reports use the
[issue tracker](../../issues); the [[FAQ]] explains what to include.
