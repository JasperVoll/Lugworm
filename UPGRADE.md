# Upgrading Lugworm (existing installs → current version)

For PCs that **already have a previous version** installed. Fresh machine? Use
[INSTALL.md](INSTALL.md) instead.

**Golden rule: never mix versions.** Grasshopper loads whatever it finds — an old
`IAMLabSlicer.gha` or `Lugworm.gha` left behind next to a new one causes duplicate-component errors or
silently loads the old build. Remove the old version completely first.

---

## Unreleased

**IAMLab Slicer is now Lugworm (0.1.0-beta, the first public baseline; the private development line ran to 4.4.0).** The Yak package is `lugworm`, the Grasshopper tab
is **Lugworm**, and component names drop the old prefix (**Robot Simulation**, not
**IAMLab Robot Simulation**). **Uninstall IAMLab-Slicer before installing Lugworm** (Rhino's
Package Manager, or `yak uninstall iamlab-slicer`): Yak treats Lugworm as a new package, and
two plugins carrying the same assembly GUID cannot load side by side. Saved `.gh` documents
open unchanged, because components resolve by GUID and no GUID moved. Settings files now
save as `*.lwset.json`; existing `*.iamset.json` files load as before and their payload
format is unchanged. Job logs and console settings move once from
`%LOCALAPPDATA%\IAMLabSlicer` to `%LOCALAPPDATA%\Lugworm` on first use; if the move is
refused, the old folder stays in use. Namespaces, assembly names (`Lugworm.gha`,
`Lugworm.Core.dll`) and the source repositories follow the new name.

**Machine Console is Duet Console again.** The console component (`…0029`, nickname `Console`)
shows **Duet Console** on the ribbon and in its window title, as it did in 3.0.0. The Moonraker
transport that made it machine-generic in 4.0.0 was removed in 4.1.0, and only Duet boards are
controlled directly. Marlin and Klipper machines remain file targets of the G-code Generator.
Nothing else moved: the GUID, every socket and wire, the `duetprintconsole` settings key and
the saved window settings are untouched, and a placed component simply displays the new name.
**Machine Files** keeps its name.

**Legend palettes: 35, with numbers.** The Legend's palette registry is cut from 147 to 35
palettes chosen for engineering data, and each answers to a fixed number (0 to 34) as well as
its name: `scheme` accepts `coolwarm` or `14`. The full list is the
[palette table](docs/reference/legend-palette-table.md). Removed: all ggsci palettes (`npg`,
`lancet`, `nejm`, …), 18 matplotlib maps (`YlOrBr`, `RdPu`, `Pastel1`, `Set3`, …) and 8 R
palettes (`R3`, `R4`, `ggplot2`, `heat`, `terrain`, `topo`, `cm`, `gray`). A document that
names a removed palette draws blues with a warning; pick a kept one. The old aliases move to
the nearest kept ramp, so their colours shift slightly: `pinks` is now `Purples`, `ambers` is
`Oranges`, `gray`/`grays` is `Greys`. An existing value list keeps its old 148 entries; delete
it and press `⇤ inputs` for the new grouped list. Every kept palette's colours are unchanged.

**Zigzag infill joins its rows along the boundary.** Where the straight join between two
rows would leave the region (a concave edge, or the side of a hole), the zigzag now follows
the infill region's boundary, one wall distance inside the last wall, as the wave infill
already did, instead of starting a new path with a travel. Only short detours are taken: a
join that would wrap most of the way around a hole still starts a new path, because every
row through that hole would otherwise lay its bead over the same edge. The wave infill got
that limit too. Expect fewer, longer infill paths and fewer travels; separate islands stay
separate.

**Pump mode is pump-agnostic.** The G-code Generator's `pump` group no longer names a pump
brand, and the 0.5–5.0 l/min rated-range warning is gone. The measured calibration table's
flow range is now the only range check. The placeholder files are renamed
`pump_calibration_placeholder.csv/.xlsx`; your own `pumpCalibCsv` path is unaffected.

**Live View keeps the whole print.** The observed trail no longer drops its oldest points
after about 5,000 observations (roughly 20 minutes of printing). Older stretches are
simplified instead, in steps up to at most 5 mm from what was observed, and `info` says how
far. Only past 20,000 points are the oldest discarded. The pause lift and park (and the
move back) are no longer drawn into the trail; the print logs still record them.

**Duet Console progress shows the total layer count.** The header, Dashboard and Job page
read `42%  layer 12/40`. The total comes from the job's own `; layer N/M` comments, or from
the firmware when the job was started elsewhere; without either it stays `layer 12`.

**Plan augmentation.** DoE Design appends `augment` (`none` or `ccd-face`, default `none`).
With `ccd-face` a `FullFact2` or `FracFact2` plan gains 2m face-centred star runs and centre
runs (`centerPts`, 3 when it is -1) after its original rows, labelled `aug01…`; the original
order is untouched and only the new block is shuffled with `seed`. Other designs report
*augment ignored for this design* and are unchanged.

**Computed responses.** RSM Evaluate appends `computed` (`yes` or `no` per response, a
single value broadcasts, default `no`). A computed response, such as a print time the
slicer estimated, is fitted and enters D and the front like any other but reports no
prediction or confidence interval; the within-PI marker, Pareto Select's ± status line and
the confirmation check skip it. Front rows append one 0/1 `computed` flag per objective;
Pareto Select reads rows and archives with or without them.

**Interior front refinement.** Retained interior Pareto recipes are now locally refined
by augmented weighted Tchebycheff search, capped at 200 evaluations per recipe. The final
front can therefore move off its former grid cells. Pareto Select marks the old selection
stale when the resulting front changes; select again to accept.

**Confirmation runs.** Pareto Select appends the optional `confirm` input group with
`confirmValues` and the `confirmReport` output. Measurements are compared with the selected
recipe's exact PI bounds and retained in its archive. Selecting a different recipe clears
its predecessor's confirmation. Missing intervals are reported, never reconstructed.

