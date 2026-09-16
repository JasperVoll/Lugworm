# Non-Planar Slicing

IAMLab Slice has two modes: `0` planar (flat layers, the default) and
`1` conical (layers shaped like cones). Conical assumes a nozzle that
never tilts: the layers bend, the tool stays vertical.

A third mode, the heat method (`mode = 2`), was retired in 4.0.0: it
emitted perimeter rings only and fell back to planar on most real
meshes. An old definition that still carries `mode = 2` slices planar
and reports a warning.

After slicing in adaptive or conical mode, enable the `varh`
group (variable layer-height flow) on the G-code Generator. In these
modes the required vertical spacing can differ from the nominal generator
height; without `varh` the amount of extruded material is only approximate.

## Conical mode (`mode = 1`)

Conical mode tilts the slicing planes into cones (`coneAngle`,
typically 15° to 25°), which can improve bead-on-bead alignment for some
shapes. It does not certify support or collision-free printing. The
`nozzleClear` input is a collision guard. If `info` warns that the cone
exceeds the nozzle clearance, reduce `coneAngle`; raising `nozzleClear`
can hide a real collision risk.

<!-- IMAGE TODO: side-by-side viewport: the same dome sliced planar and
     conical, contour colours visible. -->
![Planar and conical slicing of the same part](images/nonplanar-conical.png)

## Adaptive layers

The Adaptive group is available in planar mode. For each candidate layer
interval, Slice checks the worst active face inclination inside that interval
and chooses a height between `hMin` and `hMax`: shallow or overhanging surface
regions receive thinner layers, while steep regions can use thicker layers.
Horizontal global caps are ignored, but internal horizontal ledges force the
minimum height. A small feature therefore influences the geometric height
decision instead of being averaged away by coarse slope sampling; this says
nothing about whether the feature is printable.
The chosen gap is the largest one allowed by the faces it actually reaches.
On a dome, this avoids applying the crown's thin spacing to the lower layers
simply because the crown lies within a maximum-height lookahead.
The calculation is independent per object, with one shared horizontal layer
height for each object. `firstLayer` behavior is preserved.

Enable the G-code Generator's `varh` group after adaptive slicing. Set
`varhMin` no higher than `hMin / nominalHeight` and `varhMax` no lower than
`hMax / nominalHeight`; use the same nominal height as the first-layer/base
extrusion setting. Variable-height flow scales extrusion locally, but does not
prove support or bridge stability. Near-horizontal surfaces and bridges still
need manual inspection and a controlled test print.

## Checking printability

Run IAMLab Overhang on the result. The component measures, for each
segment, how far the bead hangs out over the bead below, and colours
the segments with the configured continuous magma scale (or the safe,
warning, and critical colours). On non-planar layers, this
bead-on-bead overhang is one input to a printability review; it cannot decide
whether an arbitrary part can be printed.
The default thresholds are placeholders; calibrate them for the
material with a test print of a stepped cone. Overhang output is one analysis
of the toolpath, not a physical printability guarantee.

The adaptive rule here is IAMLab Slicer's own conservative geometric
heuristic; it is not a reproduction or validation of a published method.
For background on adaptive/non-planar toolpath research, see the abstracts by
[Dolenc & Mäkelä (1994)](https://doi.org/10.1016/0010-4485(94)90032-9) and
[Ma & He (1999)](https://doi.org/10.1016/S0924-0136(99)00043-6).

<!-- IMAGE TODO: Overhang component viewport output on a twisted vase:
     continuous magma colours with the legend visible. -->
![Overhang analysis on a non-planar part](images/nonplanar-overhang.png)
