# About this sheet

One list per component. Work through a component from top to bottom, close
it, then open the next. Nothing here assumes you have used the plug-in before.

## What is in this sheet

| # | Component | Checks | Machine needed? |
|---|---|---|---|
| 1 | [Slice](#1-slice) | SLICE-01 … SLICE-27 | no, except SLICE-23 … SLICE-27 |
| 2 | [Graph Mapper](#2-graph-mapper) | GRAPH-01 … GRAPH-10 | no |
| 3 | [Test Geometry](#3-test-geometry) | TESTGEO-01 … TESTGEO-10 | no, except TESTGEO-09, TESTGEO-10 |
| 4 | [Overhang](#4-overhang) | OVER-01 … OVER-05 | no |
| 5 | [Interlayer Time](#5-interlayer-time) | TIME-01 … TIME-09 | no, except TIME-09 |
| 6 | [Legend](#6-legend) | LEG-01 … LEG-17 | no |
| 7 | [Load Settings](#7-load-settings-and-saved-settings-files) | SET-01 … SET-16 | no |
| 8 | [Robot Definition](#8-robot-definition) | ROBOT-01 | no |
| 9 | [G-code Generator](#9-g-code-generator) | GEN-01 … GEN-07 | no |
| 10 | [G-code Restart](#10-g-code-restart) | RESTART-01 … RESTART-09 | no, except RESTART-06, RESTART-09 |
| 11 | [Duet Console](#11-duet-console) | CON-01 … CON-18 | yes |
| 12 | [Live View](#12-live-view) | LIVE-01 … LIVE-12 | yes, printing |

Appendix A lists what is already known, so you do not report it twice.
Appendix B maps these checks onto the numbering used before.

**A good order to work in.** Components 1 to 10 need nothing but Rhino, so do
them at a desk first. Components 11 and 12 need the printer switched on. The
checks marked *needs clay* come last, when everything else has passed — they
are the only ones that risk material and machine time.

## Test round record

| | |
|------------------------------|-------------------------------------------------|
| Tester | |
| Date started | |
| Date finished | |
| Plug-in build tested | |
| Rhino version and runtime | |
| Machine used | |
| Material / mix used | |

## Summary — fill in when finished

| Component | Checks | Passed | Failed | Skipped |
|--------------------------------------|--------|--------|--------|---------|
| Before you start | 1 | | | |
| Slice | 27 | | | |
| Graph Mapper | 10 | | | |
| Test Geometry | 10 | | | |
| Overhang | 5 | | | |
| Interlayer Time | 9 | | | |
| Legend | 17 | | | |
| Load Settings, and saved settings files | 16 | | | |
| Robot Definition | 1 | | | |
| G-code Generator | 7 | | | |
| G-code Restart | 9 | | | |
| Duet Console | 18 | | | |
| Live View | 12 | | | |
| **Total** | **142** | | | |

# Before you start

## What the plug-in does

Lugworm is a plug-in for **Grasshopper**, the visual programming editor
inside **Rhino**. It turns a 3D shape into the instruction file (**G-code**)
that a clay or paste 3D printer runs, and it can also talk to the printer while
it prints.

You build a chain of components on the Grasshopper canvas and connect their
sockets with wires. A typical chain is:

`Test Geometry → Slice → Place → G-code Generator`

Each component has **inputs** on its left side and **outputs** on its right
side. Everything in this sheet is either "set this input" or "look at this
output".

## What you need

- A Windows PC with **Rhino 8** and the plug-in installed.
- A mouse with a scroll wheel (some checks need right-click and Shift/Ctrl).
- For the last three components: the printer switched on and reachable on the
  network, plus its IP address and password.
- For the checks marked **needs clay**: the machine loaded and ready to print.

You do **not** need the machine for anything before the *Duet Console*
section. Roughly two thirds of this sheet can be done at a desk.

## How to fill this sheet in

Every check has the same three parts:

- **Set up** — what must be on the canvas and what to type into which input.
- **Do** — the actions, in order.
- **Expect** — what a correct result looks like. If what you see differs from
  this in any way, the check fails.

**Every component section ends with a results table.** Work through that
component's checks, then fill the table in. Write one of these words in the
**Result** column:

| Write | Meaning |
|---|---|
| pass | You saw exactly what **Expect** describes. |
| fail | You saw something else. Say what, in the Notes column. |
| skipped | You could not run it. Say why (no machine, no clay, crashed earlier). |

If a check failed, write one of these in the **Severity** column:

| Write | Meaning |
|---|---|
| blocker | The machine moved wrongly, the program crashed, data was lost, or the G-code is wrong. Stop and report immediately. |
| major | The feature does not work, the numbers are wrong, or a message says something untrue. |
| minor | Wording, layout, colour — annoying but you can work around it. |

**In the Notes column, write:** what you set, what you clicked, what you saw,
and what the `info` output said. If there is not enough room, write "see
sheet 2" and continue on a separate page. A screenshot of the canvas together
with the `info` panel is worth more than a sentence.

## Words used in this sheet

| Word | What it means |
|---|---|
| **Canvas** | The Grasshopper workspace where components are placed and wired. |
| **Socket** | One input or output on a component. You connect sockets with wires. |
| **Panel** | A Grasshopper text box. Drag one out and wire an output into it to read text. Always attach one to `info`. |
| **Slider** | A Grasshopper number slider. Drag one out and wire it into a number input. |
| **Compute button** | A small button in the lower-right corner of some components. Those components do nothing until you click it. Slice, G-code Generator, G-code Restart and Overhang all have one. |
| **Optional group** | A set of extra inputs that is hidden until you switch it on. **Right-click the component** and tick the group in the menu; the extra sockets then appear. |
| **`⇤ inputs` button** | A button on the right-click menu that automatically creates sliders, toggles and drop-down lists for every input of that component, already set to sensible values. The fastest way to fill a component in. |
| **`info` output** | Nearly every component has one. It is the component's own report: what it did, what it refused, and why. Wire a Panel to it and read it. |
| **Layer** | One horizontal (or cone-shaped) slice of the part. |
| **Contour** | The closed curve the nozzle follows around one layer. |
| **Wall** | The outer contour, or contours, of a layer. |
| **Infill** | The pattern that fills the inside of a layer. |
| **Extruded layer** | The strand of material the nozzle actually lays down for one layer of the part. |
| **Seam** | The place where a closed contour starts and ends. |
| **Travel** | A move with the nozzle not extruding, from the end of one path to the start of the next. |
| **Tree / branch** | Grasshopper's way of holding grouped lists. Here, one branch per layer. Do not flatten it: the branches are what identify the layers. |
| **Planar / conical** | Flat horizontal layers, or layers shaped like nested cones. |

## Standard set-ups

Several checks share the same canvas. Build these once and keep them in
separate Grasshopper files, then just reload the one a check names.

### Set-up A — plain slice, no machine

1. Place **Test Geometry** (ribbon panel `6 Calibrate`). Right-click it,
   choose shape **Cylinder**. Set `height = 80`, `radius = 40`.
2. Place **Slice** (`1 Slice`), **Place** (`2 Place`) and
   **G-code Generator** (`3 G-code`).
3. Wire: Test Geometry `geometry` → Slice `geo`; Slice `contours` →
   Place `contours`; Place `moved` → Generator `crvs`.
4. Place **Robot Definition** (`4 Robot`), set its `preset` to `none`,
   and wire its `robot` output into the Generator's `robot` input. This keeps
   the exercise independent of the lab machine's mirrored coordinates.
5. On Slice set `layerHeight = 3`, `firstLayer = 3`, `spacing = 10`,
   `wallCount = 1`, `mode = 0`, `infill = 1`.
6. On the Generator set `layerHeight = 3` and `width = 10` — the Generator does
   not read these from Slice, so they must be typed in twice.
7. Attach a Panel to every `info` output.
8. Click Slice's compute button, wait for the layer count, then click the
   Generator's compute button. **Always in that order.**

You should get 26 layers at Z = 3, 6, … 78 mm.

### Set-up B — cone-shaped layers, no machine

Start from set-up A and change:

- Test Geometry shape to **Frustum**, `height = 100`, `radius = 40`; switch on
  the *Profile detail* group and set `topRatio = 0.6`.
- Slice `mode = 1`; switch on the *Conical* group; `coneAngle = 20`,
  `nozzleClear = 25`.
- Slice `wallCount = 3` so there is more than one wall to look at.

Recompute Slice, then the Generator.

### Set-up C — analysis, no machine

Start from set-up A and add:

- **Overhang** (`5 Analyze`), wired from Slice `contours`.
- **Interlayer Time** (`5 Analyze`), wired from the Generator's `gcode`.
- **Legend** (`5 Analyze`), initially unwired.

### Set-up D — machine connected

- **Duet Console** (`7 Print`): set `host` to the printer's IP
  address, `password` to the printer's password, leave `robot` unwired (that
  means the lab's SCARA arm).
- **Live View** (`7 Print`): wire the Console's `host` output into Live
  View's `host` input, and the job G-code into Live View's `gcode`.
- Attach Panels to `info`, `status` and `state`.

## First look at the ribbon

### SMOKE-01 — Every component shows its icon in the ribbon

**Do.** Open Rhino, open Grasshopper, and look at the **Lugworm** tab in the
ribbon. Expand all seven panels.

**Expect.** Every component shows a small drawn icon, the same size as every
other Grasshopper icon (24 × 24 pixels). No component shows an empty grey
capsule with only a name. The *Test Geometry* icon reads as a three-point
bending rig — a bar on two supports with a load pressing down in the middle.

## Results — Before you start

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| SMOKE-01 — Every component shows its icon in the ribbon | | | |

# 1. Slice

*Panel `1 Slice`. Has a compute button — nothing happens until you click it.*

Slice cuts a shape into layers and produces one branch of curves per layer:
the wall or walls first, the infill after them. Three of its optional groups
are the subject of this section.

- **Bricklayer** lifts every second wall loop by half a layer height, so that
  the layers interlock like brickwork instead of stacking into one continuous
  vertical seam.
- **Adaptive layer height** lets the slicer choose a different thickness for
  each layer — thin where the surface is shallow, thick where it is steep —
  between a lower and an upper limit you set.
- **Wave** and **non-planar infill** make the infill snake sideways, or up and
  down, so that neighbouring rows and neighbouring layers mesh into each other.

Remember: whenever you change anything on Slice you must click **Slice's**
compute button, and then the **Generator's** compute button, in that order.

## 1a. Bricklayer on cone-shaped layers

*Use set-up B. Switch on Slice's **Bricklayer** group (right-click → Bricklayer)
and leave its defaults: `brickShift = 0.5`, `brickOverlap = 0.5`,
`brickOuter = false`, `brickRamp = 1`.*

### SLICE-01 — Bricklayer actually shifts something on a cone-shaped part

**Set up.** Set-up B, bricklayer group on, `coneAngle = 20`, `wallCount = 3`.

**Do.** Click Slice's compute button. Read the `info` panel.

**Expect.** `info` reports a number of shifted paths and a number of shifted
layers, and **both are greater than zero**. A report of `0` shifted paths while
the group is switched on is a failure, even though nothing else looks wrong.

### SLICE-02 — The shift is visible, not just counted

**Set up.** Continue from SLICE-01.

**Do.** Zoom into the wall of the part in the Rhino viewport, from the side, so
that you can see several layers stacked above each other. Look at where each
wall loop sits in height.

**Expect.** The wall loops alternate up and down in a regular pattern, like the
courses of a brick wall. They are neither a flat stack (every loop at the same
height — nothing happened) nor scattered at random heights (something went
wrong). Trust your eyes here, not the count from SLICE-01.

### SLICE-03 — The shift survives a steeper and a shallower cone

**Set up.** Continue from SLICE-01.

**Do.** Set `coneAngle = 5`, compute, note the shifted-path count from `info`.
Then set `coneAngle = 40`, compute, note it again.

**Expect.** Both angles report a healthy number of shifted paths. The count
does **not** shrink toward zero as the angle gets larger.

### SLICE-04 — The outermost wall can be kept flat, or shifted

**Set up.** Set-up B with `coneAngle = 20` and `wallCount = 3`.

**Do.** With `brickOuter = false`, compute and look at the **outermost** wall
from the side. Then set `brickOuter = true`, compute, and look again.

**Expect.** With `brickOuter = false` the outermost wall — the one you would
see on the finished part — stays at a constant height per layer, while the
inner walls alternate. With `brickOuter = true` the outermost wall alternates
too, and the visible surface of the part changes accordingly.

### SLICE-05 — The first and last layers stay flat

**Set up.** Set-up B, `brickRamp = 1`.

**Do.** Compute. Look at the very bottom layer and the very top layer of the
part from the side.

**Expect.** The bottom layer is flat — every loop at the same height, so the
part sits properly on the bed — and so is the top layer. Only the layers in
between alternate.

### SLICE-06 — Switching bricklayer off changes nothing else

**Set up.** Set-up B.

**Do.** Note the layer count and the look of the part with the bricklayer group
on. Right-click Slice and switch the **Bricklayer** group off. Compute again.

**Expect.** The result is an ordinary flat-stacked cone-sliced part: same layer
count, same contours, no leftover height shifts anywhere. Switching the feature
off must return you exactly to the plain result.

### SLICE-07 — The G-code contains the shift, not just the preview

**Set up.** The canvas from SLICE-01, bricklayer on, `coneAngle = 20`.

**Do.** Click the Generator's compute button. Wire the Generator's `gcode`
output into a Panel, or set `savePath` to a file and open that file in a text
editor. Follow the Z values down the file.

**Expect.** The Z heights in the file alternate in the same pattern you saw in
the viewport in SLICE-02. The preview and the file are produced by separate
code, so a correct preview does not prove a correct file.

### SLICE-07a — A plain round part interlocks too (4.4.0, D10)

**Set up.** Set-up B, but switch Slice `mode` back to `0` and switch the
*Conical* group off, so the part is sliced as ordinary flat layers. Change the
Test Geometry shape to **Cylinder**, `height = 100`, `radius = 40`. Set
`wallCount = 5` and leave `wallDistance` at its default. Bricklayer group on
with its defaults.

**Do.** Click Slice's compute button. Read the `info` panel.

**Expect.** `info` reports **two** wall loops raised per layer (five walls
two-coloured from a flat outermost one) on **every** eligible layer - a line of
the shape `bricklayer: N wall loop(s) raised ... on M/M eligible layers` where
neither number is zero and the two M's are equal. Until 4.4.0 a round part
reported `0 wall loop(s) raised on 0/M eligible layers`, or raised loops on only
some layers, because the loop spacing Clipper produces sits a hair outside one
layer width. Then set `wallDistance` to three times its default and compute
again: now the walls really are too far apart to interlock and a report of zero
raised loops is the correct answer.

## 1b. Adaptive layer height, together with bricklayer and non-planar infill

*Use set-up A but change the Test Geometry shape to **Vase**
(`height = 100`, `radius = 40`, Profile group on, `waveAmp = 0.15`,
`waveCount = 3`) so that the surface inclination changes over the height.
Switch on Slice's **Adaptive layer height** group and set `hMin = 1`,
`hMax = 3`.*

Adaptive layers mean the gap between two layers is no longer the same
everywhere. Anything that measures itself against "the layer height" therefore
has to measure against the **local** gap at that place in the part, not against
the single nominal number typed into `layerHeight`.

### SLICE-08 — The bricklayer warning talks about local gaps

**Set up.** The adaptive vase above, plus the **Bricklayer** group switched on.

**Do.** Compute Slice. Read the whole `info` panel.

**Expect.** The line about the bricklayer shift expresses it as a multiple of
**each local layer gap** — wording along the lines of "× each local layer gap".
It does **not** state a single fixed number of millimetres, because there is no
single layer height in an adaptive part.

### SLICE-09 — A thin layer gets a small lift, a thick layer a large one

**Set up.** Continue from SLICE-08.

**Do.** In the viewport, find a place where the layers are visibly thin (close
together) and a place where they are visibly thick (far apart). In each place,
measure the height difference between a shifted wall loop and the loop next to
it — the Rhino `Distance` command between two points on the two loops does the
job.

**Expect.** The lift in the thin region is clearly smaller than the lift in the
thick region, roughly in the same ratio as the two layer gaps. Both regions
getting the same lift is a failure.

### SLICE-10 — No wall is ever lifted into its neighbour above

**Set up.** Continue from SLICE-08.

**Do.** Scan the whole height of the part from the side, particularly wherever
the layer spacing changes quickly.

**Expect.** No shifted wall loop ever reaches the height of the loop above it,
let alone passes it. Every loop still sits clearly below its upper neighbour.

### SLICE-11 — Too much non-planar amplitude is refused, and the message says why

**Set up.** The adaptive vase, **Bricklayer off**, **Non-planar infill** group
on. Slice `infill = 1` (zigzag). Set `npAmp` to `0.4 × layerHeight` — with
`layerHeight = 3` that is `npAmp = 1.2`.

The non-planar infill waves the infill up and down in Z. If it waves further
than about 40 % of the gap to the next layer, two layers would run into each
other, so the component must refuse it. With adaptive layers the smallest gap
in the part is what counts, not the nominal 3 mm.

**Do.** Compute Slice. Read `info`.

**Expect.** The component **refuses** to produce the non-planar infill and says
so, and the message names the **actual smallest local gap** it measured — not
the nominal layer height. Silently accepting the value, or clamping it without
saying anything, is a failure.

### SLICE-12 — Lowering the amplitude until it is accepted gives a sensible limit

**Set up.** Continue from SLICE-11.

**Do.** Lower `npAmp` step by step (try 1.0, 0.8, 0.6, 0.4) and compute after
each step, until the component stops refusing. Note the value that is first
accepted. Compare it with the smallest local gap that `info` reported in
SLICE-11. Then cut a section through the part in Rhino (`Section` command,
vertical plane through the middle) and look at the infill of two neighbouring
layers.

**Expect.** The first accepted value is roughly **0.4 × the smallest local
gap**. In the section, the wavy infill of one layer never crosses into the
layer above or below.

### SLICE-13 — The ramp-in layers do not cause a false refusal

**Set up.** Continue from SLICE-12, with an amplitude that was accepted. Set
`npRamp = 1`.

`npRamp` fades the wave to zero over the first and last layers so that the part
starts and ends flat. Those flat end layers must not be treated as if they had
to carry the full amplitude.

**Do.** Compute.

**Expect.** An amplitude that is valid for the middle of the part is
**accepted**, even though the first and last layer pair is flat. A refusal
caused by the flat ends is a failure.

### SLICE-14 — Ordinary uniform layers did not get stricter

**Set up.** Set-up A (plain cylinder, no adaptive group). Non-planar infill
group on, `npAmp = 1.2` (that is 0.4 × the 3 mm layer height), `infill = 1`.

**Do.** Compute.

**Expect.** Accepted without a refusal. The rule for ordinary uniform layers is
unchanged; only the adaptive case is stricter.

### SLICE-15 — The tooltips describe the rule that actually applies

**Set up.** The adaptive vase with both the Bricklayer and Non-planar infill
groups on.

**Do.** Hover the mouse over the `brickShift` input socket, then over the
`npAmp` input socket, and read the tooltip that appears.

**Expect.** Both tooltips describe the limit in terms of the **local** layer
gap, and what they say matches the behaviour you saw in SLICE-09 to SLICE-12.
A tooltip that still describes a fixed nominal layer height is a failure
(minor, unless it misleads you into a wrong setting).

### SLICE-16 — Variable flow keeps the ramp layers looking even

**Set up.** The adaptive vase with the Bricklayer group on. On the **G-code
Generator**, right-click and switch on the **Variable layer-height flow**
(`varh`) group; leave `varhMin = 0.25` and `varhMax = 2.5`.

Where a wall has been lifted, the gap it has to bridge is bigger or smaller
than nominal, so the amount of material has to follow. That is what the `varh`
group does, and Slice's own warning tells you to switch it on.

**Do.** Compute Slice, then the Generator. Look at the preview curves of the
first and last few layers, and read the Generator's `info`.

**Expect.** The Generator reports that variable-height flow is active. Nothing
in the ramp layers looks starved or flooded compared with the middle of the
part. (The real proof is a print — see SLICE-25.)

## 1c. Wave infill and non-planar infill

*Use set-up A (plain cylinder, 80 mm tall, radius 40, spacing 10).*

Wave infill lays the same parallel rows as the ordinary zigzag pattern, but
each row follows a wave, and neighbouring rows are mirrored so that one row's
peaks face the next row's troughs.

### SLICE-17 — Sine wave infill, and making the peaks touch

**Set up.** Set-up A with `infill = 5` (Wave, sine). Right-click Slice and
switch on the **Wave infill** group. Leave `waveLen = 20`, `waveRes = 0.5`,
`waveTangent = false`, `waveClearance = 0`.

**Do.** Compute Slice and look at the infill of one layer from directly above
(Rhino `Top` view). Then set `waveTangent = true`, compute, and look again.

**Expect.** With `waveTangent = false` the rows are mirrored sine waves whose
peaks come close to each other but leave a small gap. With `waveTangent = true`
the peaks of neighbouring rows **touch exactly**.

### SLICE-18 — Wave infill following a curve you draw yourself

**Set up.** Set-up A with `infill = 6` (Wave, graph). Wave infill group on.
Add an **Graph Mapper** (`1 Slice`) to the canvas and wire its `map`
output into Slice's `profile` input.

**Do.** On the Graph Mapper, choose a curve type from its drop-down and drag a
few of its points into an obviously non-sine shape (a sawtooth, or one tall
peak). Compute Slice and look at the infill from above. Then **disconnect** the
wire from `profile` and compute again.

**Expect.** With the wire connected, one period of the infill wave follows the
shape you drew — change the curve and the infill changes with it. With the wire
disconnected, the infill falls back to a plain sine wave and `info` says so
rather than failing.

### SLICE-19 — The wave repeats cleanly whatever curve you draw

**Set up.** Continue from SLICE-18. On the Graph Mapper, draw a curve whose
left end and right end are at clearly **different** heights.

**Do.** With `waveRepeat = false` (the default), compute and look at the infill
from above. Then set `waveRepeat = true`, compute, and look again.

**Expect.** With `waveRepeat = false` the curve is mirrored at the end of every
period, so the rows form one continuous, unbroken line — no sudden jump where
one period ends and the next begins. With `waveRepeat = true` the curve plays
forward each time, so there **is** a visible step at every period boundary.
Both behaviours are correct; the check is that they differ this way round.

### SLICE-20 — A layer with a hole in it fills correctly

**Set up.** You need a part whose layers have more than one ring. In Rhino,
draw a cylinder radius 40, height 80, and a second cylinder radius 15 through
its middle, and subtract the small one from the large one (`BooleanDifference`).
Wire that solid into Slice `geo` instead of Test Geometry.

**Do.** Compute with `infill = 5` (wave), look at one layer from above. Repeat
with `infill = 1` (zigzag).

**Expect.** In both patterns the infill fills the ring-shaped area between the
outer and the inner boundary, stops at the inner hole, and does not run across
it. No error appears.

### SLICE-21 — A layer that collapses to nothing is skipped quietly

**Set up.** Test Geometry shape **Frustum**, `height = 100`, `radius = 40`,
Profile group on, `topRatio = 0`. That makes a cone whose topmost layers shrink
to a point.

**Do.** Compute Slice with `infill = 5`, then again with `infill = 1`. Read
`info`.

**Expect.** The top of the cone is simply left out — no red error on the
component, no exception message, and no spike or stray line shooting out of the
tip in the viewport. `info` may mention the skipped layers; that is fine.

### SLICE-22 — Non-planar infill on ordinary layers

**Set up.** Set-up A, `infill = 1`. Non-planar infill group on, defaults
(`npAmp = 0.4`, `npWave = 20`, `npPhase = 0.5`, `npRamp = 1`).

**Do.** Compute. Cut a vertical section through the part in Rhino
(`Section` command) and look at the infill lines of three or four neighbouring
layers. Also look at the very bottom and very top layer.

**Expect.** The infill of each layer waves up and down, and the trough of one
layer sits over the crest of the layer below, so successive layers mesh
together like corrugated sheets. The bottom layer and the top layer are flat.
Walls are never wavy — only infill.

## 1d. Printed acceptance — needs clay in the machine

These are the only proof that the features above actually work in material.
**Print small parts.** None of these patterns has been printed before, so treat
the first run as a bring-up: stay at the machine and be ready to stop it.

### SLICE-23 — Print a flat part with bricklayer on

**Do.** Slice a small cylinder with flat layers and the Bricklayer group on,
generate the G-code, and print it.

**Expect.** The finished surface shows interlocking layers rather than one
continuous vertical seam line. The part does not lean or delaminate.

### SLICE-24 — Print a cone-sliced part with bricklayer on

**Do.** Print the geometry from SLICE-01 (set-up B), scaled down if needed.

**Expect.** It prints without the nozzle striking the previous layer, and the
interlocking is visible on the finished surface.

### SLICE-25 — Print adaptive layers, with bricklayer and then with non-planar infill

**Do.** Print the adaptive vase from section 1b twice: once with the Bricklayer
group on, once with the Non-planar infill group on. Have the Generator's `varh`
group switched on both times.

**Expect.** The flow follows the changing layer thickness — no visibly starved
thin layers and no flooded thick ones, and no collision.

### SLICE-26 — Print a part with non-planar infill

**Do.** Print the part from SLICE-22.

**Expect.** The layers mesh into each other. **Listen** during the print: if
the nozzle strikes the previous layer you will hear it. Stop the print if it
does.

### SLICE-27 — Print a wave-infill part in both wave modes

**Do.** Print one small part with `infill = 5` (sine) and one with `infill = 6`
(your own curve).

**Expect.** The wave rows are laid down as previewed and the mirrored rows
touch or clear each other as `waveTangent` said they would.

## 1e. Stable seams, open-curve orientation and wall waves (4.3.0)

Before 4.3.0 a round part sliced with one wall got its seam - and with it the start of
every spiral, Fermat or zigzag fill - at whichever section vertex happened to lie
farthest from the centre, a coin toss per layer. Every closed wall now starts where the
+X ray from the section's centre crosses it, every fill starts next to that point, and
Place orients open curves before it moves the wall seams.

### SLICE-28 — Spiral and Fermat fills stack in XY on a round vase

**Set up.** Set-up A (plain cylinder), `wallCount = 1`, `infill = 3`. Then `infill = 4`.

**Do.** Compute. Look straight down (Top view) with all layers visible, then hide all
but every fifth layer.

**Expect.** All layers show the same spiral: the start point on the +X side of every
layer lies on one vertical line, and the turns coincide in the top view instead of
being rotated against each other. Same for Fermat: the start and the end of every
layer's path sit next to each other on the +X side, on every layer. Repeat with
`wallCount = 2`: the same picture.

### SLICE-29 — The zigzag fill starts on the same side of every layer

**Set up.** Set-up A, `wallCount = 1`, `infill = 1`, `angle = 0`.

**Do.** Compute. Top view, every fifth layer visible. Then set `angle = 90` and repeat.

**Expect.** With `angle = 0` the zigzag of every layer is ONE polyline (before 4.3.0 the
connector guard cut a round part's zigzag into one piece per row - 22 travels for 23
rows) and it starts in the same corner of the part on every layer. With `angle = 90`
the start sits next to the +X wall seam on every layer.

### SLICE-30 — A fill that has to fall back to rings says so

**Set up.** A mesh whose inward offset splits into two islands (a dumbbell: two
cylinders joined by a narrower bar, bar width about three wall distances),
`infill = 3`.

**Do.** Compute and read `info`.

**Expect.** One line per object of the form `layer N: rings not strictly nested (2 islands
at depth 0) - independent rings`, naming the first layer that fell back. The preview
shows concentric rings on those layers and a spiral where the section is one island.

### SLICE-31 — Inner wall loops can wave, and a quarter turn apart

**Set up.** Set-up A, `wallCount = 3`, `infill = 0`, Non-planar group on with
`npAmp = 0`, `npWallAmp = 0.4`, `npWave = 20`, `npLoopPhase = 0.25`, `npWallOuter` off.

**Do.** Compute. Cut a vertical section through the wall (`Section`) and look at the
three loops of one layer side by side; then look from the outside.

**Expect.** The outermost loop is flat; the two inner loops wave up and down with the
same period, and where one has a crest the next has its crest a quarter period further
along, so the two layers interlock rather than run parallel. The first and last layer
are flat. Every loop closes: no step where a loop's end meets its start. With
`npWallOuter` on, the outer loop waves too and the visible surface shows it. With
`npWallAmp` above 0.4 x the layer height the component refuses with a message naming
the limit, exactly as `npAmp` does.

### SLICE-32 — Wall wave together with bricklayer

**Set up.** As SLICE-31 plus the Bricklayer group on (`brickShift = 0.5`).

**Expect.** Both act: alternate loops sit half a layer higher AND wave. No refusal -
the wave limit does not shrink because bricklayer is on, since a shifted loop's two
consecutive layers move together.

### PLACE-01 — Align keeps the infill start on the seam side, every layer

**Set up.** SLICE-29's part (`infill = 1`, `angle = 90`), wired into Place with
`seamMode = 1` (Align), `seamAngle = 0`, then `seamAngle = 90`.

**Do.** Compute, look at the `seams` points and at the start of each layer's infill
curve (turn on curve direction arrows, or bake and check `CurveStart`).

**Expect.** With 0 deg nothing moves (the slicer's seams already sit on +X). With 90 deg
every wall seam sits on +Y and every layer's zigzag starts at the end that is nearer
+Y - the same end on every layer, never alternating. `info` says `open flipped: n/m`.

### PLACE-02 — Nearest never starts a spiral at its centre

**Set up.** SLICE-28's part (`infill = 3`), `seamMode = 3` (Nearest); first with no
`seamPoint`, then with a `seamPoint` on the far side of the part from the spiral start.

**Expect.** The spiral always runs inward: it starts on the outer ring next to the wall,
never at the centre, on every layer - with and without the point. The wall seam sits
next to the spiral start. Without a point, the start stays on the +X side; with the
point it moves to the end of the spiral nearer the point, which is still the outer end.

### PLACE-03 — Track keeps open-curve ends from flipping over 30 layers

**Set up.** SLICE-29's part, `seamMode = 5` (Track), 30 or more layers.

**Expect.** Wall seams stack in a vertical line and the zigzag start end is the same
on every layer; `open flipped` in `info` is either 0 or every second layer, never a
random count.

### PLACE-04 — Keep leaves open curves alone

**Set up.** Any part with infill, `seamMode = 0`.

**Expect.** `moved` equals the slicer's curves shifted to the print centre, direction
and start included; `info` reports `open flipped: 0/m`.

## 1f. Errors that used to be silent (4.4.0)

Five failures used to happen with nothing shown: the plug-in caught the error,
quietly fell back, and carried on. The fallbacks are unchanged - nothing new
fails, and nothing that worked before stops working - but each one now says so.
These checks exist because the message is the only new behaviour, so the only
way to confirm it is to provoke the failure on purpose.

All five are hard to trigger by accident, which is exactly why they were never
noticed. Where a check cannot be provoked without editing a saved file by hand,
that is said in the set-up. **Work on a COPY of any .gh file you edit.**

### SILENT-01 — Graph Mapper says when a saved curve could not be read

**Set up.** Build a document with one Graph Mapper, drag its curve into a
clearly non-default shape, save, and close Rhino. Open the .gh file in a text
editor, find the stored text after `iamGraphMap`, and corrupt it - deleting a
handful of characters from the middle is enough. Save the edited copy.

**Do.** Open the edited document in Rhino.

**Expect.** The component comes up with the DEFAULT curve (as before) and carries
an orange warning saying the saved graph curve could not be read, naming the
parse error, and telling you to re-edit it before printing. The warning survives
the first solve - it does not flash and disappear. Before 4.4.0 the curve
reverted with no message at all, which is the failure this check is for.

**Also.** If the document had per-family memory (switch graph type, edit, switch
back, then save), corrupting one `iamGraphMemN` entry instead produces a grey
remark counting the dropped curves, and the other families still come back.

### SILENT-02 — Legend says when a wired Gradient could not be read

**Set up.** A Legend with a Grasshopper **Gradient** component wired into
`gradient`. Confirm the normal case first: the legend uses the gradient's grip
positions and `info` reports `gradient: N grips`.

**Do.** There is no reliable way to make the grip read throw from the canvas, so
this one is confirmed by its absence: with a healthy Gradient there must be NO
warning, and `gradient: N grips` must appear.

**Expect.** No warning in the healthy case. If the warning "the wired Gradient
could not be read … its grip positions were ignored and its colours spaced
evenly" ever appears in normal use, report it - the legend still draws, using
evenly spaced colours, but the positions you set were lost. Before 4.4.0 that
loss looked identical to wiring a plain colour list.

### SILENT-03 — Desirability says when its canvas graph could not be drawn

**Set up.** A working Desirability document that draws its panel on the canvas.

**Do.** Compute normally.

**Expect.** The panel draws and there is NO remark. The new remark ("the canvas
graph could not be drawn … the outputs are unaffected") only appears when the
drawing itself fails, which needs a starved graphics host; if you ever see it,
check that `optimum`, `report` and the other outputs are still correct - they
are computed before the panel and are not affected by it.

### SILENT-04 — RSM Evaluate says the same about its panel

**Set up and do.** As SILENT-03, with RSM Evaluate.

**Expect.** As SILENT-03: panel drawn, no remark, and the model outputs correct
whether or not the panel appears.

### SILENT-05 — The Duet Console says when a job log could not be closed

**Needs the machine.** Run any short job to completion with the Duet Console
connected, then read the console's log.

**Expect.** Normal runs show nothing new. The new line reads "job log: the run
could not be closed cleanly - …" and only appears when finishing the run fails -
for example if the log destination folder is removed or becomes read-only while
a job is running. To provoke it deliberately: point the log destination at a
removable drive or a network share, start a short job, and disconnect the drive
before the job ends. The print itself must be unaffected; only the log line is
new. Before 4.4.0 the run was truncated with no message anywhere.

## 1g. Pipeline extraction (4.4.0)

Slice's per-object slicing was moved out of the component into the engine. It was moved,
not rewritten, and the unit tests say the engine reproduces the old composition exactly -
but nothing in the tests can load Rhino, so the one thing still worth a human eye is that
the component around it still produces the same thing it did in 4.3.0.

### SLICE-33 — Slicing is unchanged after the move

**Set up.** Set-up A, then set-up B, exactly as they are written above.

**Do.** Compute each and note the `layers` number and the whole `info` text. Compare
against the same two set-ups on 4.3.0 (an installed 4.3.0, or the numbers recorded in an
earlier round).

**Expect.** Identical in all three: `layers`, `info` word for word, and the preview.
Nothing new appears in the runtime bubble, and nothing that used to appear is missing.

## Results — Slice

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| SLICE-01 — Bricklayer actually shifts something on a cone-shaped part | | | |
| SLICE-02 — The shift is visible, not just counted | | | |
| SLICE-03 — The shift survives a steeper and a shallower cone | | | |
| SLICE-04 — The outermost wall can be kept flat, or shifted | | | |
| SLICE-05 — The first and last layers stay flat | | | |
| SLICE-06 — Switching bricklayer off changes nothing else | | | |
| SLICE-07 — The G-code contains the shift, not just the preview | | | |
| SLICE-08 — The bricklayer warning talks about local gaps | | | |
| SLICE-09 — A thin layer gets a small lift, a thick layer a large one | | | |
| SLICE-10 — No wall is ever lifted into its neighbour above | | | |
| SLICE-11 — Too much non-planar amplitude is refused, and the message says why | | | |
| SLICE-12 — Lowering the amplitude until it is accepted gives a sensible limit | | | |
| SLICE-13 — The ramp-in layers do not cause a false refusal | | | |
| SLICE-14 — Ordinary uniform layers did not get stricter | | | |
| SLICE-15 — The tooltips describe the rule that actually applies | | | |
| SLICE-16 — Variable flow keeps the ramp layers looking even | | | |
| SLICE-17 — Sine wave infill, and making the peaks touch | | | |
| SLICE-18 — Wave infill following a curve you draw yourself | | | |
| SLICE-19 — The wave repeats cleanly whatever curve you draw | | | |
| SLICE-20 — A layer with a hole in it fills correctly | | | |
| SLICE-21 — A layer that collapses to nothing is skipped quietly | | | |
| SLICE-22 — Non-planar infill on ordinary layers | | | |
| SLICE-23 — Print a flat part with bricklayer on | | | |
| SLICE-24 — Print a cone-sliced part with bricklayer on | | | |
| SLICE-25 — Print adaptive layers, with bricklayer and then with non-planar infill | | | |
| SLICE-26 — Print a part with non-planar infill | | | |
| SLICE-27 — Print a wave-infill part in both wave modes | | | |
| SLICE-28 — Spiral and Fermat fills stack in XY on a round vase | | | |
| SLICE-29 — The zigzag fill starts on the same side of every layer | | | |
| SLICE-30 — A fill that has to fall back to rings says so | | | |
| SLICE-31 — Inner wall loops can wave, and a quarter turn apart | | | |
| SLICE-32 — Wall wave together with bricklayer | | | |
| SLICE-33 — Slicing is unchanged after the move | | | |

## Results — Place

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| PLACE-01 — Align keeps the infill start on the seam side, every layer | | | |
| PLACE-02 — Nearest never starts a spiral at its centre | | | |
| PLACE-03 — Track keeps open-curve ends from flipping over 30 layers | | | |
| PLACE-04 — Keep leaves open curves alone | | | |

# 2. Graph Mapper

*Panel `1 Slice`. No compute button — it reacts to every change.*

Graph Mapper passes a list of numbers through a curve that you draw on the
component itself, and gives back the remapped numbers. Its fourth output,
`map`, hands the curve itself to other components — that is what drives the
wave infill in SLICE-18.

*Set up for this whole section: place a Graph Mapper on an empty canvas. Wire a
Grasshopper **Series** component into `t` (start 0, step 0.05, count 21) and a
Panel into `y`, `equation` and `info`. Choose the curve type from the drop-down
on the component body.*

### GRAPH-01 — Handles appear on the point you point at

**Set up.** Curve type **Spline**. Add a few points by double-clicking on the
curve.

**Do.** Move the mouse over one of the points on the graph.

**Expect.** Two small handles — one going left, one going right — appear on
**that** point only. The other points show no handles. Move away and they
disappear again.

### GRAPH-02 — Dragging a handle keeps the curve smooth

**Set up.** Continue from GRAPH-01.

**Do.** Hover a point, then click and drag one of its handles.

**Expect.** Both handles move together, staying in line with each other, so the
curve passes through that point smoothly with no kink.

### GRAPH-03 — Shift-drag makes a corner

**Set up.** Continue from GRAPH-02.

**Do.** Hold **Shift** and drag one handle of a point.

**Expect.** Only the handle you are dragging moves; the other one stays where
it was. The curve now has a visible corner at that point.

### GRAPH-04 — Ctrl snaps the handle to quarter slopes

**Set up.** Continue from GRAPH-03.

**Do.** Hold **Ctrl** and drag a handle slowly through a wide arc.

**Expect.** The handle does not follow the mouse continuously; it jumps between
fixed slopes at quarter steps.

### GRAPH-05 — Double-clicking a handle returns the point to automatic

**Set up.** A point whose handles you have dragged into an obvious shape.

**Do.** Double-click one of that point's handles.

**Expect.** The point goes back to the automatic tangent the component
calculates by itself, and the curve through it now looks the same as through an
untouched point of the same shape.

### GRAPH-06 — Converting another curve type into a spline

**Set up.** Choose an analytic curve type from the drop-down (for example
**Sine** or **Bezier**).

**Do.** Right-click the component and choose **Convert → Spline**. Then hover
one of the resulting points and drag a handle.

**Expect.** The converted curve keeps its shape. Its points start out on
automatic tangents, and they gain handles that behave exactly as in GRAPH-02 to
GRAPH-05.

### GRAPH-07 — Undo puts a handle back

**Set up.** A spline with handles.

**Do.** Drag a handle to a clearly different position, release, then press
**Ctrl+Z**.

**Expect.** The curve returns exactly to what it was before the drag. Every
single edit is undoable, one step at a time.

### GRAPH-08 — Each curve type remembers its own shape

**Set up.** A spline you have shaped by hand, handles and all.

**Do.** Switch the drop-down to a different curve type, then switch back to
**Spline**.

**Expect.** Your whole curve comes back — the same points in the same places
with the same handles, not a reset default.

### GRAPH-09 — The curve survives saving and reopening

**Set up.** A spline where you have set handles on some points and left others
automatic, shaped so it only ever rises (never dips).

**Do.** Save the Grasshopper file, close it, reopen it.

**Expect.** The curve, its points and its handles are exactly as you left them.
The points you never touched still produce a curve that only rises — it does
not bulge above or dip below the points it passes through.

### GRAPH-10 — The `map` output drives the wave infill

**Set up.** The Graph Mapper wired into **Slice**'s `profile` input, as
in SLICE-18.

**Do.** Reshape the curve on the Graph Mapper and recompute Slice.

**Expect.** The `map` output carries the curve itself (not just numbers), and
the infill wave in the viewport takes the shape you drew.

## Results — Graph Mapper

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| GRAPH-01 — Handles appear on the point you point at | | | |
| GRAPH-02 — Dragging a handle keeps the curve smooth | | | |
| GRAPH-03 — Shift-drag makes a corner | | | |
| GRAPH-04 — Ctrl snaps the handle to quarter slopes | | | |
| GRAPH-05 — Double-clicking a handle returns the point to automatic | | | |
| GRAPH-06 — Converting another curve type into a spline | | | |
| GRAPH-07 — Undo puts a handle back | | | |
| GRAPH-08 — Each curve type remembers its own shape | | | |
| GRAPH-09 — The curve survives saving and reopening | | | |
| GRAPH-10 — The `map` output drives the wave infill | | | |

# 3. Test Geometry

*Panel `6 Calibrate`. No compute button.*

Test Geometry produces ready-made sample shapes for calibration prints, so that
a test series always uses the same body. It offers free shapes (cylinder, vase,
twisted vase, frustum, wall, box), six standard test specimens taken from DIN
EN and ASTM standards, and one zig-zag extrusion test line.

*Set up for this section: place a Test Geometry component on an empty canvas.
Attach a Panel to `info`. Its `geometry` output is a solid body; its `path`
output is a line to print directly.*

### TESTGEO-01 — The shape list is complete

**Do.** Open the `Shape` drop-down on the component.

**Expect.** It lists **thirteen** shapes: six free shapes (cylinder, vase,
twisted vase, frustum, wall, box), six standard specimens, and the zig-zag
extrusion path.

### TESTGEO-02 — Every standard specimen comes out at its published size

**Set up.** Switch on the **Standard specimen scale** group and leave
`specimenScale = 1`.

**Do.** Select each of the six standard specimens in turn. For each one,
measure the body in the Rhino viewport (`Distance` between opposite corners, or
`BoundingBox`), and compare against this table:

| Shape | Standard | Nominal size, mm |
|---|---|---|
| Bending prism | DIN EN 196-1 | 160 × 40 × 40 |
| Bending bar | DIN EN ISO 178 | 80 × 10 × 4 |
| Bending bar | ASTM D790 | 127 × 12.7 × 3.2 |
| Cube | DIN EN 12390 | 150 × 150 × 150 |
| Cylinder | DIN EN 12390 | Ø150 × 300 |
| Compression prism | ASTM D695 | 12.7 × 12.7 × 25.4 |

**Expect.** Every measurement matches the table. Note that the standard
specimens ignore `height`, `radius`, `length` and `width` on purpose — their
size comes from the standard.

### TESTGEO-03 — The bending span is reported

**Set up.** Select the DIN EN 196-1 bending prism.

**Do.** Read the `info` panel.

**Expect.** `info` names the standard and states the **bending span** — the
distance between the two supports in the test rig — as 100 mm for EN 196-1. It
has to be reported, because you cannot measure it off the specimen itself.
Check the other two bending shapes as well: 64 mm for ISO 178 and 51.2 mm for
ASTM D790.

### TESTGEO-04 — Scaling scales the span too, and warns about thin walls

**Set up.** The DIN EN ISO 178 bar (4 mm thick at full size — thinner than a
single extruded layer of clay, which is why scaling exists).

**Do.** Set `specimenScale = 2`, read `info` and measure the body. Then set
`specimenScale` low enough that the smallest dimension drops below 5 mm (for
this bar, any value under about 1.25).

**Expect.** At `specimenScale = 2` the body is twice as large in every
direction **and** the reported span has doubled with it. Once the smallest
dimension is under 5 mm, `info` adds a remark about the layer width — the part
is thinner than what the nozzle can lay down.

### TESTGEO-05 — A scaled specimen says it is no longer a standard specimen

**Set up.** Any standard specimen with `specimenScale` set to anything other
than 1.

**Do.** Read `info`.

**Expect.** A second remark states plainly that the body is no longer at the
standard's dimensions, so results are comparable inside your own test series
but cannot be reported against the standard.

### TESTGEO-06 — The zig-zag comes out of `path`, not `geometry`

**Set up.** Select the **zig-zag extrusion path** shape. Switch on the
**Extrusion test path** group: `zzLength = 150`, `zzPitch = 10`,
`zzPasses = 8`, `zzLayers = 1`, `zzArcTurns = true`.

The zig-zag is a line to drive the nozzle along, not a solid to be sliced.
This is the one thing a first-time user gets wrong.

**Do.** Look at all four outputs and read `info`.

**Expect.** `geometry` is **empty**, and `path` (the fourth output) holds the
zig-zag line. `info` explains that this shape is a toolpath, and that it must
go to the G-code Generator rather than to Slice.

### TESTGEO-07 — The zig-zag path produces usable G-code

**Set up.** Wire Test Geometry's `path` output **straight into the G-code
Generator's `crvs` input** — not through Slice. Set the Generator's
`layerHeight = 3` and `width = 10`, and `robot` from a Robot Definition set to
`none`. Set `zzLayers = 3` on Test Geometry.

**Do.** Click the Generator's compute button. Read `info`, look at the
`preview` curves, and read the G-code itself in a Panel.

**Expect.** A sensible file: the passes alternate direction (out, back, out,
back), and each stacked layer starts at the opposite end from the layer below,
so the seams are not all stacked at one end.

### TESTGEO-08 — Rounded turns bulge, sharp turns do not

**Set up.** The zig-zag with `zzPitch = 10`.

**Do.** With `zzArcTurns = true`, look at the ends of the passes from above and
measure how far the turn reaches past the end of a straight pass. Then set
`zzArcTurns = false` and look again.

**Expect.** With rounded turns the reversal is a half circle that reaches
`zzPitch / 2` = 5 mm past the end of the pass. With sharp turns the nozzle
turns on the spot and reaches no further than the pass itself.

### TESTGEO-09 — Print and break one standard prism — needs clay

**Do.** Print one **DIN EN 196-1** prism (160 × 40 × 40 mm) and break it in the
three-point bending rig.

**Expect.** The printed specimen fits the rig at the span the component
reported in TESTGEO-03 (100 mm), without having to move the supports.

### TESTGEO-10 — Print the zig-zag test line — needs clay

**Do.** Print the zig-zag path from TESTGEO-07, with `zzPitch` set to the layer
spacing you actually intend to use.

**Expect.** Look at the reversals: the layer should not tear on the inside of a
turn nor pile up on the outside. Look at the gaps between passes: they tell you
whether the flow matches the spacing. Record what you see — this is a
measurement, not a pass/fail, unless the path is obviously wrong.

## Results — Test Geometry

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| TESTGEO-01 — The shape list is complete | | | |
| TESTGEO-02 — Every standard specimen comes out at its published size | | | |
| TESTGEO-03 — The bending span is reported | | | |
| TESTGEO-04 — Scaling scales the span too, and warns about thin walls | | | |
| TESTGEO-05 — A scaled specimen says it is no longer a standard specimen | | | |
| TESTGEO-06 — The zig-zag comes out of `path`, not `geometry` | | | |
| TESTGEO-07 — The zig-zag path produces usable G-code | | | |
| TESTGEO-08 — Rounded turns bulge, sharp turns do not | | | |
| TESTGEO-09 — Print and break one standard prism — needs clay | | | |
| TESTGEO-10 — Print the zig-zag test line — needs clay | | | |

# 4. Overhang

*Panel `5 Analyze`. Has a compute button.*

Overhang measures how far each piece of a layer hangs out over the layer below
and colours the part accordingly. The colours come from the **magma** scale
(deep purple → red-orange → light orange).

Two thresholds set the classes: `warnDeg` (default 25°) and `failDeg`
(default 45°). Both defaults are placeholders and should be measured for your
own material — but for testing, the defaults are fine.

*Set up for this section: set-up C. Wire Slice's `contours` into Overhang's
`contours`, and Overhang's `legend` output into an **Legend**
component's `legend` input. Attach Panels to `info` and `summary`.*

### OVER-01 — Three classes in three magma colours

**Set up.** Set-up C on the twisted vase (Test Geometry shape **Twisted vase**,
Profile group on, `twistDeg = 90`) so that some of the part really does
overhang. `colorMode = 0`, `warnDeg = 25`, `failDeg = 45`.

**Do.** Click Overhang's compute button. Look at the part in the viewport and
at the Legend.

**Expect.** Segments are painted in exactly three colours: deep purple where
the overhang is below the warning angle, red-orange between warning and
failure, light orange beyond the failure angle. The Legend shows the same three
colours with the same meanings.

### OVER-02 — A continuous colour scale, and it stays quick on a big part

**Set up.** Continue from OVER-01 but set `colorMode = 1`. Then also try it on
a deliberately heavy part — a tall vase with `spacing = 2` and a fine `tol`,
so that the slice has on the order of a hundred thousand segments.

**Do.** Compute. Look at the colours, look at the Legend, then orbit and zoom
the viewport.

**Expect.** Instead of three blocks of colour, the part is painted on a smooth
magma ramp running from 0° up to `failDeg`, with everything above `failDeg`
staying at the end colour. The Legend shows that ramp with marks at the warning
and the failure angle. Orbiting and zooming stay smooth even on the heavy part.

### OVER-03 — The `colors` output matches what you see

**Set up.** Either colour mode.

**Do.** Wire the `colors` output (the sixth one) together with the `contours`
into a Grasshopper **Custom Preview** component, and hide Overhang's own
preview (right-click → Preview off).

**Expect.** The part looks identical to the way Overhang painted it. The
`colors` tree is also cut up into the same branches as the `angles` output, so
the two line up point for point.

### OVER-04 — Local reference versus gravity reference

**Set up.** Set-up **B** (cone-shaped layers) wired into Overhang. This matters
only on cone-sliced parts.

`global = false` measures the overhang against the layer's own direction — how
far one layer sticks out over the layer below it. `global = true` measures it
against true vertical — how far the part leans against gravity. On cone-shaped
layers the two are different.

**Do.** Compute with `global = false`, note the colours and read `info` and the
first line of `summary`. Then set `global = true`, compute, and compare.

**Expect.** With `global = false` the colours follow the `angles` output. With
`global = true` they follow the `anglesWorld` output instead, `info` states
`reference: global (world Z)`, the Legend's title reads **overhang (global)**,
and `summary` begins with a `# reference: global` line. The two number outputs
`angles` and `anglesWorld` themselves are unchanged by the toggle — both are
always produced.

### OVER-05 — On flat layers the toggle changes nothing

**Set up.** Set-up A (ordinary flat layers) wired into Overhang.

**Do.** Compute with `global = false`, then with `global = true`.

**Expect.** Nothing visible changes. On flat layers the two ways of measuring
give the same answer, so they must agree.

## Results — Overhang

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| OVER-01 — Three classes in three magma colours | | | |
| OVER-02 — A continuous colour scale, and it stays quick on a big part | | | |
| OVER-03 — The `colors` output matches what you see | | | |
| OVER-04 — Local reference versus gravity reference | | | |
| OVER-05 — On flat layers the toggle changes nothing | | | |

# 5. Interlayer Time

*Panel `5 Analyze`. No compute button.*

For every point of the print, this component works out **how long ago the
material directly underneath it was laid down**. That waiting time decides
whether the layers bond: too short and the layer below is still soft and
collapses, too long and it has skinned over and the next layer will not stick.
The path is coloured on the Plasma scale between your two thresholds.

*Set up for this section: set-up C. Wire the G-code Generator's `gcode` output
into Interlayer Time's `gcode` input. Wire a number **slider** into `layer`
(range 0 to the layer count). Attach Panels to `report` and `info`.
Wire the `legend` output (the eighth one) into an **Legend**.*

### TIME-01 — The layer slider cuts the preview and the data together

**Do.** Drag the `layer` slider from its top value down to about half way.

**Expect.** The preview in the viewport shows only the layers up to the slider
value, and the `perLayer`, `times`, `path` and `colors` outputs shrink to
exactly the same set of layers — check by wiring a Grasshopper **Tree
Statistics** or a Panel to one of them and watching the branch count fall.

### TIME-02 — The written report always describes the whole print

**Do.** With the slider part way up, read the `report` panel, the `layers`
output and the `info` panel.

**Expect.** All three still describe the **entire** print, not the visible part
of it. This asymmetry is deliberate: the preview scrolls, the report does not.

### TIME-03 — Scrolling costs nothing

**Set up.** A large job — slice a tall part so the G-code has a few hundred
layers.

**Do.** Drag the `layer` slider quickly up and down.

**Expect.** The viewport redraws instantly. No spinner or progress bar appears,
Grasshopper does not stall, and `info` does not report a new computation time
each time you move the slider. Moving the slider only changes what is drawn.

### TIME-04 — Layer shading makes neighbouring layers tellable apart

**Set up.** Switch on the **Display** group.

**Do.** Look at a region where several neighbouring layers have very similar
times, with `shade = 0`, then `shade = 1`, then `shade = 2`.

**Expect.** All three render. At `shade = 0` the layers blend together. At
`shade = 1` alternate layers are tinted so you can count them. At `shade = 2`
there is a gradual tint from bottom to top. In both 1 and 2 you can tell
neighbouring layers apart.

### TIME-05 — Layers are lit, not flat

**Set up.** Display group on, `display = 1` (extruded layers).

**Do.** Look at the layers in a shaded Rhino viewport and orbit around them.

**Expect.** The layers look like rounded solid sausages, lit from above, with
visible shading on their curved sides. They do not look like flat black
silhouettes.

### TIME-06 — The `colors` output ignores the shading

**Do.** Wire `colors` into a Panel. Note some of the values. Change `shade`
between 0, 1 and 2 and look at the values again.

**Expect.** The colours in `colors` do not change. Shading is a display effect
only; `colors` always carries the plain Plasma colour for the measured time,
so it can be used as data.

### TIME-07 — The legend lives outside the component

**Do.** Look at the viewport with Interlayer Time computed, and look at the
list of inputs on the component.

**Expect.** No legend is drawn inside the viewport by Interlayer Time itself,
and there is **no `legend` input** on the component. There is a `legend`
**output** (the eighth one), which you wire into the Legend component.

### TIME-08 — The offline self-check passes

**Set up.** No Rhino needed for this one; it runs the component against its own
built-in test definition.

**Do.** Run the plug-in's probe for this component:
`-Probe interlayer-time`.

**Expect.** It reports success.

### TIME-09 — A recorded print log opens here and in a spreadsheet

**Set up.** You need a `print.csv` from a real print — do the Live View section
first, or use a file from an earlier print.

**Do.** Wire the file's lines into the `log` input (or its path into
`logPath`). Also open the same file in Excel or LibreOffice.

**Expect.** In Grasshopper: the real recorded timestamps replace the estimate
from the feedrates, and `report` says which clock it used. In the spreadsheet:
the file opens as a proper table with a header row and one row per sample, no
mangled columns.

## Results — Interlayer Time

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| TIME-01 — The layer slider cuts the preview and the data together | | | |
| TIME-02 — The written report always describes the whole print | | | |
| TIME-03 — Scrolling costs nothing | | | |
| TIME-04 — Layer shading makes neighbouring layers tellable apart | | | |
| TIME-05 — Layers are lit, not flat | | | |
| TIME-06 — The `colors` output ignores the shading | | | |
| TIME-07 — The legend lives outside the component | | | |
| TIME-08 — The offline self-check passes | | | |
| TIME-09 — A recorded print log opens here and in a spreadsheet | | | |

# 6. Legend

*Panel `5 Analyze`. No compute button.*

Grasshopper has no legend of its own, so this component draws one for any range
of data: a continuous colour ramp, thresholded classes, named groups, or a
scale of circle sizes. It can work on its own, or it can be fed by the `legend`
output of Overhang or Interlayer Time — in which case it always shows exactly
the colours those components painted.

Anything you type into the Legend's own inputs **overrides** the corresponding
field coming down the wire, so one legend can be adjusted without being
disconnected from its source.

*Set up for this section: place a Legend on an empty canvas. Wire a
Grasshopper **Random** component (100 values between 0 and 50) into `values`.
Attach a Panel to `info` and one to `spec`. Use the right-click `⇤ inputs`
button to create sliders and drop-downs for the inputs you need.*

### LEG-01 — A legend from plain numbers

**Set up.** Only `values` wired. `type = 0` (continuous ramp).

**Do.** Look at the legend drawn in the viewport.

**Expect.** A smooth colour bar with tick marks at round, readable numbers, a
sensible number of decimal places, and a range running from the smallest to the
largest value in your data.

### LEG-02 — Classes with thresholds, named or not

**Set up.** `type = 1` (classes). Switch on the **Style** group and wire a list
of ascending numbers into `breaks`, for example `10, 20, 30, 40`.

**Do.** Look at the legend. Then wire five names into `labels`, for example
`very low, low, medium, high, very high`.

**Expect.** Four thresholds give **five** bands. Without `labels`, each band is
labelled with the range it covers (`10 – 20` and so on). With `labels` wired,
your names replace those ranges, in order.

### LEG-03 — Named groups and a size scale

**Set up.** `type = 2` (groups) with a list of names in `labels`; then
`type = 3` (size) with a few sample values in `breaks`.

**Do.** Look at each.

**Expect.** `type = 2` draws one swatch per group with its name beside it.
`type = 3` draws circles of increasing size with the sample values labelled.
Both render and both are labelled correctly.

### LEG-04 — The colour scheme list is complete and every family works

**Set up.** Right-click the component and press `⇤ inputs` so that a drop-down
list is created for `scheme`.

**Do.** Open the drop-down and count roughly how many entries it has and how
they are grouped. Then pick each of these in turn and look at the result:
`magma`, `RdBu`, `tab10`, `Okabe-Ito`, `rainbow`, `npg`, `gsea`,
`material-teal`. Then try the older short names `blues`, `pinks` and `ambers`.
Then switch on `reverse`. Finally wire your own list of colours into `colors`
and set `scheme` to `custom`.

**Expect.** About 150 entries, grouped by family with a prefix so you can tell
where each name comes from. Each of the eight sampled names renders a plausible
scale. `blues`, `pinks` and `ambers` still work. `reverse` flips the direction
of the scale. Your own `colors` list wins over the named scheme.

### LEG-05 — Category colours are used in order, ramp colours are sampled

**Set up.** `type = 1` (classes) with four `breaks`, so five bands.

**Do.** Set `scheme = tab10` and look at the five band colours. Then set
`scheme = magma` and look again.

**Expect.** With `tab10` — a set of ten distinct named colours — the five bands
take tab10's first five colours **in their original order**. With `magma` — a
continuous ramp — the five bands take five colours spaced evenly along the
ramp. A category palette must not be treated as a ramp.

### LEG-06 — A hand-made Grasshopper gradient is reproduced exactly

**Set up.** Style group on. Drop a Grasshopper **Gradient** component on the
canvas and wire its colour output into the Legend's `gradient` input. Drag its
grips so that they are clearly **unevenly** spaced.

**Do.** Look at the legend. Then drag one grip and watch. Then read the `spec`
panel. Then wire a plain list of four colours into `gradient` instead. Finally
wire a list into `colors` as well, with the gradient still connected.

**Expect.** The legend reproduces the gradient's colours **at their actual
positions**, uneven spacing included. Dragging a grip updates the legend
immediately. `spec` contains a `stops=` entry recording those positions. A
plain colour list is spaced evenly instead. With both wired, `gradient` wins
and the component says so in a remark.

### LEG-07 — Another font renders everywhere

**Set up.** Switch on the **Text** group and set `font = Times New Roman`.

**Do.** Look at the legend with `placement = 0`, then `1`, then `2` (switch on
the **Placement** group; for `1` wire a Plane, for `2` wire a Point).

**Expect.** The text is in Times New Roman in all three placements, correctly
sized and not clipped.

### LEG-08 — Exactly one legend is visible, and the outputs still bake

**Set up.** Placement group on.

**Do.** Set `placement = 1` (fixed to a plane) and look carefully at the
viewport: is there anything besides the legend itself? Then set `placement = 2`
(floating billboard) and look again. Then bake the `mesh` output, and wire
`labels` and `frames` into a Grasshopper **Text Tag 3D**.

**Expect.** Exactly **one** legend is visible in each case — no wire edges
drawn over it, no little plane icons scattered on the labels, and no second
static copy sitting next to the floating one. Baking `mesh` still works and
gives a coloured legend object; the Text Tag 3D still shows the labels.

### LEG-09 — The viewport overlay sits where you tell it

**Set up.** Placement group on, `placement = 0`.

**Do.** Step `anchor` through all eight values (0 to 7). At each one, note
where the legend sits. Change `margin` from 18 to about 60. Change
`orientation` from 0 to 1.

**Expect.** The eight anchors put the legend at top-left, top-right,
bottom-left, bottom-right, top, bottom, left and right of the viewport
respectively. `margin` changes the distance from the viewport edge.
`orientation` switches between a horizontal bar and a vertical one. The legend
stays put while you orbit and zoom.

### LEG-10 — Fixed to a plane, it behaves like real geometry

**Set up.** `placement = 1` with a Plane wired into `plane`.

**Do.** Orbit and zoom. Bake the legend. Look at where the label text sits.
Then repeat the whole check in a Rhino document whose units are **inches**
(`File → Properties → Units`).

**Expect.** The legend behaves like an object in the model: it grows and
shrinks as you zoom, it is measured in document units, and it bakes with its
colours intact. The label text lands on the legend, not somewhere beside it.
It is the correct physical size in an inch document too.

### LEG-11 — The floating billboard keeps its size

**Set up.** `placement = 2` with a Point wired into `point`.

**Do.** Orbit all the way around the point, then zoom in and out a long way.

**Expect.** The legend always faces you, and it stays the **same size on the
screen** however far you zoom — unlike the plane placement in LEG-10.

### LEG-12 — Hiding the legend does not switch off the data

**Do.** Set `show = false`.

**Expect.** Nothing is drawn in the viewport, but the `colors`, `mesh`,
`labels` and `spec` outputs all still produce their values. Outputs must never
depend on whether something is drawn.

### LEG-13 — Fed from Overhang, the legend matches the painted part

**Set up.** Wire Overhang's `legend` output (the fifth one) into the Legend's
`legend` input, with the Overhang checks' twisted vase on screen.

**Do.** Compare the colours in the legend against the colours on the part,
in both `colorMode` 0 and 1.

**Expect.** They are the same colours, with the same boundaries. A legend that
disagrees with the picture it explains is worse than no legend.

### LEG-14 — Fed from Interlayer Time, the legend matches too

**Set up.** Wire Interlayer Time's `legend` output (the eighth one) into the
Legend's `legend` input. On Interlayer Time set `minTime = 10` and
`maxTime = 120`, and set `legendMax` to a fixed value such as 180.

**Do.** Look at the legend.

**Expect.** It shows the Plasma scale, running up to the `legendMax` you set,
with a mark at each of the enabled thresholds (`minTime` and `maxTime`), and a
grey swatch labelled for the points that had no layer underneath them.

### LEG-15 — Your own settings override the wire, and giving them up returns to it

**Set up.** A legend fed from one of the wires above.

**Do.** One at a time, set `min`, `max`, `scheme`, `decimals`, `ticks`, `unit`
and `title` on the Legend itself. Then clear each one again (delete the value,
or disconnect the object).

**Expect.** Each setting you type in takes precedence over what the wire says.
When you clear it, the legend goes back to the wire's own value. The wire is a
starting point, not a lock.

### LEG-16 — Colouring geometry with the legend's own colours

**Set up.** A Legend with `values` wired.

**Do.** Wire the `colors` output, together with some geometry (one object per
value — for example the segments of the analysed part), into a Grasshopper
**Custom Preview**.

**Expect.** The colour each object gets matches the swatch that its value falls
on in the legend. If it does not, either the legend or the colouring is wrong —
they come from the same scale.

### LEG-17 — A damaged wire is handled gracefully

**Set up.** Copy the text out of the `spec` panel of a working legend and paste
it into a Grasshopper Panel, then wire that panel into a second Legend's
`legend` input. This lets you edit the wire by hand.

**Do.** First add an invented extra key to the text, for example
`banana=yellow;`, and look at the result. Then change the version number in the
text to something else, and look again.

**Expect.** The invented key is simply ignored and the legend still draws
correctly. The wrong version number is refused, with a message you can actually
read and act on — not a crash and not a silent blank.

## Results — Legend

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| LEG-01 — A legend from plain numbers | | | |
| LEG-02 — Classes with thresholds, named or not | | | |
| LEG-03 — Named groups and a size scale | | | |
| LEG-04 — The colour scheme list is complete and every family works | | | |
| LEG-05 — Category colours are used in order, ramp colours are sampled | | | |
| LEG-06 — A hand-made Grasshopper gradient is reproduced exactly | | | |
| LEG-07 — Another font renders everywhere | | | |
| LEG-08 — Exactly one legend is visible, and the outputs still bake | | | |
| LEG-09 — The viewport overlay sits where you tell it | | | |
| LEG-10 — Fixed to a plane, it behaves like real geometry | | | |
| LEG-11 — The floating billboard keeps its size | | | |
| LEG-12 — Hiding the legend does not switch off the data | | | |
| LEG-13 — Fed from Overhang, the legend matches the painted part | | | |
| LEG-14 — Fed from Interlayer Time, the legend matches too | | | |
| LEG-15 — Your own settings override the wire, and giving them up returns to it | | | |
| LEG-16 — Colouring geometry with the legend's own colours | | | |
| LEG-17 — A damaged wire is handled gracefully | | | |

# 7. Load Settings, and saved settings files

*Panel `1 Slice`. No compute button.*

Every Lugworm component can save its current inputs to a small file with
**Save settings…** on its right-click menu. Load Settings points the whole
document at a **folder** of such files — one per component — so that pressing
the `⇤ inputs` button on any component builds its sliders and drop-downs from
your saved values instead of the built-in defaults. One folder per material or
per machine, and you can switch between them.

*Set up for this section: set-up A, plus an empty folder somewhere on disk
(for example `C:\temp\iamset-test`). Place a **Load Settings** component and
wire a Panel with that folder's path into `folder`. Attach Panels to `files`,
`unmatched` and `info`.*

### SET-01 — Saving a component's settings writes one file

**Do.** On the **Slice** component, set some clearly recognisable values
(`layerHeight = 2.5`, `spacing = 7`, `wallCount = 4`), switch on the
**Bricklayer** group, then right-click and choose **Save settings…**. Save it
into your test folder. Open the file in a text editor.

**Expect.** One `.iamset.json` file appears. It contains the values you set,
**and** a record of which optional groups were switched on.

### SET-02 — Loading them back is a single undo step

**Do.** Change Slice's values to something else. Right-click Slice, choose
**Load settings…**, and pick the file you just saved. Then press **Ctrl+Z**
once.

**Expect.** Loading restores both the values and the switched-on groups. A
single Ctrl+Z undoes the whole thing at once, not input by input.

### SET-03 — Empty inputs get new sliders, and a slider's range is widened not clipped

**Set up.** A saved file in which one number is **outside** the normal range of
a slider — for example `layerHeight = 40`.

**Do.** Delete everything wired into Slice's inputs so they are all empty.
Press `⇤ inputs` on Slice.

**Expect.** A new slider, toggle, panel or drop-down list appears for each
input, carrying the value from the file. Where the value lies outside a
slider's usual range, the **range is made bigger** so the value fits. The value
is never quietly changed to fit the slider.

### SET-04 — An existing slider is reused

**Set up.** One input of Slice wired to exactly one slider.

**Do.** Press `⇤ inputs`.

**Expect.** That slider is set to the file's value in place. No second slider
is created next to it.

### SET-05 — Anything more complicated upstream is left alone

**Set up.** Wire something more involved into one of Slice's inputs — a maths
expression component, or two sliders through an addition — and wire a plain
slider into another input.

**Do.** Press `⇤ inputs` and read the report the component shows.

**Expect.** The complicated input is **not touched**. It is named in the
report, alongside a count of how many inputs were created, updated and skipped.
Your own work upstream is never overwritten.

### SET-06 — Geometry and colours are deliberately not saved

**Do.** On a component that has geometry, point, plane, colour or tree inputs
(the G-code Generator's `crvs`, for example), choose **Save settings…** and
read the report it shows.

**Expect.** Those inputs are not written into the file, and the report names
them so you know they were left out on purpose. A settings file holds numbers
and switches, not geometry.

### SET-07 — A folder feeds every component at once

**Set up.** Save settings files for three different components into the test
folder. Point Load Settings at that folder.

**Do.** Press `⇤ inputs` on each of those three components, and also on a
fourth component for which the folder holds **no** file.

**Expect.** Each of the three builds its inputs from its matching file. The
fourth falls back to the plug-in's built-in defaults without complaining.

### SET-08 — Matching is by what is inside the file, not by its name

**Do.** Rename one of your settings files to something unrelated, such as
`clay-fast-monday.iamset.json`. Look at the `files` output.

**Expect.** It still matches the same component. The link is the component name
written **inside** the file; the file name is yours to choose.

### SET-09 — Two files claiming the same component

**Do.** Copy one settings file within the folder so that two files claim the
same component — give them names that sort differently, e.g. `aaa.iamset.json`
and `zzz.iamset.json`. Read `files` and `unmatched`.

**Expect.** The one whose path comes first alphabetically is used, and the same
one is used every time — never a random winner. **Both** files are reported, so
you can see the clash and fix it.

### SET-10 — Broken files are reported, not fatal

**Do.** Put two more files in the folder: one that is not valid JSON at all
(type some random text into it), and one that names a component that does not
exist in this plug-in (edit the `component` value to `LugwormNonsense`). Read
`unmatched` and `info`.

**Expect.** Both appear in the `unmatched` list — the broken one with the error
that was hit, the other one identified as naming an unknown component. Nothing
throws, the component does not go red, and the other files still work.

### SET-11 — Apply everything at once, undo it at once

**Do.** With several matching files in the folder, press **Apply to canvas** on
the Load Settings component. Then press **Ctrl+Z** once.

**Expect.** Every matching component is updated in one go, and a single Ctrl+Z
puts the whole document back as it was.

### SET-12 — Watching the folder for changes

**Do.** Set `watch = true`. With Grasshopper open, edit one of the settings
files in a text editor and save it. Then make any small change on the canvas to
trigger a solve, and press `⇤ inputs` on the affected component. Also open
Windows Task Manager and look at Rhino's thread count before and after.

**Expect.** The new value from the edited file is picked up. No extra
background threads appear — the folder is re-read during the ordinary solve,
by design, not watched by a separate thread.

### SET-13 — Only one Load Settings component per document

**Do.** Place a **second** Load Settings component on the same canvas and point
it at a folder.

**Expect.** The second one is refused with a clear error message on the
component. The first one keeps working normally. There is no silent takeover.

### SET-14 — The folder is not saved into the file

**Do.** Save the Grasshopper document, close it, and reopen it.

**Expect.** The folder registration is **gone** — the Load Settings component
no longer points anywhere. This is deliberate: a folder path is specific to one
computer, so it is never carried inside a shared file.

### SET-15 — Files work across language settings

Numbers are written differently in different countries — `2.5` in English,
`2,5` in German. A settings file must not depend on the setting of the computer
it was written on.

**Do.** Save a settings file with a decimal value such as `2.5`. Change Windows'
regional format to **German** (`Settings → Time & language → Language & region
→ Regional format`), restart Rhino, and load that file. Then do the reverse:
save a file with the German setting active, switch back to English, restart and
load it. Set your regional format back when you are finished.

**Expect.** In both directions the value comes back as `2.5` — not `25`, not
`2`, and not an error.

### SET-16 — A file from a newer format is refused; a stray key is tolerated

**Do.** Open a settings file in a text editor. Change the line
`"iamlab-settings": 1` to `"iamlab-settings": 2` and load it. Then set it back
to `1`, add an invented key such as `"banana": 3`, and load it again.

**Expect.** The changed version number is refused with a message that says so.
The invented key is ignored and the rest of the file loads normally.

## Results — Load Settings, and saved settings files

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| SET-01 — Saving a component's settings writes one file | | | |
| SET-02 — Loading them back is a single undo step | | | |
| SET-03 — Empty inputs get new sliders, and a slider's range is widened not clipped | | | |
| SET-04 — An existing slider is reused | | | |
| SET-05 — Anything more complicated upstream is left alone | | | |
| SET-06 — Geometry and colours are deliberately not saved | | | |
| SET-07 — A folder feeds every component at once | | | |
| SET-08 — Matching is by what is inside the file, not by its name | | | |
| SET-09 — Two files claiming the same component | | | |
| SET-10 — Broken files are reported, not fatal | | | |
| SET-11 — Apply everything at once, undo it at once | | | |
| SET-12 — Watching the folder for changes | | | |
| SET-13 — Only one Load Settings component per document | | | |
| SET-14 — The folder is not saved into the file | | | |
| SET-15 — Files work across language settings | | | |
| SET-16 — A file from a newer format is refused; a stray key is tolerated | | | |

# 8. Robot Definition

*Panel `4 Robot`. No compute button.*

Robot Definition selects the machine once, and its `robot` output is then wired
into every other component that needs to know which machine it is working for.
Its `preset` input takes one of: `none` (no machine — toolpath only),
`generic-cartesian`, `generic-duet-cartesian`, `potterbot-scara-v4` (the lab's
SCARA arm), `generic-klipper` and `generic-marlin`.

*Set up: place a Robot Definition on the canvas and press `⇤ inputs` so that
`preset` becomes a drop-down list. Attach Panels to every output.*

### ROBOT-01 — Every profile reports the same deposition process

**Do.** Step the `preset` drop-down through every profile in the list. After
each one, read the `process` and the `flavor` outputs.

**Expect.** `process` reads **`Paste (LDM)` for every single profile** — this
plug-in only does paste extrusion, so that socket never varies. `flavor` on the
other hand **does** vary: `DuetRrf` for the Duet and Potterbot profiles,
`Klipper` for `generic-klipper`, `Marlin` for `generic-marlin`.

## Results — Robot Definition

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| ROBOT-01 — Every profile reports the same deposition process | | | |

# 9. G-code Generator

*Panel `3 G-code`. Has a compute button.*

The Generator turns the placed layers into the actual instruction file the
printer runs: how much material to push out per millimetre of path, how the ram
pulls back and pushes again at a seam, how fast to move, and how far to lift
over material already printed.

**Two things must be typed in twice.** The Generator does not read them from
Slice: its `layerHeight` must equal Slice's `layerHeight`, and its `width`
should equal Slice's `spacing`.

**Three G-code dialects.** Different printer controllers understand slightly
different commands. `firmware = 0` is Duet RRF, `1` is Marlin, `2` is Klipper.
Left unwired, the Generator follows the connected Robot Definition's own
default dialect. Only Duet machines can be driven over the network from
Grasshopper; a Klipper or Marlin file has to be transferred to the machine by
hand.

**Warnings and remarks.** In Grasshopper a **warning** turns the component
orange and shows a warning balloon. A **remark** is milder: no colour change,
but a message you can read by hovering the component's message bubble or by
right-click → *Runtime messages*. Several checks below turn on the difference,
so make sure you can see both.

*Set up for this section: set-up A. Wire the `gcode` output into a Panel so you
can read the file, and attach a Panel to `info`.*

### GEN-01 — Default behaviour with no machine selected

**Set up.** Disconnect the Robot Definition from the Generator's `robot` input
so that nothing is wired there.

**Do.** Compute and read `info`.

**Expect.** `info` names the ram extruder model it is calculating for, and
reports the dialect as Duet RRF. With nothing connected, the Generator assumes
the lab's own machine.

### GEN-02 — A Klipper file speaks Klipper

**Set up.** Robot Definition set to `generic-klipper`, wired into `robot`.
Leave `firmware` unwired so it follows the profile.

**Do.** Compute and read the whole G-code in a Panel — or better, set
`savePath` to a `.gcode` file and open it in a text editor where you can
search.

**Expect.** In the file:

- a `SET_PRESSURE_ADVANCE` line;
- **one** `SET_VELOCITY_LIMIT` line carrying `ACCEL=` and
  `SQUARE_CORNER_VELOCITY=`, and **no** `M201` or `M205` lines;
- at every `; layer` comment, a `SET_PRINT_STATS_INFO` line with
  `TOTAL_LAYER=` and `CURRENT_LAYER=`;
- an `M84` in the footer at the end of the file.

### GEN-03 — A Duet file speaks Duet

**Set up.** Robot Definition set to `potterbot-scara-v4` (or nothing wired at
all). `firmware` unwired.

**Do.** Compute and read the file.

**Expect.** `M201` and `M566` lines, one per axis, near the top — and **no**
`M84` at the end. The Duet footer deliberately leaves the motors flagged as
homed, which is why `M84` must not be there.

### GEN-04 — The dialect list, and what happens when you leave it alone

**Do.** Press `⇤ inputs` on the Generator so that `firmware` becomes a
drop-down list, and open it. Then leave `firmware` unwired and change the
Robot Definition's `preset` between `generic-klipper`, `generic-marlin` and
`potterbot-scara-v4`, computing and reading `info` after each.

**Expect.** The drop-down offers Duet RRF, Marlin **and** Klipper. With
`firmware` unwired, the dialect follows whatever the connected profile says is
its default, and `info` prints the resulting dialect on a line beginning
`flavor:`.

### GEN-05 — Asking a Klipper machine for the wrong dialect warns you

**Set up.** Robot Definition `generic-klipper` wired into `robot`. Press
`⇤ inputs` so `firmware` has a drop-down.

**Do.** Set `firmware` to **Duet RRF** and compute; read the message. Set it to
**Marlin** and compute; read the message. Set it to **Klipper** and compute.
Finally disconnect `firmware` altogether and compute.

**Expect.** Duet RRF and Marlin each produce a **warning** that names the
mismatch and tells you how to fix it. Klipper, and no connection at all,
produce no message. In every case `info` reports the dialect on its `flavor:`
line.

### GEN-06 — Milder mismatches produce only a remark

**Do.** Three cases, computing and reading the messages after each:

1. Robot Definition `potterbot-scara-v4` wired, `firmware` set to **Klipper**.
2. **Nothing** wired into `robot`, `firmware` set to **Klipper**.
3. Robot Definition `generic-marlin` wired, `firmware` set to **Duet RRF**.

**Expect.** Case 1 is a **warning** — the lab machine really cannot run that
file. Cases 2 and 3 are only **remarks** — nothing is going to be driven over
the network, so the mismatch is worth mentioning but not worth an orange
component.

### GEN-07 — The variable-flow group is labelled for what it does

**Do.** Right-click the Generator and read the name of the group in the menu
that controls variable layer-height flow (`varh`).

**Expect.** It reads **"adaptive / non-planar slices"**. It must not mention
heat or temperature — this plug-in prints paste, not thermoplastic, and there
is no heated path anywhere in it.

## Results — G-code Generator

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| GEN-01 — Default behaviour with no machine selected | | | |
| GEN-02 — A Klipper file speaks Klipper | | | |
| GEN-03 — A Duet file speaks Duet | | | |
| GEN-04 — The dialect list, and what happens when you leave it alone | | | |
| GEN-05 — Asking a Klipper machine for the wrong dialect warns you | | | |
| GEN-06 — Milder mismatches produce only a remark | | | |
| GEN-07 — The variable-flow group is labelled for what it does | | | |

# 10. G-code Restart

*Panel `3 G-code`. Has a compute button.*

When a print fails part way through, Restart rebuilds the file so the machine
can carry on from where it stopped. You tell it **where** the print stopped in
one of three ways, and it removes everything already printed, adds a safe
approach down to the restart point, and keeps the rest of the job untouched.

The three ways are the inputs `z` (a height in mm), `percent` (the progress
percentage the controller was showing) and `point` (the actual position).
**Connect only one of them.** If more than one is connected, `point` beats
`percent`, and `percent` beats `z`.

**The one thing that trips people up:** `point` wants a **Rhino** point — a
point in your Rhino document, in document units. It does *not* want the raw
X/Y/Z numbers off the machine's own display. On the lab's SCARA arm the two
frames are mirrored: raw machine `(X, Y, Z)` is Rhino `(−Y, −X, Z)`. So raw
machine `(200, −500, 400)` mm is the Rhino point `(500, −200, 400)` mm. Wiring
a position output from Live View or the Duet Console avoids doing that in
your head, because those outputs are already in Rhino coordinates.

*Set up for this section: set-up A with a tall part, so there is a real job to
cut. Wire the Generator's `gcode` output into Restart's `gcode` input, and set
`gcodePath` to the same file if you saved one. Attach Panels to `info` and
`resume`, and preview the `printed` and `remaining` outputs.*

### RESTART-01 — The socket says which kind of point it wants

**Do.** Hover the mouse over the `point` input socket and read the description
that appears.

**Expect.** It says a Rhino / document point, and warns against entering raw
machine coordinates.

### RESTART-02 — Restarting from a height

**Set up.** Only `z` connected. Set `z` to a height part way up the part, for
example 30 mm.

**Do.** Click Restart's compute button. Look at the `printed` and `remaining`
curves in the viewport and read `info`.

**Expect.** `printed` covers everything below that height and `remaining`
everything from the first extruding move at or above it. The two together make
up the whole original job with nothing missing and nothing duplicated.

### RESTART-03 — Restarting from the percentage on the machine's display

**Set up.** Only `percent` connected, set to 50. `gcodePath` pointing at the
original saved `.gcode` file.

**Do.** Compute. Read `info`. Then **disconnect** `gcodePath` and compute
again.

**Expect.** With `gcodePath` connected, the cut is exact — the controller
counts bytes of the file, so it needs the real file to be exact. Without it,
the component still works but says in `info` that the position is only
approximate.

### RESTART-04 — Restarting from a point, in a millimetre document

**Set up.** A Rhino document in **millimetres**. Only `point` connected.

**Do.** Pick a location part way through the job and work out its two sets of
coordinates. Feed the **Rhino** point into `point` and compute. Then find the
chosen restart location in the output G-code (the `resume` output tells you
where it is) and read the machine coordinates written there.

Worked example, if your job reaches these heights: feed the Rhino point
`(500, −200, 400)`. The restart must land at the location whose machine
coordinates are `(200, −500, 400)`.

**Expect.** The restart is cut at the location whose machine coordinates are
the mirrored version of your Rhino point — `X` and `Y` swapped and both
negated, `Z` unchanged. The example numbers are deliberately not symmetric: if
the conversion were applied twice, or the wrong way round, the restart would
land somewhere else entirely and you would see it.

### RESTART-05 — The same point in an inch document

**Set up.** The same job in a Rhino document set to **inches**
(`File → Properties → Units`).

**Do.** Feed the same physical location as an inch point — for the worked
example above, `(19.6850, −7.8740, 15.7480)` — and compute.

**Expect.** Exactly the same restart location is chosen as in RESTART-04. This
proves the unit conversion and the mirrored-frame conversion are being applied
together and in the right order.

### RESTART-06 — Feeding the position straight from the machine

**Set up.** A real print that has actually stopped part way. Wire the
**Duet Console**'s `pos` output, or **Live View**'s `pos` output, straight
into Restart's `point`.

**Do.** Compute and compare the `printed` curves against the part standing on
the bed.

**Expect.** The chosen restart location matches where the print really stopped,
with no coordinate conversion done by hand. Check the `printed` curves against
the physical part before you run anything.

### RESTART-07 — The approach comes down from above

**Do.** Look at the beginning of the restart file, and at the `remaining`
curves near the restart point.

**Expect.** The nozzle travels across at a safe height — clear of everything
already printed, by the `clearance` distance — and only then descends onto the
restart point. It never drags sideways through the standing part. After the
approach, the rest of the job continues exactly as in the original file.

### RESTART-08 — No heating preamble, but the settings are carried over

**Do.** Read the first few dozen lines of the restart file.

**Expect.** There is **no** heat-up-and-wait block — this machine prints clay
and has nothing to heat. The settings that carry over from earlier in the
original file (units, absolute or relative positioning, feedrate mode and so
on) **are** re-stated, so the restart does not inherit an unknown state.

### RESTART-09 — Halt a real print and restart it — needs clay

This is the highest-consequence path in the whole plug-in. Do it on a small
part you are willing to lose.

**Do.** Start a print, let it run part way, and stop it. Build the restart file
using RESTART-06's wiring. Check `printed` against the part on the bed. Then
run the restart file.

**Expect.** The machine approaches from above, resumes at the right height and
the right place, and the finished part is continuous — no visible step, no
missing band, no collision on the way in.

## Results — G-code Restart

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| RESTART-01 — The socket says which kind of point it wants | | | |
| RESTART-02 — Restarting from a height | | | |
| RESTART-03 — Restarting from the percentage on the machine's display | | | |
| RESTART-04 — Restarting from a point, in a millimetre document | | | |
| RESTART-05 — The same point in an inch document | | | |
| RESTART-06 — Feeding the position straight from the machine | | | |
| RESTART-07 — The approach comes down from above | | | |
| RESTART-08 — No heating preamble, but the settings are carried over | | | |
| RESTART-09 — Halt a real print and restart it — needs clay | | | |

# 11. Duet Console

*Panel `7 Print`. No compute button. **Needs the printer connected.***

The Duet Console is the single window for driving a Duet-controlled printer
from Grasshopper: connecting, homing, jogging, capturing where the bed is and
where the nozzle touches it, checking and uploading a job, starting, pausing
and cancelling it, sending single commands, and keeping the job logs. Double-
click the component on the canvas to open the window. It has five pages:
**Dashboard**, **Control**, **Job**, **Terminal** and **Logs**.

**Safety first.** From here on the machine really moves. Keep the emergency
stop within reach, keep your hands out of the working area, and start every
session by watching one small jog move before trusting anything else.

*Set up for this section: set-up D.*

### CON-01 — The window opens with nothing filled in

**Set up.** A brand new Duet Console component with **every input empty and
unwired** — no host, no password, no G-code, nothing.

**Do.** Double-click the component to open the window. Click through all five
pages.

**Expect.** The window opens and stays open. Every page draws. Nothing crashes
and no page is blank or broken. It should tell you it is not connected, not
fail because it is not connected.

### CON-02 — The window works under both of Rhino's runtimes

Rhino 8 can run plug-ins under two different .NET runtimes, and the console's
drawing library ships separately for each. Both have to work.

**Do.** Start Rhino in the **.NET Framework** mode and open the Duet Console
window. Close Rhino, start it in the **.NET Core** mode, and open the window
again.

**Expect.** The window opens and draws correctly both times. A missing drawing
library shows up as a blank or black panel, or as an error when the window
opens.

### CON-03 — Connecting and reading the machine's state

**Set up.** `host` set to the printer's IP address, `password` set.

**Do.** Open the window and connect. Watch the Dashboard, and the `status`,
`state`, `connected` and `homed` outputs on the canvas.

**Expect.** It connects, `connected` becomes true, and `state` follows the real
machine (idle, busy, printing). The Dashboard shows the machine's own
coordinates in millimetres.

### CON-04 — Homing

**Do.** Home the machine from the Control page. Watch the machine and the
`homed`, `xHomed`, `yHomed`, `zHomed` outputs.

**Expect.** The machine homes each axis, and the four homed outputs turn true
as the axes finish. The reported position afterwards agrees with where the arm
actually is.

### CON-05 — The jog pad and the step sizes

**Do.** On the Control page, pick the smallest step size and press each
direction button once: X plus, X minus, Y plus, Y minus, Z plus, Z minus. Watch
the machine each time. Then change the step size chip and repeat one of them.

**Expect.** Every button moves the arm once, in one direction, by exactly the
step shown on the selected chip. Changing the chip changes the distance moved.
Nothing moves twice, and nothing keeps moving after the button is released.

### CON-06 — Press and hold repeats safely

**Do.** Press and **hold** one jog button for several seconds, then release.
Watch both the machine and the Terminal page.

**Expect.** The arm keeps stepping while you hold. Crucially, only **one** move
is ever in flight at a time — commands are not stacked up faster than the
machine can run them, so the arm stops promptly when you let go rather than
working through a queue of leftover moves.

### CON-07 — Every jog button says which way it will move

The lab's arm has a mirrored coordinate frame: the machine's X axis does not
point the same way as Rhino's X axis. Without a tooltip there is no way to know
which button moves the arm which way, so each button carries one.

**Do.** Hover the mouse over each of the six jog buttons in turn and read the
tooltip. Then repeat the whole hover pass:

- at Windows display scaling **100 %, 125 %, 150 % and 200 %**
  (`Settings → System → Display → Scale`; restart Rhino after each change);
- once with **no Robot Definition** wired into `robot` (that means the lab's
  SCARA arm), and once with **`generic-duet-cartesian`** wired.

**Expect.** Each tooltip names three things: the **machine axis** it moves, the
**step size** it will use, and the **Rhino direction** the arm will travel in.
It never says something camera-relative like "left" or "right". For the SCARA
arm the Rhino directions are: machine X+ → Rhino −Y, machine X− → Rhino +Y,
machine Y+ → Rhino −X, machine Y− → Rhino +X. For the Cartesian profile the two
frames match, so the tooltip says so. When jogging is not available, the
tooltip states the reason.

At every display scale, each tooltip appears next to the button it belongs to,
is fully readable, and is not clipped or misplaced.

### CON-08 — Capturing the bed origin

**Do.** Jog the nozzle to the corner of the bed you use as the origin and
capture it from the Control page. Read the `bedOrigin` output.

**Expect.** The captured point appears on `bedOrigin` in **Rhino** coordinates
and document units, and it matches where the nozzle actually is.

### CON-09 — Capturing the nozzle height

**Do.** Jog the nozzle down until it just touches the bed and capture Z zero.
Read the `zZero` output.

**Expect.** The captured height appears on `zZero` in document units and
matches the machine's reading at that moment.

### CON-10 — Driving to a list of points

**Set up.** Wire a small list of points in **Rhino** coordinates into the
`points` input — three or four positions well inside the reachable area.

**Do.** Open the go-to selector on the Control page and drive to each point in
turn.

**Expect.** Every point you wired appears in the selector. Selecting one moves
the arm to that position, once, and the reported position afterwards matches
the point you chose.

### CON-11 — The terminal only accepts commands from its list

The Terminal page lets you send single commands by hand. Only commands on an
approved list are allowed through, so a typing slip cannot send something
dangerous.

**Do.** On the Terminal page, send an ordinary allowed command (a status query,
for example). Then type a command that is **not** on the list and try to send
it.

**Expect.** The allowed command goes through and the machine's reply appears.
The disallowed one is refused before it reaches the machine, with a message
saying it is not permitted — not silently swallowed and not sent anyway.

### CON-12 — Dangerous commands need a second click

**Do.** Send one of the commands the console treats as dangerous, and watch
what happens on the first click.

**Expect.** The first click does not send anything. It arms the button and asks
you to confirm; only the second click sends. Clicking somewhere else instead
cancels it.

### CON-13 — After a controller reset, the console re-checks everything

`M999` restarts the controller board, which throws away everything the console
thought it knew about the machine's state.

**Do.** Send `M999` from the Terminal page (confirming as in CON-12) and watch
the console while the board comes back.

**Expect.** The console notices the reset, discards its cached state, and
re-establishes the connection and the machine's state from scratch — homed
flags, position, everything. It must not carry on displaying values from before
the reset as if they were still true.

### CON-14 — A job containing an unknown macro is refused

The console reads the whole job before uploading it, and refuses anything it
does not fully understand. The same reader is used by Print Stats, Interlayer
Time, Live View, Robot Simulation and G-code Restart, so a fault here shows up
in all of them.

**Set up.** Take a Klipper-dialect job (GEN-02's file) and add a line calling a
user-defined macro — for example `PRINT_START` — near the top.

**Do.** Wire it into the console's `gcode` input and run the job check on the
Job page.

**Expect.** The job is **refused**, and the message names the three extended
commands that *are* allowed. Refusing what it does not recognise is the correct
behaviour — the alternative is uploading a file that does something unexpected.

### CON-15 — The same job without the macro passes

**Do.** Remove the macro line, leaving only `SET_PRESSURE_ADVANCE`,
`SET_VELOCITY_LIMIT` and `SET_PRINT_STATS_INFO` as extended commands. Run the
job check again.

**Expect.** It validates cleanly.

### CON-16 — Values the machine does not report read as −1, never 0

**Set up.** Connected to the lab's SCARA arm, which does not report speed and
flow overrides.

**Do.** Read the `speedFactor` and `flowFactor` outputs.

**Expect.** Both read **−1**, which means "the machine did not tell us". They
must not read `0`, which would mean "the override is set to zero percent" — a
completely different and alarming statement.

### CON-17 — Uploading, simulating and running a job

**Set up.** A small job wired into `gcode`. Switch on the **Job** group and set
`jobName` to something you will recognise.

**Do.** On the Job page: check the job, upload it, and read the `uploaded`
output. Set `dryRun = true` and start it — the machine runs a firmware
simulation with no motion and reports an estimated time. Then set
`dryRun = false` and start it for real. Pause it, resume it, and cancel it.

**Expect.** The upload is verified and `uploaded` names the remote file. The
dry run produces a time estimate and the machine does not move. The real run
starts, `progress` climbs from 0 to 1, and pause, resume and cancel each do
what they say, promptly.

### CON-18 — Job logs are kept where you told them to go

**Do.** On the Logs page, set a folder with **Change location**, switch on
keeping job logs, and run a short job. Watch the **Log:** button in the window
header while the job runs, then look in the folder afterwards.

**Expect.** The header's Log: button reports the current job log's status while
the job runs and opens the Logs page when clicked. After the job, the log files
are in the folder you chose.

## Results — Duet Console

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| CON-01 — The window opens with nothing filled in | | | |
| CON-02 — The window works under both of Rhino's runtimes | | | |
| CON-03 — Connecting and reading the machine's state | | | |
| CON-04 — Homing | | | |
| CON-05 — The jog pad and the step sizes | | | |
| CON-06 — Press and hold repeats safely | | | |
| CON-07 — Every jog button says which way it will move | | | |
| CON-08 — Capturing the bed origin | | | |
| CON-09 — Capturing the nozzle height | | | |
| CON-10 — Driving to a list of points | | | |
| CON-11 — The terminal only accepts commands from its list | | | |
| CON-12 — Dangerous commands need a second click | | | |
| CON-13 — After a controller reset, the console re-checks everything | | | |
| CON-14 — A job containing an unknown macro is refused | | | |
| CON-15 — The same job without the macro passes | | | |
| CON-16 — Values the machine does not report read as −1, never 0 | | | |
| CON-17 — Uploading, simulating and running a job | | | |
| CON-18 — Job logs are kept where you told them to go | | | |

# 12. Live View

*Panel `7 Print`. No compute button. **Needs the printer connected and
printing.***

Live View draws the machine while it prints: the bed plate, the arm at the real
nozzle position, and the layer growing as the job goes on. It only reads from
the machine — it never sends a command. It can also record the nozzle path
with timestamps to a CSV file.

The picture is drawn straight into the Rhino viewport and animated smoothly,
without re-running the Grasshopper canvas, so you can keep editing the
definition while a print runs.

Several checks below ask you to read numbers out of Live View's `info` output.
Wire a large Panel to it and keep it visible; it is the main instrument in this
section.

*Set up for this section: set-up D, with a real job running. Wire the job's
G-code into Live View's `gcode` so the layer preview follows the print.*

### LIVE-01 — The arm animates without the canvas re-solving

**Do.** Start a print and watch the viewport and the Grasshopper canvas
side by side for a minute or two.

**Expect.** The arm and the growing layer animate smoothly. The canvas is
**not** re-solved for every frame — components do not flash as if recomputing,
and the definition stays editable while the print runs. Read `info`: the count
of canvas solves caused by the machine should stay at zero.

### LIVE-02 — The arm holds still rather than diving through the bed

After homing, while the arm travels to the start of the print, the controller
sometimes reports a nozzle height far below the bed. That reading is wrong, and
the display must refuse it rather than draw it.

**Do.** Watch the viewport during the approach after homing, from the moment
homing finishes until printing starts. Read `info` afterwards.

**Expect.** The drawn arm **holds its last believable position**, shown as a
ghost, instead of plunging below the bed plate and springing back. `info` says
something like `pose held (uncertain): Z … below the machine minimum …`, and a
`validity:` line counts how many readings were refused. The raw readings are
still written to the diagnostics file and to `print.csv` — they are hidden from
the picture, not thrown away.

### LIVE-03 — An older controller board is never wrongly refused

The lab's board runs an older vendor firmware that answers in a legacy format
and does not report its own travel limits. Live View must not treat "no limits
reported" as "outside the limits".

**Do.** Run a **full** print on that board — homing, printing, a pause, a
resume, and a cancel — and watch `info` and the drawn arm throughout.

**Expect.** No legitimate reading is ever refused. Because the board reports no
travel limits, `info` shows the machine envelope as **UNKNOWN**, and on that
path no position is ever rejected for being outside it.

### LIVE-04 — The trail breaks only where it should

The trail is the line showing where the nozzle has actually been. It should be
broken wherever there is a genuine gap in knowledge, and continuous everywhere
else.

**Do.** During a print, cause each of these in turn and watch the trail:
disconnect and reconnect the network; run a homing move; and print the same
file a second time. Then read the break counts from `info` — look for a line
beginning `breaks:` that attributes each break to a cause.

**Expect.** The trail breaks at the reconnect, at the homing move and between
the two prints. Inside an ordinary layer it stays continuous. Judge this from
the attributed break counts in `info`, not by squinting at the picture.

### LIVE-05 — No break at a seam, and honest gaps when sampling is slow

A seam is where the nozzle slows down to close a contour. Slowing down is not a
gap in knowledge and must not break the trail.

**Do.** Watch the trail across several seams during a normal print. Then set
`pollMs` on the Duet Console to `2000` (one reading every two seconds), print
a small contour, and watch again.

**Expect.** No break at any seam. At the two-second interval, where readings are
genuinely too far apart to know what happened in between, the trail shows
**points with gaps between them** — it does not draw straight lines across the
part joining readings that are far apart.

### LIVE-06 — The intended path is drawn distinctly from the real one

**Do.** With the job G-code wired into `gcode`, look at the intended path drawn
in the viewport alongside the recorded trail. Find a place where the job homes
(`G28`) and look at what is drawn across it. Read `info`.

**Expect.** Printing moves are drawn **solid**; travel, seam and coast moves are
drawn **dotted and dimmer**. The recorded trail stays visually distinct from
both. **No straight line is drawn across a `G28`** — the nozzle does not travel
in a straight line to the home position, so drawing one would be a lie. `info`
reports how many printing runs and travel runs it found, and any simplification
tolerance it applied.

### LIVE-07 — Waking up from idle is immediate

**Do.** Let the machine stand still for more than a second, then jog it or let
the print resume. Watch the first moment of movement, then read the frame
interval figures in `info`.

**Expect.** The animation picks up at full frame rate **immediately**. There is
no lag of half a second before it catches up. In `info`, the 95th-percentile
frame interval returns to about 33 ms without waiting out a slow beat.

### LIVE-08 — A long print does not grow without limit

**Do.** Run a print long enough to record more than 5,000 nozzle positions —
once as one continuous run, and once with deliberate breaks in it (as in
LIVE-04). Read `info` and the `trail` output at the end of each.

**Expect.** `info` and `trail` say explicitly that the trail was compacted
(points merged where doing so moves the line by no more than 0.2 mm) and that
older points were pruned. Finished runs are compacted; a very long run still in
progress may lose its oldest points. Write down how much history is still
visible and what the frame and overlay timings were.

### LIVE-09 — Measure the cost of drawing, three ways

This is a measurement, not a yes/no check. Write down every number.

**Set up.** One saved job, run three times on the same document. Switch off the
components' automatic outputs. Run it: (a) with the **Duet Console alone**,
(b) with the console **plus Live View**, (c) with the console plus Live View
**plus Robot Simulation**.

**Do.** After each run, read these out of Live View's `info` and record them:

- canvas solves caused by the machine — should be **0**;
- how many times the job G-code was parsed — should be **1**, with the cache-hit
  count rising after that;
- redraws against frame callbacks — should be at most one redraw per callback;
- the `cadence:` line: the interval you asked for, the interval actually used
  after the session's limit, and the interval actually measured;
- the `latency:` line: the round trip for one position query, the full time for
  one poll, and how old the displayed position is;
- frame build time, median and 95th percentile;
- how long the interface waited on callbacks, 95th percentile.

Then leave the machine idle and watch.

**Expect.** The numbers above come out as stated, and while the machine is idle
nothing repaints — `info` reaches "at rest" and stays there.

**Note.** Asking for a faster interval than 250 ms has no effect: the session
limits it to 250 ms and says so out loud. At 40 mm/s that is 10 mm of nozzle
travel between two readings, which is why the trail can look slightly faceted.
That limit is deliberate and is waiting on the measurements from this check and
the next one.

### LIVE-10 — Measure what the diagnostic capture actually costs

**Do.** Run the **same 60-second workload** twice: once with the diagnostic
capture switched on, once with it off. Both times, read the round trip for a
position query, the full poll time and the acquisition interval from `info`,
and record them.

**Expect.** Two sets of three numbers, written down. This check stays open
until the numbers exist — instrumentation being present is not an answer, and
the round-trip figure alone is not an answer either.

### LIVE-11 — Recording, and proving nothing was dropped

**Set up.** Switch on the **Print path log** group: `logEnable = true`, a
`logFolder`, `logEveryN = 5`.

**Do.** Run **one normal print** and **one print that you halt part way**. For
each of them:

- **While it is recording**, look at the console's logging-health line and
  confirm the staging area is being written. Do this during the print —
  successful copying removes the staging area afterwards, so looking at the
  folder later proves nothing.
- Afterwards, find `diagnostics.jsonl` in the destination folder.
- Open the manifest and read `outcome` and the three per-run drop counters.

**Expect.** For the halted print, `outcome` reads **Halted**. For the normal
print, all three drop counters read **zero**. Write down all four numbers for
each print.

### LIVE-12 — Trace one bad reading from the machine to the screen

**Do.** With diagnostic capture on, watch for a moment where the displayed
position jumps somewhere impossible — this happens most often just after a
layer change or a seam. Capture the diagnostics around it and follow that one
reading through the four stages: the machine's raw answer, the parsed reading,
the recorder, and the drawn picture. Note which stage first shows the bad
value.

**Expect.** The bad value is already present in the machine's own raw answer —
the plug-in is passing on a wrong reading, not inventing one. On this run,
confirm two things: the arm now **holds** instead of diving (as in LIVE-02),
and the seams and travel moves are drawn (as in LIVE-06). If any stage after
the raw answer is the first to show the bad value, that is a new fault: report
it with the captured file.

## Results — Live View

Fill this in when you have worked through the checks above.

| Check                                                         | Result     | Severity     | Notes                                                         |
|------------------------------------------------------------|----------------|----------------|------------------------------------------------------------------|
| LIVE-01 — The arm animates without the canvas re-solving | | | |
| LIVE-02 — The arm holds still rather than diving through the bed | | | |
| LIVE-03 — An older controller board is never wrongly refused | | | |
| LIVE-04 — The trail breaks only where it should | | | |
| LIVE-05 — No break at a seam, and honest gaps when sampling is slow | | | |
| LIVE-06 — The intended path is drawn distinctly from the real one | | | |
| LIVE-07 — Waking up from idle is immediate | | | |
| LIVE-08 — A long print does not grow without limit | | | |
| LIVE-09 — Measure the cost of drawing, three ways | | | |
| LIVE-10 — Measure what the diagnostic capture actually costs | | | |
| LIVE-11 — Recording, and proving nothing was dropped | | | |
| LIVE-12 — Trace one bad reading from the machine to the screen | | | |

# Appendix A — Already known, please do not report

These are known and are being dealt with. Seeing one of them is not a new
finding. Everything else you see is worth reporting.

- **A single physical jerk, once per print, on the SCARA arm.** Known and
  unfixed; nothing in the motion path, the firmware, the macros or the
  generated file has been changed. If you see it, it is still worth recording
  the evidence: the time and layer it happened at, which axis moved, whether
  the machine recovered or lost its position, which file was printing, and what
  the controller's own diagnostics said. Do not open it as a new fault.
- **The controller reports nozzle positions below the bed during the approach
  after homing.** The wrong reading comes from the machine itself. The display
  refuses it and holds the last good position instead (LIVE-02), but the
  underlying reading is still wrong.
- **The live trail can look slightly faceted.** Positions are read at most four
  times a second, which at normal printing speed is about 10 mm of travel
  between readings. This limit is deliberate and stays until the measurements
  in LIVE-09 and LIVE-10 exist.
- **Some internal rough edges that no normal use reaches.** A filter that
  modifies a reading after publishing it; duplicate sheet names in a
  spreadsheet export; a cache-clearing routine that leaves its counters
  populated; and a session manager that is not thread-safe in the abstract
  although every current caller runs on one thread. All known, none of them
  reachable from anything in this sheet.
- **Heating, fans, filament and thermoplastic printing are not part of this
  product.** Neither is controlling a Klipper machine over the network. Klipper
  here is only a file dialect: you generate the file and transfer it yourself.
- **Installing and upgrading are not part of this sheet.**

# Appendix B — For the maintainer: old check numbers

The tester does not need this. It maps the checks above onto the numbering used
in earlier rounds, so that older notes and open items can be reconciled.

| Here | Previously | Here | Previously |
|---|---|---|---|
| Smoke check | 0.6 | LEG-01 … LEG-04 | 4.1, 4.2, 4.3, 4.4 |
| SLICE-01 … SLICE-07 | D.1 – D.7 | LEG-05 … LEG-08 | 4.4a, 4.4b, 4.4c, 4.4d |
| SLICE-08 … SLICE-16 | E.1 – E.9 | LEG-09 … LEG-17 | 4.5 – 4.13 |
| SLICE-17 … SLICE-22 | F.1 – F.6 | SET-01 … SET-16 | 6.1 – 6.16 |
| SLICE-23, 24 | J.1, J.2 | ROBOT-01 | 7.12 |
| SLICE-25, 26, 27 | J.4, J.3, J.5 | GEN-01 … GEN-03 | 7.1, 7.5, 7.6 |
| GRAPH-01 … GRAPH-10 | 3.1 – 3.10 | GEN-04 … GEN-07 | 7.16 – 7.19 |
| TESTGEO-01 … TESTGEO-08 | G.1 – G.8 | RESTART-01 | H.1 |
| TESTGEO-09, TESTGEO-10 | J.6, J.7 | RESTART-02, RESTART-03 | new |
| OVER-01 … OVER-05 | 4a.1 – 4a.5 | RESTART-04 … RESTART-08 | H.2 – H.6 |
| TIME-01 … TIME-08 | 5.1 – 5.8 | RESTART-09 | J.8 |
| TIME-09 | 10a.13 | CON-01, CON-02 | 1.11, 1.10 |
| CON-03, CON-04 | new | CON-05 … CON-10 | 8.3 |
| CON-11 … CON-13 | 8.7 | CON-14, CON-15 | 7.13, 7.14 |
| CON-16 | 8.12 | CON-17, CON-18 | new |
| LIVE-01 | 8.10 | LIVE-02 | 10a.43 |
| LIVE-03, LIVE-04 | 10a.9, 10a.10 | LIVE-05, LIVE-06 | 10a.30, 10a.44 |
| LIVE-07, LIVE-08 | 10a.45, 10a.46 | LIVE-09, LIVE-10 | 10a.29, 10a.12 |
| LIVE-11, LIVE-12 | 10a.11, 10a.8 | | |

Dropped on purpose: the packaging and release-script checks (A.1 – A.6,
0.2 – 0.5, 1.10's install half), the documentation checks (K.2 – K.6) and the
release sign-off list. The once-per-print jerk (8.15) is in Appendix A rather
than as a check, because it is an open investigation and not a pass/fail.
