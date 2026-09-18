---
title: "Home"
nav_order: 1
description: "Laboratory slicing, experiments, and machine control in Grasshopper."
permalink: /
---

# IAMLab Slicer

<p class="iamlab-intro">Slicing, material experiments, and machine control in Grasshopper.</p>

IAMLab Slicer is a Rhino 8 plugin for clay and cementitious-material 3D printing. Developed in a materials laboratory, it brings specimen design, toolpath generation, and print review into one Grasshopper workflow.

{: .warning }
> **The public package is being prepared.** For early access, contact [jasper.vollmert@tuhh.de](mailto:jasper.vollmert@tuhh.de). The installation guide explains how to install the package once you have it.

<div class="iamlab-actions">
<a class="btn btn-primary" href="{{ '/docs/getting-started/' | relative_url }}">Get started</a>
<a class="btn" href="{{ '/docs/getting-started/component-map/' | relative_url }}">Explore the component map</a>
</div>

## From geometry to a reviewed toolpath

Start with **Geometry → Slice → Place → G-code Generator**. Review the generated job in **Robot Simulation**, then write it with **Save G-code**. Robot Definition supplies the coordinate frame, kinematics, firmware dialect, and extrusion process; choose `none` for a preview without a machine model.

The [Getting started guide]({{ '/docs/getting-started/' | relative_url }}) walks through a first toolpath. The [Component Map]({{ '/docs/getting-started/component-map/' | relative_url }}) shows the main connections and the tools you can add along the way.

{% include figure.html src="home/pipeline-and-simulation.png" alt="IAMLab Slicer pipeline and SCARA simulation" %}

## What you can do

<div class="iamlab-cards">
<div class="iamlab-card"><h2>Build a toolpath</h2><p>Slice planar, adaptive, or conical layers. Position the part, set seams, and generate G-code.</p><a href="{{ '/docs/workflows/non-planar-slicing/' | relative_url }}">Explore layer strategies</a></div>
<div class="iamlab-card experiment"><h2>Calibrate a material</h2><p>Plan test prints, fit measured results, and compare settings across several goals.</p><a href="{{ '/docs/workflows/calibration-and-doe/' | relative_url }}">Follow the calibration workflow</a></div>
<div class="iamlab-card machine"><h2>Prepare and run a job</h2><p>Review robot motion, export a file, or upload and monitor a job on a supported Duet machine.</p><a href="{{ '/docs/workflows/robots-and-machine-control/' | relative_url }}">Set up machine control</a></div>
</div>

The plugin also supports vase mode, running-bond walls, and planar or height-varying wave infill. Graph Mapper shapes parameter values, while Legend explains analysis colours. G-code output supports Duet RepRapFirmware, Marlin, and Klipper, with ram/pump flow and pressure controls. G-code Restart helps prepare an interrupted print for continuation.

Direct machine control is available for Duet machines over WiFi, including jogging, print controls, heaters, and fans. Transfer Marlin and Klipper files using your usual machine workflow.

## Find the right guide

| Task | Start here |
|---|---|
| Install the Rhino 8 Windows package | [Installation]({{ '/docs/getting-started/installation/' | relative_url }}) |
| Create and preview a first toolpath | [Getting started]({{ '/docs/getting-started/' | relative_url }}) |
| Understand the connections | [Component Map]({{ '/docs/getting-started/component-map/' | relative_url }}) |
| Look up inputs, outputs, or optional groups | [Component reference]({{ '/docs/components/' | relative_url }}) |
| Follow a task from start to finish | [Workflows]({{ '/docs/workflows/' | relative_url }}) |
| Report a problem or contact the team | [About]({{ '/docs/about/' | relative_url }}) |

## Safety

IAMLab Slicer generates toolpaths and can command real machinery. Review every G-code file and robot program in Robot Simulation before running it. Keep the emergency stop within reach and stay with a running print.

The drawn robot models are approximate. Read the reach, joint-limit, and speed checks as well as inspecting the preview. Machine-state and board-identity checks block control actions when they fail.

## No warranty


THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

This is the warranty disclaimer of the MIT license under which IAMLab Slicer is
released; it is reproduced here because the software commands real machinery.
Nothing is warranted, including that the software is useful or fit for your
purpose, and no responsibility is accepted for the firmware connection or for
any problem with a robot, printer, or material, including damage, injury, or
lost work.

Machine control has been tested only on our own machine. Other firmwares and
other machines have not been tested extensively. Treat any machine other than
the one this was developed on as untested. To collaborate on making a machine
compatible, see [About]({{ '/docs/about/' | relative_url }}).

## License and contact

IAMLab Slicer is released under the MIT license. Copyright 2026 Jasper Vollmert, [IDAC](https://www.tuhh.de/idac/news), Hamburg University of Technology.

Report problems in the [issue tracker](https://github.com/JasperVoll/iamlab-slicer-documentation/issues). See [About]({{ '/docs/about/' | relative_url }}) for contact details, the team, and publications.
