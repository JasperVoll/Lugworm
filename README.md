# Lugworm

Named after the lugworm (German: Wattwurm), which casts coiled strings of sand that look like extruded layers.
Lugworm is a Rhino 8 plugin for clay and cementitious-material 3D printing.

Developed by Jasper Vollmert at the Institute of Digital and Autonomous
Construction ([IDAC](https://www.tuhh.de/idac)), Hamburg University of
Technology.

Documentation site: <https://jaspervoll.github.io/Lugworm/> (installation,
component map, component reference, workflows). Packages:
[GitHub Releases](https://github.com/JasperVoll/Lugworm/releases). Code, issues and pull requests:
<https://github.com/JasperVoll/Lugworm>.

## Functions

 The plugin is designed for direct Duet-machine control directly in Grasshopper and includes:
- a Slicer and G-code generator including non-planar support and the bricklayer algorithm
- Direct control and connection to Duet-board machines
- A reworked legend component and extended graph mapper capabilities
- A Design of experiments toolbox

For further information see documentation.

## Issues, contributions and contact

Use the [issue tracker](https://github.com/JasperVoll/Lugworm/issues) for bugs,
questions and feature requests. [CONTRIBUTING.md](CONTRIBUTING.md) explains how
to build, test and open a pull request. Machine control has been tested only on our own machine; if you
want to work on compatibility with yours, get in touch: Jasper Vollmert,
[jasper.vollmert@tuhh.de](mailto:jasper.vollmert@tuhh.de).

## Citing

When Lugworm contributes to published work, we'd be happy if you cite the software using the
repository's `CITATION.cff` (Jasper Vollmert,
[ORCID 0009-0001-6245-912X](https://orcid.org/0009-0001-6245-912X), Institute of
Digital and Autonomous Construction, Hamburg University of Technology).

## Safety

This software generates toolpaths and can command robotics. Validate
G-code and robot programs before running them, keep the emergency stop within
reach, and never leave a running print unattended. The rendered robot models
are approximate; the kinematic checks, not the meshes, are the safety
mechanism.

## License

[MIT](LICENSE), © 2026 Hamburg University of Technology (software) and Jasper
Vollmert (documentation). Bundled third-party components are
listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) (Clipper2, Boost
1.0; Math.NET Numerics, MIT).
[license page](https://jaspervoll.github.io/Lugworm/docs/license/).

Jasper Vollmert, jasper.vollmert@tuhh.de
Institute of Digital and Autonomous Construction (IDAC), Hamburg University of Technology
