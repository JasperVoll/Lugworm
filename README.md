# Lugworm

A Grasshopper plugin for slicing and controlling clay and cementitious-material 3D printing in Rhino 8.

Lugworm is named after the lugworm (German: Wattwurm) of the Wadden Sea. It lives in a U-shaped burrow, swallows sand at one end and extrudes it at the other, one coil on top of the last, until a small cast stands on the beach.

The plugin was developed at the Institute of Digital and Autonomous Construction (IDAC), Hamburg. It is for researchers and practitioners working with clay, concrete and other liquid-deposition (LDM) materials. The plugin is intended to be compatible with Duet-Board LDM printers but also supports Klipper and Marlin firmware.

**Lugworm** components are divided in seven groups that follow the work from left to right: Slice, Place, G-code, Robot, Analyze, Calibrate, Print. Planned experiments (DoE Design, Test Geometry, RSM Evaluate, Desirability, Pareto Select), toolpath analysis (Overhang, Interlayer Time, Print Stats, Legend), direct machine control (Duet Console, Machine Files, Live View) and slicing (Slice, Place, Print Bed, G-code Generator, G-code Restart, Save G-code) each have a page on the [documentation site](https://jaspervoll.github.io/Lugworm/).

Lugworm is distributed as a packaged Grasshopper plugin and open sourced here.

## Installation

Requirements: Rhino 8 on **Windows**. The `.yak` package bundles the plugin and its dependencies.

- [GitHub Releases](https://github.com/JasperVoll/Lugworm/releases)
- Food4Rhino: plugin page link to be added

Save `lugworm-<version>-rh8_0-*.yak` in a folder of its own, then run PowerShell with `--source` pointing at that folder:

```powershell
& "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\lugworm" lugworm
```

Restart Rhino 8, run the `Grasshopper` command and look for the **Lugworm** tab.

## Documentation

The [documentation site](https://jaspervoll.github.io/Lugworm/) covers [installation](https://jaspervoll.github.io/Lugworm/docs/getting-started/installation/), a [map of how the components connect](https://jaspervoll.github.io/Lugworm/docs/getting-started/component-map/), the [component reference](https://jaspervoll.github.io/Lugworm/docs/components/) and an overview of the [slicing and DoE workflows](https://jaspervoll.github.io/Lugworm/docs/workflows/).

## Issues and contact

Use the [issue tracker](https://github.com/JasperVoll/Lugworm/issues) for bugs, questions and feature requests, or send me an e-mail (Jasper Vollmert) at [jasper.vollmert@tuhh.de](mailto:jasper.vollmert@tuhh.de). Machine control has been tested only on our own machine and if you would like to work on compatibility for yours, feel free to get in touch.

## Citing

When Lugworm contributes to published work, we'd appreciate it if you cite the software using the repository's `CITATION.cff`. The plugin is developed at the [Institute of Digital and Autonomous Construction (IDAC)](https://www.tuhh.de/idac/news), Hamburg University of Technology, by Jasper Vollmert ([ORCID 0009-0001-6245-912X](https://orcid.org/0009-0001-6245-912X)).

## License, safety, no warranty

Source and documentation are under the MIT License, see [LICENSE](LICENSE). TUHH holds the economic rights in the software; Jasper Vollmert is its author. The documentation retains its Jasper Vollmert copyright notice. See the [license page](docs/license.md) for attribution and the German legal context. Vendored and packaged third-party components keep their own licenses: Clipper2 (Boost Software License 1.0), Math.NET Numerics, SkiaSharp, OpenTK and the native Skia library. Rhino and Grasshopper require a licensed Rhino 8 installation for Windows.

Lugworm generates toolpaths and can command real machinery. Check every G-code file and robot program in Robot Simulation, keep the emergency stop within reach and do not leave a running print unattended. The MIT warranty and liability disclaimer applies to the extent permitted by applicable law; see [LICENSE](LICENSE) and the [license page](docs/license.md).