**Pareto reliability.** Front rows now append feasibility, predictions, 95% PI half-widths,
exact PI bounds, leverage and an extrapolation flag. Pareto Select reads both old and new
rows and archives. Its window shows response-unit uncertainty below the plot, and hollow
markers identify recipes beyond the training leverage range. Reselect after a front changes.

**Failed DoE runs.** An optional `feasible` measurement column accepts 0 or 1 (blank is 1).
Failed rows are excluded from response fits, while all flags train a separate feasibility
model. Desirability appends `feasibleThreshold` (0.5) and excludes candidates below it before
dominance. Goals are supplied for response models only; the feasibility model needs no goal.

**Response transforms.** RSM Evaluate appends `transform` (`none`, `log1p`, `sqrt`,
`boxcox`), defaulting to `none`. Opting in fits and reports statistics on the transformed
scale; predictions and interval bounds return to response units. Box-Cox requires positive
values and reports its fitted exponent. Existing model JSON without these fields stays untransformed.

**DoE run order and replicates.** An unwired `seed` now defaults to 0 and produces a
reproducibly randomised plan. Wire `-1` to keep the previous standard order. The appended
`replicates` input defaults to 1; larger values repeat every row, including centres,
before shuffling across all replicates.

**Version was removed.** The `1 Slice` component (GUID
`a1a2c3d4-0028-4bcb-9c3d-aa0000000028`) is gone, together with the Core `SemVer` and
`UpdateCheck` classes, the `BuildDateUtc` / `ReleaseManifestUrl` assembly metadata and the
`-ReleaseManifestUrl` switch of `tools/release.ps1`. Its GUID is retired and never reused. A
saved document that contained it opens with a placeholder; delete the placeholder. The
installed version is shown by Rhino's `PackageManager` (the Lugworm entry) and by the
Details tab of the `.gha` file's properties; settings files keep recording it in their
`version` field.

**"Bead" is now "layer" everywhere.** Tooltips, group labels, value-list items, runtime
messages, the Interlayer Time legend (`no layer below`) and the documentation say *layer*
for the extruded strand. Code identifiers followed: `Core/Paths/BeadFrames` is
`LayerFrames`, `PumpPlanInput.BeadWidth` and `BricklayerOptions.BeadWidth` are
`LayerWidth`, and `ExtrusionPreview.BuildBead` / `BeadColor` / `BeadMaterial` are
`BuildLayer` / `LayerColor` / `LayerMaterial`. No socket name changed; Interlayer Time's
`display` value list now reads `layers (extruded structure)`.

**Legend.** The sliders the `⇤ inputs` button creates for `min` and `max` are 0–100
(at 0) and 0–1000 (at 100) instead of ±1 000 000; the unwired fallback is still 0 and 1.
The title sits a full text height above the bar or the swatch rows instead of half a gap.
In the viewport overlay the ticks and marks are drawn as filled rectangles (the 2D line
call left them invisible in Rhino 8) and every label is placed from Rhino's own text
measurement, so the title no longer lands on the bar.

**`⇤ inputs` and settings files.** Objects created for the inputs of a group a settings
file has just switched on are placed beside their sockets; they used to land at the top of
the canvas because those sockets had no layout yet. Applying a settings folder or file now
starts one solution instead of one per component. The four Duet components' `elbow` input
gets a 0/1 slider (default 1) instead of a 0–2 one.

**The SCARA calibration pair was removed.** `Calib Points`
(`a1a2c3d4-0020-4b4b-9c3d-aa0000000020`) and `Scara Fit`
(`a1a2c3d4-0021-4b5b-9c3d-aa0000000021`) are gone from the `7 Print` panel, along with
the Core code behind them (`Calibration/CalibrationSequence.cs`, `ScaraFit.cs`,
`CalibrationReport.cs`). Their GUIDs are retired and never reused.

A saved document that contained either component **opens with a placeholder** where it
used to sit, and everything that was wired downstream of it loses its source. Delete the
placeholder and rewire. Nothing else moved: no other component's sockets changed, and the
Machine Console keeps its `points` input and its go-to selector, so marking points from
any source can still be driven point by point.

The stage-2 kinematic fit those two components performed is no longer provided by the
plugin. `Resources/robot.xml` and `RobotCatalog.PotterbotLimits` still hold the calibrated
`L1`/`L2`; a new fit has to be solved outside the plugin and pasted in the same way
([docs/reference/duet-control.md](docs/reference/duet-control.md), M3).

**Desirability and Pareto Select: the weights moved to where the front is.** Two additive
socket changes, both appended last so no saved wire moves:

| What | Change |
|---|---|
| *(new)* **Desirability `objectives`** output | Index 9, after `pareto`: the active-goal names in model order, without `none` goals. These are the axes of the `pareto` tree. Wire it to Pareto Select's `objectiveNames` instead of typing the names into a panel. It is emitted whether or not the pareto group is on. |
| *(new)* **Pareto Select `weights`** input | After `optimumSettings`: one importance per active goal. A green square marks the recipe that scores best under those weights, computed from the front's own desirabilities, response goals only. The front itself never depends on the weights (it is ordered on the goal desirabilities, which the goal type and bounds define), so this is how you explore a preference on the trade-off without recomputing Desirability. It is a marker, not a selection, and it is not saved with the document. |
| **Pareto Select `front` / `objectiveNames`** and **Desirability `paretoOn` / `pareto`** | Wording only: the tooltips now say that the front's axes are the goal desirabilities and that the weights do not shape it. Names, indices and behaviour are unchanged. |
| **Desirability report** | The Pareto section gains one explanatory line under its heading. Numbers are unchanged. |
| **Desirability `pareto` front endpoints** | Each objective's best recipe is now refined off the search grid by Nelder–Mead on that single goal, so a target goal's extreme sits at the target instead of at the nearest grid cell and the report's *best width at …* names a true local extreme. A front computed on the same models and goals can therefore differ in its endpoint rows from a 4.4.0 front, which Pareto Select reports as *front changed*; select again to accept. Interior rows, D and the weights' role are unchanged. |

