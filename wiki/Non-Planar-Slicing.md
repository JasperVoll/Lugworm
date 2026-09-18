# Non-Planar Slicing

IAMLab Slice uses `mode = 0` for planar layers and `mode = 1` for conical
layers with a vertical, non-tilting nozzle. `mode = 2` slices planar and warns.
After adaptive or conical slicing, enable Generator's `varh` group: actual
vertical gaps can differ from nominal height, making extrusion approximate
without variable-height flow.

## Conical mode (`mode = 1`)

Set `coneAngle` (typically 15° to 25°) for conical layers. Improved bead alignment
does not certify support or collision-free printing. `nozzleClear` guards
clearance: if `info` warns, reduce `coneAngle`. Raising `nozzleClear` can hide
a real collision risk.

<!-- IMAGE TODO: side-by-side viewport: the same dome sliced planar and
     conical, contour colours visible. -->
![Planar and conical slicing of the same part](images/nonplanar-conical.png)

## Adaptive layers

In planar mode, Adaptive chooses the largest gap between `hMin` and `hMax`
allowed by face inclinations within that gap. Shallow/overhanging regions get
thinner layers; steep regions can get thicker ones. Global horizontal caps are
ignored; internal horizontal ledges force `hMin`. Small features affect spacing,
which does not establish printability. Each object is calculated independently,
with shared horizontal heights within it; `firstLayer` still applies.

Enable `varh`; set `varhMin` no higher than `hMin / nominalHeight` and `varhMax`
no lower than `hMax / nominalHeight`. Match nominal height to the first-layer/base
extrusion setting. Local flow scaling does not prove support or bridge stability:
manually inspect near-horizontal surfaces and bridges and run a controlled test print.

## Check printability

Run IAMLab Overhang to measure bead overhang per segment. It colours results with
a continuous magma scale or safe/warning/critical colours. Calibrate placeholder
thresholds using a stepped-cone print in your material. Overhang analysis cannot
decide arbitrary printability or guarantee a physical print.

Adaptive spacing is a geometric heuristic, not validation of a published method.

<!-- IMAGE TODO: Overhang component viewport output on a twisted vase:
     continuous magma colours with the legend visible. -->
![Overhang analysis on a non-planar part](images/nonplanar-overhang.png)
