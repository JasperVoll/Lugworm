# IAMLab Slicer

A Grasshopper plugin for slicing and controlling clay and cementitious-material 3D printing in Rhino.

## What it is and who it is for

"I am a laboratory slicer." IAMLab Slicer grew out of a materials laboratory, where repeatable test specimens and controlled experiments matter more than production throughput. It is intended for researchers and practitioners working with clay, concrete, and other liquid-deposition materials.

The plugin brings the workflow into Grasshopper: plan experiments, create and slice specimens, vary parameters, generate G-code, review robot motion, and operate supported Duet machines. Planar, adaptive, and conical slicing cover both conventional and non-planar work.

IAMLab Slicer is distributed as a packaged Grasshopper plugin. This repository provides the releases, the documentation site, and the issue tracker.

## What's in it

### Testing protocols and design of experiments

The calibration workflow is built around planned tests. **DoE Design** creates a series of test prints; **Test Geometry** provides repeatable sample shapes; and **RSM Evaluate** fits measured results. **Desirability** recommends settings from the fitted models, while **Pareto Select** exposes trade-offs and keeps the selected recipe with the Grasshopper document. **Calib Points** and **Scara Fit** support measurement-based calibration of the laboratory SCARA.

### Parameters along the toolpath

The rewritten **Legend** component presents continuous ramps, thresholded classes, named groups, or size scales. **Graph Mapper** passes parameter values through a curve drawn directly on the component. Together they support readable, controlled parameter changes along a toolpath.

### Direct machine control

**Machine Console** controls Duet 3 machines from inside Grasshopper over WiFi. It covers connection, homing, jogging, job checks and uploads, print control, commands, calibration positions, and logs. **Machine Files** manages files on the connected machine, and **Live View** shows the bed, arm, nozzle position, and deposited bead while a job runs.

### Slicing, placement, and G-code

**Slice** creates planar, adaptive, and conical layers. **Place** positions layers on a **Print Bed** and sets their seams. **G-code Generator** produces Marlin, Duet RepRapFirmware, or Klipper G-code; **G-code Restart** prepares a file for continuing an interrupted print; and **Save G-code** writes the result without silently overwriting an earlier file. **Print Stats**, **Overhang**, and **Interlayer Time** help review the planned print.

### Robots and project support

**Robot Definition** selects the machine profile and coordinate frame. **Robot Simulation** previews a job and checks SCARA reach, joint limits, and speeds before hardware is run. **Load Settings** restores saved component settings, and **Version** reports the installed plugin and runtime information.

## Installation

**Requirements: Rhino 8 on Windows. Nothing else to install.** The released `.yak` package includes the plugin and its dependencies.

- [GitHub Releases](https://github.com/JasperVoll/iamlab-slicer-documentation/releases)
- **Food4Rhino:** plugin page link to be added

Save `iamlab-slicer-<version>-rh8_0-*.yak` in its own folder. Then run PowerShell, pointing `--source` to that folder:

```powershell
& "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\iamlab" iamlab-slicer
```

Restart Rhino 8, run the `Grasshopper` command, and look for the **IAMLab** ribbon tab with the groups **1 Slice**, **2 Place**, **3 G-code**, **4 Robot**, **5 Analyze**, **6 Calibrate**, and **7 Print**.

## Documentation

The [IAMLab Slicer documentation site](https://jaspervoll.github.io/iamlab-slicer-documentation/) covers installation, a map of how the components connect, component references, calibration, and machine control.

- [Installation](https://jaspervoll.github.io/iamlab-slicer-documentation/docs/getting-started/installation/)
- [Component Map](https://jaspervoll.github.io/iamlab-slicer-documentation/docs/getting-started/component-map/)
- [Components](https://jaspervoll.github.io/iamlab-slicer-documentation/docs/components/)
- [About](https://jaspervoll.github.io/iamlab-slicer-documentation/docs/about/)

## Issues and contact

Use the [issue tracker](https://github.com/JasperVoll/iamlab-slicer-documentation/issues) for bugs, questions, and feature requests. For direct contact, email Jasper Vollmert at [jasper.vollmert@tuhh.de](mailto:jasper.vollmert@tuhh.de).

## Citing

When IAMLab Slicer contributes to published work, please credit Jasper Vollmert ([ORCID 0009-0001-6245-912X](https://orcid.org/0009-0001-6245-912X)), the [Institute of Digital and Autonomous Construction (IDAC)](https://www.tuhh.de/idac/news), Hamburg University of Technology.

## License and third-party notices

IAMLab Slicer is licensed under the MIT License. The distributed package also includes or depends on third-party components under their respective licenses, including Clipper2, Math.NET Numerics, SkiaSharp, and OpenTK. Rhino and Grasshopper require a licensed Rhino 8 installation.

## No warranty

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

This is the warranty disclaimer of the MIT license under which IAMLab Slicer is released; it is repeated here because the software commands real machinery. Nothing is warranted, including that the software is useful or fit for your purpose, and no responsibility is accepted for the firmware connection or for any problem with a robot, printer, or material, including damage, injury, or lost work.

Machine control has been tested only on our own machine. Treat every other machine as untested. Check every G-code file and robot program in Robot Simulation, keep the emergency stop within reach, and do not leave a running print unattended.

Direct contact is welcome if you would like to work with us on compatibility for your machine: [jasper.vollmert@tuhh.de](mailto:jasper.vollmert@tuhh.de).