---

## Upgrading to 4.4.0

No socket was added, removed or reordered; every 4.1.0 to 4.3.0 document loads with its
wires intact. Four things change what an unchanged document produces, all within the slice
tolerance or in the direction the 4.3.0 notes announced.

**Hollow sections now get a spiral or a Fermat curve.** A section with an outer skin and an
inner skin (an annulus, a vase with an inner wall) used to fall back to independent rings
with the `rings not strictly nested` note because the nesting test compared bounding boxes.
It now tests containment, so such sections print as one continuous spiral or Fermat path
like a solid section does. A document that relied on the ring fall-back sees a different
toolpath; solid sections are unchanged.

**Offset rings carry fewer vertices.** Every wall and infill ring coming out of the offset
is thinned to `min(0.02 mm, spacing / 200)`; no vertex moves more than a hundredth of a
millimetre, but hole-side rings that used to grow into thousands of points now stay at the
outer ring's count. G-code files are smaller and previews faster; the printed shape is the
same within the slice tolerance.

**Bricklayer now interlocks round parts.** On a cylinder, cone or vase the adjacency test
missed every wall pair by a few micrometres, so `brick` reported `0 wall loop(s) raised`
and the walls stayed flat since 4.1.0. They now shift as the rectangle fixtures always did;
a round part printed with `brick` on will look different from the 4.3.0 print of the same
document. The cone tip can still leave a few layers near the apex flat.

**Non-planar paths are thinned after modulation.** `npAmp` and `npWallAmp` output keeps
the same wave within the tolerance with far fewer points (a 200 mm wave line goes from 130
to 10 points where the wave is flat).

Also visible, not affecting output: Graph Mapper, Legend, Desirability, RSM Evaluate and
the Machine Console now report four failures that were silent (an unreadable saved curve,
an unreadable Gradient, a panel that failed to draw, a job log that could not close); the
fallbacks are unchanged, only the message is new.

## Upgrading to 4.3.0

Sockets were only added, none removed or reordered: `Slice` gains three inputs at
the end of its non-planar group (`npWallAmp`, `npLoopPhase`, `npWallOuter`) and its
`wallDistance` / `infillDistance` pair, `G-code Generator` gains `alternateInfill`.
Every 4.1.0 to 4.2.1 document loads with its wires intact. Two things change what an
unchanged document produces.

**Every wall seam moves to the +X side of the section.** Until 4.2.1 a wall started at
whichever section vertex lay farthest from the centre - on a round part a coin toss per
layer, which is why spiral, Fermat and zigzag infill looked different on every layer and
why `Place` in Keep mode showed seams wandering around the part. Every closed
wall now starts where the +X ray from its own centre crosses it, and every infill
pattern starts next to that point. A document that relied on the old accidental seam
(Keep mode, or a printed part whose seam happened to land somewhere useful) will see its
walls start elsewhere; `Align` at 0 deg is the same point, so documents using Align see
no change.

**`Place` now orients open curves in every mode except Keep**, so the infill
start sits on the same side of the part on every layer: an end far from every wall (a
spiral's centre) never starts, the end nearer the layer's seam target starts, a near-tie
follows the layer below. In `Nearest`, all walls seam next to the first open curve's
start. `info` reports `open flipped: n/m`.

**Zigzag and wave infill on round or oblique parts are now one polyline per layer.**
A connector-guard tolerance bug split them into one piece per row (22 travels for 23
rows on a circle); those travels are gone, so such parts print noticeably faster and
with fewer starts.

**Wall waves.** `npWallAmp` waves the inner wall loops with the same period as the
infill, a quarter turn apart per loop by default, so adjacent layers interlock;
`npWallOuter` includes the visible wall. The amplitude limit is the same actual-gap rule
as `npAmp`; with bricklayer on, the guard sees the real gap at the bricklayer ramp
boundary (half a layer) and refuses accordingly.

---

## Upgrading to 4.2.1

No component contract changes since 4.1.0: no socket was added, removed or
reordered by 4.2.1, so every 4.1.0 or 4.2.0 document loads with its wires intact.
Two things do change what you get out of an unchanged document.

**`Test Geometry` (new in 4.2.0)** gains the DIN EN / ASTM bending and
compression specimens, a zig-zag extrusion test path on a new appended `path`
output, and a redrawn icon. The `Shape` enum is append-only, so a saved
selection keeps its shape. See [the 4.2.0 changelog](docs/2-changelog/w10_v4.2.0.md).

**The bricklayer and non-planar groups behave differently on conical and
adaptive slices.** 4.2.1 fixes four defects found in the 4.1.0 slicing code, and
three of them change output rather than only rejecting bad input:

- **Conical slicing now actually shifts bricklayer walls.** Adjacency was
  measured in 3-D on geometry already mapped onto the cone, so on any material
  cone angle neighbouring walls looked further apart than one layer width and
  nothing shifted - the group reported as enabled and did nothing. Adjacency is
  now measured in XY. A conical document that looked flat-walled in 4.1.0 will
  come out interlocked; this is the intended behaviour, not a regression.
- **With adaptive layers, the shift and the amplitude follow the actual layer
  gap.** Bricklayer used one global `brickShift x layerHeight` for every layer,
  and non-planar bounded its amplitude against the nominal `layerHeight` too.
  On an adaptive schedule that let a wall move to or past its neighbour and let
  neighbouring non-planar infill cross. Each layer now uses its own upward level
  gap (the last layer reuses the previous one), and the non-planar amplitude is
  bounded per adjacent pair including both ramp factors. **A non-planar
  amplitude that 4.1.0 accepted on an adaptive stack can now be refused** - the
  message names the actual gap. Lower `npAmp` or raise the local layer height.
  Bricklayer's warning line says "x each local layer gap" when adaptive is on.
- Direct `Lugworm.Core` callers: `Bricklayer.Apply` now rejects a
  `ShiftFraction` that is NaN, infinite or outside `[0, 1]` instead of emitting
  NaN coordinates, and both infill patterns skip null/degenerate boundary rings
  instead of throwing on them. The Grasshopper components already validated
  their own inputs, so nothing changes on the canvas.

**`G-code Restart`'s `point` input wants a Rhino/document point, not raw
Duet machine XYZ.** The socket, the tooltip and the wiki said "Machine XYZ (read
off the Duet)", but the component maps whatever it receives through
`MachineFrame.ToMachine` - so typed-in Duet coordinates were reflected a second
time and could select the wrong restart location on a failed print. **Only the
text changed; the input, its order and its type are untouched**, so existing
wires from a Live View or Machine Console position output were always correct
and stay correct. If you have been typing raw Duet numbers in, convert them
first: Duet `(X, Y, Z)` is Rhino `(-Y, -X, Z)` in millimetres - raw
`(200, -500, 400)` is Rhino `(500, -200, 400)`.

