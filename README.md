# Lugworm

Grasshopper plug-in for paste extrusion: it slices geometry (planar and
non-planar), generates G-code (Marlin / Duet RRF / Klipper) and can drive a
Duet-controlled printer directly over the network. Rhino 8 on Windows.
0.1.0-beta is the first public baseline. Before it the plug-in was developed
privately as IAMLab Slicer (versions 1.x to 4.4.0) and renamed in September 2026.

Lugworm is named after *Arenicola marina*, the lugworm of the Wadden Sea. It
lives in a U-shaped burrow, swallows sand at one end and extrudes it at the
other, one coil on top of the last, until a small cast stands on the beach.
That is the whole plugin in one animal: material goes in, a toolpath comes
out, and the object grows layer by layer.

Robot support is selected, not hard-coded. One **Robot Definition** component
picks the machine and everything robot-dependent reads it from one wire.
Slicing, placement, extrusion, G-code and layer preview work with no robot
selected at all, so the plug-in is usable as a plain slicer.

Developed by Jasper Vollmert at the Institute of Digital and Autonomous
Construction ([IDAC](https://www.tuhh.de/idac)), Hamburg University of
Technology.

Documentation site: <https://jaspervoll.github.io/Lugworm/> (installation,
component map, component reference, workflows). Packages:
[GitHub Releases](https://github.com/JasperVoll/Lugworm/releases); the
Food4Rhino listing follows. Code, issues and pull requests:
<https://github.com/JasperVoll/Lugworm>.

## What it does

- Planar, adaptive and conical (non-planar) slicing for overhang-aware
  toolpaths; interlocking walls (bricklayer), sine and graph-drawn wave infill,
  non-planar infill.
- G-code for Marlin, Duet RepRapFirmware and Klipper with collision-safe
  travels, retraction modes, pump extrusion and mid-print restart.
- Robot profiles (none, generic Cartesian, generic Duet Cartesian, Potterbot
  SCARA v4, generic Klipper, generic Marlin); **Robot Simulation** poses the
  model and validates reach, joint limits and arm mode against the firmware's
  own choices. Adding a robot is one catalog entry, see `docs/ARCHI.md` §12.1.
- Machine control over the Duet `rr_*` HTTP API: connect, home, jog, upload,
  print and monitor from the **Duet Console**, with fail-closed safety
  gates. Klipper is a G-code target only.
- Analysis: overhang classes, time between deposited layers (from estimated
  timing or a recorded print log), print statistics, and a legend that reads
  its scale straight off the analysis that painted it.
- Settings files: any component saves and loads its inputs as one JSON file;
  **Load Settings** points a document at a folder of presets.
- DoE calibration: plan a run series, fit response surfaces, optimise with
  desirability functions and pick a recipe on the trade-off front.

## Where to start

| I want to… | Read |
|---|---|
| Install the plugin | [INSTALL.md](INSTALL.md), packages on [GitHub Releases](https://github.com/JasperVoll/Lugworm/releases) |
| Upgrade an existing installation (including from IAMLab Slicer) | [UPGRADE.md](UPGRADE.md) |
| Learn the plugin from zero | [Beginner's guide](docs/user-guide/beginner-guide.pdf) ([md](docs/user-guide/beginner-guide.md)) |
| Practice with guided exercises | [Exercises](docs/user-guide/exercises.pdf) ([md](docs/user-guide/exercises.md)) |
| Look up parameters and technical detail | [Reference](docs/user-guide/reference.pdf) |
| Run a print from one window | [Duet Console](docs/user-guide/print-console.md) |
| Inspect timing between deposited layers | [Interlayer Time](docs/user-guide/interlayer-time.md) |
| Explain a colour scale in the viewport | [Legend](docs/user-guide/legend.md) |
| Keep material or machine presets on disk | [Settings files](docs/user-guide/settings-files.md) |

Technical references live in `docs/reference/` (pump extrusion, Duet control,
DoE calibration, legacy ram calibration). For development see
[RUNNING_IN_VS.md](docs/dev/RUNNING_IN_VS.md), [TESTING.md](docs/dev/TESTING.md),
[RELEASING.md](docs/dev/RELEASING.md) and [docs/ARCHI.md](docs/ARCHI.md), the
architecture ground truth to read before changing code. Version history:
[docs/2-changelog/changelog_table.md](docs/2-changelog/changelog_table.md).

## Issues, contributions and contact

Use the [issue tracker](https://github.com/JasperVoll/Lugworm/issues) for bugs,
questions and feature requests. [CONTRIBUTING.md](CONTRIBUTING.md) explains how
to build, test and open a pull request, and how merged changes flow into the
next release. Machine control has been tested only on our own machine; if you
want to work on compatibility with yours, get in touch: Jasper Vollmert,
[jasper.vollmert@tuhh.de](mailto:jasper.vollmert@tuhh.de).

## Citing

When Lugworm contributes to published work, please cite the software using the
repository's `CITATION.cff` (Jasper Vollmert,
[ORCID 0009-0001-6245-912X](https://orcid.org/0009-0001-6245-912X), Institute of
Digital and Autonomous Construction, Hamburg University of Technology).

## Safety

This software generates toolpaths and can command real machinery. Validate
G-code and robot programs before running them, keep the emergency stop within
reach, and never leave a running print unattended. The rendered robot models
are approximate; the kinematic checks, not the meshes, are the safety
mechanism. See [duet-control.md](docs/reference/duet-control.md) for the safety
model of the machine-control components.

## License

[MIT](LICENSE), © 2026 Hamburg University of Technology (software) and Jasper
Vollmert (documentation). Bundled third-party components are
listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) (Clipper2, Boost
1.0; Math.NET Numerics, MIT). Attribution details and the German legal context:
[license page](https://jaspervoll.github.io/Lugworm/docs/license/).

Jasper Vollmert, jasper.vollmert@tuhh.de
Institute of Digital and Autonomous Construction (IDAC), Hamburg University of Technology