None of this has been printed. The fixes are covered by Core tests only.

---

## Upgrading to 4.1.0

Version 4.1.0 makes the plug-in **LDM-only again**. Thermoplastic (FDM)
printing, the console's Thermals page and the direct Klipper connection are
removed. Read this before opening a 4.0.0 document.

**Klipper is still a supported G-code target.** The `firmware` list keeps
*Klipper*, `KlipperCommands` and the job parser are untouched, and a Klipper
board driving a ram or pump extruder is a machine this slicer feeds - it just
receives its job as a file instead of over the network.

- **The G-code generator's `fdm` group is gone**, with all thirteen of its
  inputs (extrusion model, filament diameter, flow, retraction in filament mm,
  hotend/bed and first-layer temperatures, wait, fan, fan-from-layer, fan boost,
  cooldown), and so is the `filamentMm` output at index 10. A document that had
  the group switched on shows one "unknown feature group" warning on load and
  emits **ram** G-code. If you have a 4.0.0 file that printed thermoplastic,
  keep a 4.0.0 install - 4.1.0 cannot reproduce that output.
- **`Machine Console` lost five outputs**: `hotend`, `hotendTarget`,
  `bed`, `bedTarget` and `fan`. This is the console's only non-append change
  ever, so **re-check its output wires once** after loading a 4.0.0 document.
  `speedFactor` and `flowFactor` stay - they are `M220`/`M221` overrides, not
  temperatures - and the "Temperature outputs" context-menu entry is now
  "Override outputs". Its saved on/off state carries over.
- **The console window has five tabs, not six.** The Thermals page, the
  Dashboard's heater strip, the heat/fan/extrude/tune controls and the "Heat up"
  next-action are gone. No gate changed.
- **Klipper machines can no longer be connected.** `generic-klipper` remains as
  a *dialect* profile (Cartesian, paste, Klipper G-code) but no longer advertises
  machine control, so wiring it into the Machine Console, Machine Files or Live
  View is refused with "use a Duet profile". `generic-klipper-fdm` and
  `generic-marlin-fdm` are **removed**; a saved wire carrying either fails with
  "unknown robot profile". A new `generic-marlin` (Cartesian, paste, Marlin
  dialect, no transport) replaces the Marlin FDM entry.
- **`Robot Definition` keeps its `process` output socket** - it now
  always reports `Paste (LDM)`. Wires to it survive; the value no longer varies.
- **Console settings files move to version 3.** A version-1 or version-2 file
  still loads; its thermal presets are ignored.
- **Mid-print restart no longer re-heats.** `G-code Restart` kept the
  modal settings; the thermoplastic re-heat-and-wait preamble went with the
  filament model.

Everything else in 4.1.0 is additive - three new **Slice** feature groups, all
off by default, so an existing document slices bit-for-bit as it did in 4.0.0:

| New group | What it does |
|---|---|
| **bricklayer** | Shifts adjacent wall loops by half a layer height so the layers interlock. Two loops interlock only when they are within one layer width *and* run alongside each other for more than half the shorter loop. The outermost wall stays flat unless `brickOuter` is set, and `brickRamp` layers stay flat at the bottom and top. **Turn on the G-code generator's `varh` group with it** - the ramp layers otherwise under- and over-extrude, and the component says so when it shifts anything. |
| **wave** | Feeds the two new infill modes, `5 Wave (sine)` and `6 Wave (graph)`: parallel rows that follow a wave, mirrored row to row so their peaks nearly touch. `waveTangent` makes them touch exactly. Mode 6 follows any curve you draw in **Graph Mapper** - wire its new `map` output into Slice's `profile` input. |
| **nonplanar** | Wave-modulates the infill height so successive layers mesh. The amplitude ramps flat at the first and last layers, and an amplitude above 0.4 x the layer height is refused rather than silently clamped. |

**Graph Mapper gains a fourth output, `map`**, appended - existing wires
are unaffected. It carries the curve itself, so the slicer evaluates it at
whatever resolution it needs rather than at a fixed sample count.

**None of the three new groups has been printed.** They are covered by Core
tests only; no shifted wall has been drawn in a Rhino viewport and no wave
infill has been through the G-code generator on hardware. Treat the first run as
a bring-up.

---

## Upgrading to 4.0.0

> **Historical.** Kept so a 3.0.0 document can still be walked forward one step at a
> time. The thermoplastic and Klipper-control parts of this section were **undone by
> 4.1.0** (above), and the two reference documents it linked no longer exist.

Version 4.0.0 retires Universal Robots (UR) support entirely.

- **`UR Program` and `UR Robot` no longer exist.** A document
  that contains either opens with Grasshopper's missing-component
  placeholders. Delete those objects — nothing else in the file is affected.
  Keep a 3.0.0 install if URScript output is still needed.
- **`Robot Definition` no longer accepts `ur5`, `ur10`, or `ur10e`.**
  An `iamlab-robot:v1:ur10e` wire now fails with "unknown robot profile."
- **`Robot Simulation` lost the inputs `toolLen`, `radialYaw` and
  `yawDeg`**, and — since the 2026-09-08 polish — its whole **bed group**
  (`bedWidth`, `bedDepth`, `bedCenter`) and **pose group** (`baseOffset`,
  `armMode`). The plate now comes only from a wired **Print Bed** (or the
  profile's default), and the SCARA arm mode comes only from `robot.xml`'s
  homed pose, the same authority the checks use. Wires to its other inputs
  survive because Grasshopper reconnects by parameter name; a file that had
  either group switched on shows one "unknown feature group" warning on load.
- **`Print Bed` lost its `reach` group** (`reachMin`, `reachMax`).
  Reach belongs to the robot profile; an old bed wire that still carries
  `rmin`/`rmax` parses but the values are ignored.
- **Heat-method slicing (Slice `mode = 2`) is retired**, Core included. A
  saved definition with `mode = 2` slices **planar** and says so in a
  warning; the `heat` input group and its five inputs are gone. Conical
  (`mode = 1`) is unchanged.
- **Definition wires are named the same on both ends.** Print Bed's first
  output is now `bed` (was `definition`), Robot Definition's first output is
  `robot` (was `definition`) and its input is `preset` (was `robot`); Robot
  Simulation's outline output is `outline` (was `bed`). Renamed outputs keep
  their index, so existing wires stay attached.

Everything else in 4.0.0 is additive. What you will notice:

| You had | What happens now |
|---|---|
| **Duet Console** on the canvas | Renamed **Machine Console** (nickname `Console`). Only the ribbon name and the nickname changed — the GUID, every socket and every wire are untouched, and the component you already placed simply displays the new name. |
| **Duet Files** on the canvas | Renamed **Machine Files**, same GUID, same sockets, same wires. It now also accepts a Klipper (`generic-klipper*`) profile and manages files under Moonraker's `gcodes` root. |
| **Duet Live** on the canvas | Renamed **Live View**, same GUID. Also accepts a Klipper profile. Its sockets did change: see the three Live View rows near the end of the table below (no extrusion preview, no print path log, a new `trail` output). |
| **Duet Connect / Status / Jog / Terminal / Print** | Unchanged, still hidden, still loaded, still **Duet-only**. They were not renamed and do not speak Moonraker. |
| Machine Console **output wires** | Seven outputs were **appended** at indices 20–26: `hotend`, `hotendTarget`, `bed`, `bedTarget`, `fan`, `speedFactor`, `flowFactor` (`-1` = the machine does not report that value). Appending moves nothing, so every existing wire on outputs 0–19 stays attached. |
| The console window | Gains a **Thermals** page (index 5 on the rail) on a machine that reports heaters, or under a thermoplastic profile — a clay machine's window is unchanged. The Dashboard gains a heaters strip on such a machine, and its next-action button offers **Heat up** before Start when a thermoplastic hotend is cold. No gate changed. |
| **G-code Generator** with `firmware` unwired | **Unchanged output.** The `firmware` list gained *Klipper* and, left unwired, now follows the robot definition's default flavour — but an unwired `robot` input still resolves to the Potterbot, whose default is **Duet RRF**, and every profile that existed before 4.0.0 is still Paste + Duet RRF. Saved files therefore keep emitting Duet RRF with the ram extrusion model, byte for byte. |
| **G-code Generator** context menu | New **Thermoplastic / filament extrusion inputs** (`fdm`) group: extrusion model, filament diameter, flow, retraction in filament mm, hotend/bed and first-layer temperatures, wait, fan and cooldown. Off by default. A new `filamentMm` output is appended at index 10. **Removed again in 4.1.0.** |
| **Robot Definition** output wires | Two outputs **appended**: `process` (Paste / Thermoplastic) and `flavor` (the default G-code dialect). Existing wires are unaffected. Three profiles were added: `generic-klipper`, `generic-klipper-fdm`, `generic-marlin-fdm` (aliases `klipper` / `moonraker`, `klipperfdm`, `marlin` / `marlinfdm`). |
| **Interlayer Time** with a `layer` slider | The slider now **also cuts the emitted trees** — `perLayer`, `times`, `path` and `colors` are trimmed to the visible layers, so what you measure downstream matches what you see. `report`, `layers` and `info` still describe the whole print. Nothing is recomputed by scrolling. |
| **Interlayer Time**'s viewport legend | **Gone.** The `legend` *input* and its painter were replaced by a `legend` *output* carrying the scale. Wire it into the new **Legend** to place a legend. The same output was added to **Overhang**. New: a `shade` input, defaulting to *alternate layers*, which tints every other layer so adjacent layers stay distinguishable; `colors` is unaffected by it. |
| **Graph Mapper** Spline curves | Points now carry tangent handles — hover an anchor to reveal them, drag to keep the point smooth, Shift to move one side and make a corner, Ctrl to snap the slope to quarters, double-click a handle to return the point to automatic. **Saved files stay byte-identical until you actually set a handle** — until then a 2.5.0-era file and a 4.0.0 one are the same text — after which one trailing `h=` token carries the handles. A handle list that does not match the point list is dropped rather than treated as an error. |
| Repeating the same inputs across files | New: **settings files**. Any Lugworm component can *Save settings…* / *Load settings…* as one JSON file, and the new **Load Settings** component (`1 Slice`) points a document at a folder so every `⇤ inputs` button builds from your presets. Loading never overwrites a wired component — see [Settings files](docs/user-guide/settings-files.md). |
| *(new)* **Legend** | New `5 Analyze` component: a legend for any data range, wired from Overhang / Interlayer Time or described by hand, with about 150 palettes (matplotlib, R, ggsci) and a `gradient` input that reads Grasshopper's Gradient component. See [Legend](docs/user-guide/legend.md). |
| **Overhang** colours | The three classes are now **magma** hues (deep purple / red-orange / light orange) instead of green / amber / red, and a new `colorMode` input offers a **continuous magma ramp** by angle; a `colors` output is appended at index 5. The `legend` wire follows whichever mode is active. |
| **Overhang** reference | A new `global` toggle (off by default) classifies by the **world-Z** angle (the lean against gravity) instead of the **local** layer-normal angle (the layer-on-layer stagger); the viewport, `colors`, the `summary` percentages and the `legend` title follow it, while `angles` and `anglesWorld` always carry both readings. The two new 4.0.0 inputs sit **after** `tol`, which keeps its 3.0.0 socket, so a 3.0.0 file with `tol` wired still reads. |
| **G-code Generator** with a Klipper definition and `firmware` wired to Duet-RRF or Marlin | A **warning** now names the mismatch (Moonraker refuses that dialect at upload). The reverse — Klipper dialect on a Duet definition — warns too; with the robot unwired it is a remark. |
| **The console window** text | Labels start with a capital, the header's job-log badge reads **Log: …** and opens the Logs page when clicked, and the logging texts say where a kept job goes (a local buffer first, then the folder you choose under *Change location*). |

**Klipper machine control was never run on hardware, and 4.1.0 removed it** rather
than shipping an unverifiable transport. The Klipper *dialect* stays: generate the
G-code and transfer the file yourself. The Potterbot regression for 4.0.0 is still
owed.

The existing 3.0.0 upgrade notes below still apply if you are upgrading from
a pre-3.0.0 build.

---

## 1. Close Rhino

Rhino locks the plug-in files while running. Close **all** Rhino instances.

## 2. Remove the old version

**If you installed via Yak** (the normal way):

```powershell
& "C:\Program Files\Rhino 8\System\Yak.exe" uninstall lugworm
```

**If you (also) copied files by hand at some point**, check the manual folder and delete any
Lugworm leftovers there:

```powershell
Get-ChildItem "$env:APPDATA\Grasshopper\Libraries" | Where-Object Name -match "Lugworm|MathNet|Clipper2"
```

Delete what that lists (`Lugworm.gha`, `Lugworm.Core.dll`, `MathNet.Numerics.dll`,
`Clipper2Lib.dll`, and from 3.0.0 also `SkiaSharp*.dll`, `OpenTK*.dll` and
`libSkiaSharp.dll` / `x64\libSkiaSharp.dll` **if they came from Lugworm** - LunchBox ships
its own SkiaSharp 2.88.6; leave another plug-in's copies alone).

## 3. Install the new version

Save `lugworm-<version>-rh8_0-*.yak` into a folder of its own (e.g. `C:\Temp\lugworm`) and run:

```powershell
& "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\lugworm" lugworm
```

(Manual alternative: unzip the `.yak`, copy everything from `net7.0\` (or `net48\` if
you start Rhino with `/netfx`) into `%APPDATA%\Grasshopper\Libraries\` - the folder as
it is, including the native `libSkiaSharp.dll` in its place - then **unblock**:
`Get-ChildItem "$env:APPDATA\Grasshopper\Libraries" -Recurse | Unblock-File`)

## 4. Start Rhino → Grasshopper, verify

The **Lugworm** tab should show **seven** groups:
**1 Slice - 2 Place - 3 G-code - 4 Robot - 5 Analyze - 6 Calibrate - 7 Print**
(older builds had fewer — if you see four or six, the old build is still loading: redo step 2.)

---

## What changes in your existing .gh definitions

Old files open and work, with these deliberate exceptions:

| You had | What happens now |
|---|---|
| Slicer **mode = 2 (heat)** from any earlier build | Heat mode was removed in 1.5.0, reintroduced as the slope-capped non-planar mode, and **retired for good in 4.0.0**. The object slices planar with a warning; wires to the old heat inputs are dropped. |
| G-code header parsing downstream | Header markers are now plain `; --- BEGIN/END HEADER ---` (no ClayGcode/version line) and include `G28`. Adjust anything that scraped the old header text. |
| Right-click "create value list" menu items | Removed — use the **`⇤ inputs`** button on the component instead (creates value lists, sliders, toggles, panels in one click). |
| `ramDiameter` left at default | Default changed **15 → 94 mm** (Lugworm ram). Explicitly wired values are untouched. |
| Fermat infill | Different (correct) path — single spiral starting **and** ending at the boundary seam. |
| **Duet Connect / Status / Jog / Terminal / Print on the canvas** | Still load and still work — they are hidden from the ribbon, not removed, and their GUIDs, sockets and behaviour are unchanged. You can leave them, or move to the **Machine Console** (called *Duet Console* in 3.0.0), which does everything they did in one window. |
| **Print Console** from 2.6.0 | Renamed *Duet Console* in 3.0.0, **Machine Console** in 4.0.0, and given the Connect icon. Its **output** wires 0–7 (`status`, `state`, `progress`, `pos`, `homed`, `jobFile`, `log`, `info`) are unchanged and stay attached; twelve new outputs were appended after them. |
| The Print Console **`open`** input | **Removed.** Press the component's **Console** capsule to open the window, and press it again to bring it to the front. Because removing an input shifts the ones after it, Grasshopper remaps the remaining input wires by position — **re-check the console's input wires once** after upgrading, and delete the now-orphaned wire that fed `open`. The 2.6.0 behaviour of reopening the window on load when it was saved with `open=true` is gone; the window no longer opens by itself. |
| The Terminal **`expert`** toggle | **Removed.** Opening the console's Terminal tab is itself the expert act. The diagnostic allowlist and the two-press confirmation for motion/config/job commands are unchanged. |
| **Reset (M999) from the console** | Now allowed **only while the machine is halted or off**. The Jog component's armed any-state override was not carried into the console; it still exists on the (hidden) Jog component. To recover a wedged-but-not-halted board from the console, press E-STOP first — that halts it — then Reset. |
| **The console window** | Opens on a **Dashboard** now, with a navigation rail (Dashboard - Control - Job - Terminal - Logs) instead of top tabs, larger controls, and a fixed footer with Reset and E-STOP. Nothing about the gates changed. Validate now works offline (it never needed a connection), Start says **Start (dry M37)** when `dryRun` is on, and the go-to list is paged so every calibration point is reachable. |
| **Job logs** | Every job is now recorded automatically to a local buffer (`%LOCALAPPDATA%\Lugworm\PrintLogBuffer\`). A record is **kept** only when you press *Save this job* or switch on *Keep all job logs* on the console's Logs page and choose a folder; unsaved temporary records are removed when Rhino closes cleanly. This is now the **only** job log: Live View's own `log` group is gone, and this one runs whether Live View is on the canvas, switched off or deleted. |
| **Live View** (*Duet Live* in 3.0.0) re-solving the canvas on every poll | Definitions saved before 3.0.0 keep that behaviour, now limited to **at most one re-solve per second**. The viewport itself animates independently of solves, on a 30 fps budget that steps down to 15 and 10 fps when frames get expensive and stops entirely once the pose has settled. New Live View components default to *viewport-only*: their outputs are a snapshot of the last solve - right-click **Automatically update GH outputs** to get the old behaviour. The context menu also has smoothing and an observed-vs-intended deviation overlay (Viridis) for the trail. |
| **Calib Points / Scara Fit** in an **inch or metre** document | Now converted at the document boundary like every other component (they emitted machine millimetres as Rhino points before). Millimetre documents are unchanged. |
| **DoE Design / Desirability** Excel export | Runs in the background now: the `info` output says `exporting...` on the solve that starts it and `saved:` on the next one; a failed write reports once and is retried on the next solve. |
| **Desirability report** `≈best` marker | Renamed **within-PI** and explained as a coverage check, not an equivalence claim; it is withheld when any response has no prediction interval. Numbers and rankings are unchanged. |
| **Live View** `bedWidth` / `bedDepth` / `bedCenter` | **Removed.** The bed is configured in one place now - the **Print Bed** component, wired to `bed` on Live View, Robot Simulation, Place and Calib Points. Opening an older document inserts a Print Bed carrying that component's former width, depth and centre, wires it to `bed`, and says so on the component; wired inputs move across as wires, so the same upstream objects still drive the plate. If `bed` was already wired, the wired bed stays authoritative and the old values are reported as dropped. Check the inserted bed once: a document that relied on a Cartesian robot's 800x800 default plate gets the lab plate's defaults instead. |
| **Calib Points** bed rectangle group (`bedCenter` / `bedWidth` / `bedDepth`) | **Removed** and migrated the same way, into the same shared `bed` input. An unwired `bed` means no bed filter, exactly as switching the old group off did. |
| An **unwired `bed`** on Live View or Robot Simulation | **No plate is drawn**, and the component says "connect a Print Bed component to 'bed'". Neither invents a plate any more - independent default plates were how the two came to show different beds for the same machine. |
| *(new)* Print Bed **`shape`** | New input, appended after `printCenter` so saved socket indices do not move: 0 rectangle (the default, and what every bed saved so far is), 1 ellipse - equal width and depth make it a circle. A rectangular bed still writes `iamlab-bed:v1:...` unchanged; an elliptical one writes `iamlab-bed:v2:...`, which 4.0.0 and earlier refuse to read rather than mistaking it for a rectangle. |
| **Live View** `extWidth` / `layerHeight` | **Removed**, together with the extrusion surface they drew. Live View visualises the toolpath the machine is reporting; a layer preview is what the **G-code Generator** and **Robot Simulation** are for. A saved value is dropped and a wire into either socket is named in one warning on load. |
| **Live View** print path log (`logEnable` / `logEveryN` / `logFolder` / `logPrefix` / `logLayerHeight` / `logExport`, and the `layers`, `log`, `path`, `logInfo` outputs) | **All removed**, including the whole `log` toggle group. Logging lives on the **Machine Console**, which already records *every* job automatically and streams it to the folder you choose on its Logs page - with or without a Live View on the canvas. Opening an older document reports one "unknown Lugworm feature group 'log' was ignored" warning plus a warning naming any of the removed sockets that carried a wire or a value. |
| **Live View** output wires on `layers` / `log` / `path` / `logInfo` | Grasshopper drops a wire to an output that no longer exists, silently. Those four sat at indices 7-10, and index **7 is now the new `trail` output**, so an old wire can end up on `trail` and report a type mismatch downstream. Live View says so once when it opens such a document: **delete those four wires**. |
| *(new)* **Live View** `trail` | New output at index 7: the **observed** toolpath in Rhino coordinates, one branch per continuous run - a polyline, or a single point for a run of one observation. It is never smoothed (the animation's interpolated pose never reaches it), never joined across a break, and never snapped to the intended path; a straight line is also withheld where too much time passed between two observations for the route between them to be known, so sparse samples show as points and gaps instead of chords across the part. Since changelog section 20 that judgement rests on the gap alone: a seam where the machine slows down or pauses to prime no longer breaks the run, and `info` attributes every break it did make (`breaks: continuity N, sparse sample N, ...`). Suspect samples stay in it with their raw coordinates and are counted in `info`. It needs no logging switched on, because there is none left to switch on. |
| **Live View** `gcode` | Still input 2, but now a **fallback**. Whenever the Machine Console has a job, that job's exact bytes are the intended path and the wire is ignored (with a remark if it does not match). A job started outside Rhino - from the machine's own screen - has unknown bytes, so **no** intended path is drawn and `info` says so; the wired `gcode` is never substituted for it. |
| **Start after a print** | A validated job whose first motion is a full `G28` no longer needs a Home all first: the console lets you Start with unhomed axes and an unknown arm configuration because the job homes itself before it moves (decision D9). Jog, Go to and Resume still require homing; a file that does not begin with a full G28 keeps the old requirement. |
| **Live View arm** | Stays drawn through the job's own homing, a cancel and a stale connection - it never vanishes; `info` says `arm pose held: ...` or `STALE` instead. The firmware's single-axis position glitch (one axis of `machinePosition` 15-50 mm off while `userPosition` is right, see changelog section 19) is repaired from the same reply; repaired samples are counted in `info` and flagged `MachinePositionGlitch` in the job log. **Since section 20** the held arm is drawn as a translucent ghost while the pose is uncertain, so "held" and "live" are never confused: the arm keeps its last valid position and `info` names the reason. |
| *(new)* **Live View refuses impossible positions** | The controller sometimes reports coordinates outside its own axis limits - on the lab Potterbot, Z down to -389 mm during the approach while the same reply declares the Z range as 0-400. Live View no longer animates those: it holds the last valid arm as a ghost and `info` says `pose held (uncertain): Z ... below the machine minimum ...` and counts the refusals under `validity:`. **The raw values are still recorded** in the job log and the diagnostics capture - nothing is clamped or corrected - and a machine that reports no axis limits (the legacy RRF API) is never refused on that basis; `info` then says the envelope is UNKNOWN. This contains a *reporting* fault; it does not fix the controller, and it cannot change how the machine moves. |
| *(new)* **Live View draws travel and seams** | The intended path used to show only the extruding moves, and it dropped the first segment of every extrusion run. It now draws the complete commanded geometry: printing runs solid, travel, seam and coast moves dotted in a dimmer neutral, with no line invented across a `G28` where the file itself has none. `info` reports how many printing and travel runs were drawn and, on a very large job, the simplification tolerance used for the display (the comparison against the path still uses the full-detail samples). |
| **Live View trail on a long print** | The observed trail keeps 5,000 points. Beyond that it used to discard the oldest ones, which quietly removed the early layers of a long print. It now compacts older completed runs within 0.2 mm first - the early layers stay visible - and `info` states under `retention:` how many points were compacted and how many were actually discarded. |
| **Live View `info` rates** | The diagnostics block now separates numbers that used to be one: the interval you configured, the interval the session actually targets after its clamp, the measured coordinate-acquisition interval, the coordinate round trip, the whole poll's elapsed time, the overlay interval and how old the newest drawn point is. **A poll interval below 250 ms is still clamped to 250 ms** - it now says so instead of printing your request back. At 40 mm/s one observation every 250 ms is 10 mm of motion, so a coarse trail is the acquisition rate and not the frame rate. |
| **Jog buttons** | Hovering an X, Y or Z jog button now explains what it does: which machine axis moves, by how much, and which way that is in the Rhino frame for your machine (on the SCARA the frame is mirrored, so machine X+ moves toward Rhino -Y). It deliberately does not promise a screen direction - the viewport can be rotated. When jogging is refused, the tooltip says why. The commands the buttons send and press-and-hold are unchanged. |
| **Job log manifests** | `manifest.json` is version 4 and records, per run, how many telemetry rows and diagnostic lines the logging worker had to drop. Older manifests still open; their zeroes mean "this version did not record it", not "nothing was dropped". |
| The nozzle marker and its "sample age" text in Live View | **Gone.** A stale pose is shown by the arm turning into a translucent ghost, and the timing facts (sample age, poll interval, frame cost, redraws, GH solves, parses) are in the `info` output. |
| *(new)* **Pareto Select** | New `6 Calibrate` component: pick a recipe on the Desirability Pareto front graphically; the choice persists by value and is flagged stale when the front changes. |
| **Machine Console** outputs | Grouped into six categories, and a **new** console shows only three sockets: `host`, `status` and `gcode`. To get the others back, right-click the component and use **Outputs shown**: Connection, Job, Motion, Validation, Temperatures, Logs / Diagnostics. Saved documents keep every wire - opening one switches on each category that has a wired output and says so, and a category with a wired socket cannot be hidden. Hidden outputs also stop re-solving the canvas, so a console with Motion and Temperatures hidden no longer updates on every position poll. |
| *(new)* Machine Console **`gcode`** | New output carrying the job that is running, or the one a Start would run - including one loaded from Recent prints. It changes only when the job does, never on a status poll. A print started outside the console (its own screen, another client) publishes an empty list, because its exact bytes are unknown. |
| *(new)* Machine Console **Recent prints** | The Job page lists the last ten runs with name, time, outcome and machine. Every run from now on also archives its exact G-code beside its log, so selecting one loads it - it starts nothing - and **Print again** runs the same Validate / verified Upload / explicit Start flow on those bytes. Runs logged by earlier versions stay listed and are marked *not available for reprint*: they have no archived bytes. |

New since your version (nothing to configure, just there): the **Machine Console** —
one window with connection, homing, press-and-hold jogging, origin capture, job control,
a terminal and go-to — plus Print Stats, the whole **6 Calibrate** DoE workflow
(see [docs/reference/doe-calibration.md](docs/reference/doe-calibration.md)), arctic robot/bed/layer visuals.

---

## Upgrade troubleshooting

| Symptom | Fix |
|---|---|
| Two copies of components / "duplicate component" warnings | Old files still present — redo step 2, check both the Yak package folder (`%APPDATA%\McNeel\Rhinoceros\packages\8.0\lugworm`) and `%APPDATA%\Grasshopper\Libraries`. |
| Lugworm tab still shows 4 groups | Same as above — the 1.1.0 build is loading. |
| No Lugworm tab at all after manual copy | Blocked DLLs — run the `Unblock-File` line from step 3. |
| Infill crashes with a Clipper2 `MissingMethodException` | Fixed in 1.4.1+ — you are still running the old build; redo the upgrade. |

Maintainer contact: Jasper Vollmert — jasper.vollmert@tuhh.de (IDAC @ TUHH).
