# Generate-ComponentPages.ps1
# Generates one wiki page per component (docs/github/wiki/IAMLab-*.md), the
# Components index, the sidebar, and the component rows of IMAGES-TODO.md from
# the component sources in IAMLabSlicer/Components. Run again after inputs,
# outputs, or tooltips change. The plain-language introductions and notes live
# in the $P table below; edit them here, not in the generated files.
#
#   powershell -File docs\github\tools\Generate-ComponentPages.ps1

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$src = Join-Path $repo "IAMLabSlicer\Components"
$w = Join-Path $repo "docs\github\wiki"
$g = Join-Path $repo "docs\github"
$enc = [Text.UTF8Encoding]::new($false)

# A ParamSpec/output description is normally a concatenation of string
# literals, but a few components share text through a `const string NAME =
# "...";` field (e.g. the Duet/Klipper password note) and splice it in with
# `+ NAME +`. $litQuoted extracts those const declarations (literal-only,
# since a const initializer cannot itself reference another identifier
# here); $lit is the wider pattern used inside ParamSpec/output argument
# lists, which also accepts a bare identifier as one concatenation term so
# such fields are not silently dropped from the generated tables.
function JoinLits([string]$s, $consts) {
  if (-not $consts) { $consts = @{} }
  $parts = [regex]::Matches($s, '"(?:[^"\\]|\\.)*"|[A-Za-z_]\w*') | ForEach-Object {
    $tok = $_.Value
    if ($tok.StartsWith('"')) { $tok.Substring(1, $tok.Length - 2) -replace '\\"','"' }
    elseif ($consts.ContainsKey($tok)) { $consts[$tok] }
    else { "" }
  }
  ($parts -join "")
}
$litQuoted = '(?:"(?:[^"\\]|\\.)*"\s*\+?\s*)+'
$lit = '(?:(?:"(?:[^"\\]|\\.)*"|[A-Za-z_]\w*)\s*\+?\s*)+'
$comps = @()
foreach ($f in Get-ChildItem $src -Filter *Component.cs) {
  $t = [IO.File]::ReadAllText($f.FullName)
  $consts = @{}
  foreach ($cm in [regex]::Matches($t, 'const\s+string\s+(\w+)\s*=\s*(' + $litQuoted + ')\s*;')) {
    $consts[$cm.Groups[1].Value] = JoinLits $cm.Groups[2].Value $consts
  }
  $m = [regex]::Match($t, '(?s)base\(\s*"(IAMLab [^"]+)",\s*"([^"]+)",\s*(.*?),\s*(?:"IAMLab",\s*)?"(\d [A-Za-z-]+)"\s*\)')
  if (-not $m.Success) { continue }
  $inputs = @()
  foreach ($pm in [regex]::Matches($t, 'new ParamSpec\(\s*"([^"]*)",\s*"([^"]*)",\s*(' + $lit + '),\s*"([^"]*)",\s*PType\.(\w+)(?:,\s*GH_ParamAccess\.(\w+))?\s*\)((?:\s*\.\w+\([^)]*\))*)')) {
    $hint = $pm.Groups[7].Value; $def = ""
    $h = [regex]::Match($hint, '\.Hint\(\s*[^,]+,\s*[^,]+,\s*([^,\)]+)'); if ($h.Success) { $def = $h.Groups[1].Value.Trim() }
    $hb = [regex]::Match($hint, '\.HintBool\(\s*(\w+)'); if ($hb.Success) { $def = $hb.Groups[1].Value }
    $ht = [regex]::Match($hint, '\.HintText\(\s*"([^"]*)"'); if ($ht.Success) { $def = $ht.Groups[1].Value }
    $inputs += [pscustomobject]@{ name=$pm.Groups[1].Value; nick=$pm.Groups[2].Value; desc=(JoinLits $pm.Groups[3].Value $consts); group=$pm.Groups[4].Value; type=$pm.Groups[5].Value; access=$pm.Groups[6].Value; default=$def }
  }
  $outIdx = $t.IndexOf("RegisterOutputParams"); $outputs = @()
  foreach ($pm in [regex]::Matches($t, 'pManager\.Add(\w+)Parameter\(\s*"([^"]*)",\s*"([^"]*)",\s*(' + $lit + '),\s*GH_ParamAccess\.(\w+)')) {
    $rec = [pscustomobject]@{ name=$pm.Groups[2].Value; nick=$pm.Groups[3].Value; desc=(JoinLits $pm.Groups[4].Value $consts); type=$pm.Groups[1].Value; access=$pm.Groups[5].Value }
    if ($outIdx -ge 0 -and $pm.Index -gt $outIdx) { $outputs += $rec } else { $inputs += [pscustomobject]@{ name=$rec.name; nick=$rec.nick; desc=$rec.desc; group="core"; type=$rec.type; access=$rec.access; default="" } }
  }
  $groups = @(); $gi = $t.IndexOf("ToggleGroups")
  if ($gi -ge 0) { foreach ($gm in [regex]::Matches($t.Substring($gi, [Math]::Min(3000, $t.Length - $gi)), '\(\s*"(\w+)",\s*"([^"]+)"\s*\)')) { $groups += [pscustomobject]@{ id=$gm.Groups[1].Value; label=$gm.Groups[2].Value } } }
  $comps += [pscustomobject]@{ file=$f.Name; name=$m.Groups[1].Value; nick=$m.Groups[2].Value; desc=(JoinLits $m.Groups[3].Value $consts); panel=$m.Groups[4].Value; hidden=($t -match 'GH_Exposure\.hidden'); heavy=($t -match 'override\s+bool\s+Heavy\s*=>\s*true'); groups=$groups; inputs=$inputs; outputs=$outputs }
}

$P = @{}
$P["IAMLab Slice"] = @{ intro = @'
IAMLab Slice cuts a shape into layers. The input can be a mesh, a Brep,
or a surface, or a list of them. The result is a tree of closed curves
with one branch per layer; each branch holds the wall curves first and
the infill after them. Three slicing modes are available: flat layers
(planar), layers shaped like cones (conical), and layers that follow
the surface. The component has a compute button and runs
only when the button is clicked.
'@; notes = @'
- `layerHeight` and `spacing` must be repeated on IAMLab G-code
  Generator (`layerHeight`, `width`), because the G-code Generator does
  not read them from the curves.
- `mode`: `0` planar is the normal choice. `1` conical is the
  non-planar mode; see [[Non Planar Slicing]] before using it, and
  enable the `varh` group on the G-code Generator afterwards. The heat
  method (`mode = 2`) was retired in 4.0.0; a saved `2` slices planar
  and warns.
- `infill`: `4` (fermat) and `3` (spiral) produce one continuous path
  per layer; `1` (zigzag) and `2` (concentric) produce many separate
  lines or rings. Concentric, spiral and fermat start at the seam of the
  innermost wall, so the hop from wall to infill is one `spacing`. A
  layer with several islands falls back to separate rings.
- Adaptive layer height (group): layers are thin where the surface is
  shallow and thick where it is steep, between `hMin` and `hMax`;
  `info` reports the realised range. Planar mode only.
- `cornerRadius` rounds sharp corners slightly so that the nozzle does
  not over-fill them.
- The Spiralize group prints the whole part as one continuous helix
  (vase mode). Every layer must then consist of exactly one closed
  curve; `wallCount` and `infill` are ignored and `info` says so.
- Brep and surface inputs are meshed internally with a tolerance-driven
  setting. Prepare the mesh upstream if you need control over it.
- With a list of shapes, the shapes are printed one after the other;
  the Multi-object group sets the order.
'@; image = "Slice with a cylinder from Test Geometry connected; the viewport shows the contour tree, one colour per layer." }
$P["IAMLab Graph Mapper"] = @{ intro = @'
IAMLab Graph Mapper changes a list of numbers by passing them through a
curve that you draw on the component. A typical use is a flow or speed
value that should change gradually from the bottom to the top of a
print: feed the layer numbers in, draw the curve, and read the remapped
values out. The component does the same job as the Graph Mapper that
ships with Grasshopper, with more curve types and editable points.
'@; notes = @'
- Curve types are chosen from the dropdown on the component. Polyline
  and Spline are fully editable: double-click adds a point, double-click
  on a point removes it, and any other curve type can be converted into
  them.
- Spline points carry tangent handles. Hover a point to reveal them; a
  plain drag keeps the point smooth (both handles follow together),
  Shift moves one side only and turns the point into a corner, Ctrl
  snaps the dragged handle's slope to a quarter step, and double-click
  a handle returns it to the automatic tangent. A point converted from
  another curve type starts with automatic handles the same way.
- While dragging a point, Ctrl snaps to steps of 0.05 and Shift to
  steps of 0.01. Ctrl+Z undoes the last edit; the reset button restores
  the current curve type.
- The Domain group sets which input values map to the left and right
  edge of the graph and which output values the bottom and top edge
  represent.
'@; image = "The Graph Mapper on the canvas with a custom polyline curve and a point being dragged." }
$P["IAMLab Version"] = @{ intro = @'
IAMLab Version shows which version of the plugin is installed, when the
build was made, and which .NET runtime Rhino loaded. The summary line is
the first thing to include in a bug report. The component can also
check whether a newer release is available, but only when `check` is
set to true; by default no network request is made.
'@; notes = @'
- The update check contacts the release source at most once per Rhino
  session and only while `check` is true. If no release source is
  configured in the build, the check reports that no source is
  available.
'@; image = "The Version component with a panel on the summary output." }
$P["IAMLab Load Settings"] = @{ intro = @'
IAMLab Load Settings points the document at a folder of saved settings
files, one per component. Every IAMLab component can save its current
inputs with "Save settings…" on its right-click menu, which writes a
small JSON file next to the ones this component reads. From then on,
the "⇤ inputs" button on any IAMLab component builds its sliders,
toggles, panels and value lists from the matching file instead of the
built-in defaults, and falls back to the defaults where the folder has
nothing to say. This makes it easy to keep one settings folder per
material or per machine and switch between them.
'@; notes = @'
- Files are matched by the `component` name written inside the JSON,
  never by file name, so a file can be called `clay-fast.iamset.json`
  or anything else that is meaningful to you.
- The `apply` button applies every matching file to the canvas at once,
  as one undo step. An input with nothing connected gets a new object
  carrying the file's value; an input already connected to exactly one
  slider, toggle, panel or value list is updated in place; anything
  more complex upstream is left alone and listed in the report.
- `watch` re-checks the folder on the next solve when a file's name,
  size or timestamp changed. Turn it off only if the per-solve
  directory listing is a measured problem.
- `unmatched` lists files this build could not use: unreadable files,
  two files claiming the same component, and files naming a component
  that does not exist in this build.
- Only one Load Settings component is allowed per document; a second
  one is refused rather than silently taking over.
- Geometry, points, planes, colours and tree inputs are never saved or
  loaded; the save report says so.
'@; image = "Load Settings pointed at a folder, with the report output listing matched and unmatched files." }
$P["IAMLab Place"] = @{ intro = @'
IAMLab Place moves the sliced layers to the print position on the bed
and decides where each layer starts and ends (the seam). The bottom
centre of the part lands on `printCenter`. The seam can be kept as it
is, aligned in one direction, rotated a little on every layer, placed
nearest to a point, guided along a curve, or kept above the seam of the
layer below.
'@; notes = @'
- `printCenter` overrides the print centre of a connected
  [[IAMLab Print Bed]]. Without either, the built-in default is the
  usual position on the lab bed.
- `seamMode`: `0` keep, `1` align (default; `seamAngle` sets the
  direction), `2` stagger (`seamAngle` is the step per layer), `3`
  nearest (`seamPoint`), `4` guide (`seamCurve`, drawn in world
  coordinates where the placed part stands), `5` track (each seam above
  the seam of the layer below).
- Whether the placed part can be reached by the robot is checked by
  [[IAMLab Robot Simulation]], not here.
'@; image = "Place with seamMode = 1; the viewport shows the placed part with the seam points lined up in a column." }
$P["IAMLab Print Bed"] = @{ intro = @'
IAMLab Print Bed describes the print bed once: how large the plate is,
where the plate is in the Rhino document, and where a part is placed by
default. The `definition` output is connected to [[IAMLab Place]],
[[IAMLab Robot Simulation]], and [[IAMLab Live View]], so that all of
them use the same bed. Inputs on those components still win when they
are connected explicitly.
'@; notes = @'
- `center` is the middle of the plate. `printCenter` is where the
  bottom centre of a part lands, which is usually not the middle of the
  plate. Keep the two separate.
- The Reach group narrows the ring-shaped area that a SCARA arm can
  reach, for example when part of the plate is physically blocked.
  Normally the robot profile supplies the reach, and both inputs stay
  at 0.
'@; image = "Print Bed connected to Place and Robot Simulation; the viewport shows the plate outline and the print centre." }
$P["IAMLab G-code Generator"] = @{ intro = @'
IAMLab G-code Generator turns the placed layers into the G-code file
that the printer runs. The component handles the parts that are
specific to clay and paste: how much material is pushed out per
millimetre of path, how the ram pulls back and pushes again at a seam,
how fast the nozzle moves, and how travel moves lift over material that
is already printed. Many settings live in optional groups that are
switched on with a right-click only when they are needed. The
component has a compute button.
'@; notes = @'
- `layerHeight` must be the same as on [[IAMLab Slice]], and `width`
  should match the `spacing` used there.
- `ramDiameter` is the inside diameter of the material cartridge. It
  sets how far the ram must move for a given amount of material.
- `retraction` chooses how seams are handled: `0` never pulls the ram
  back, `1` connects seams that are close together without pulling
  back, `2` pulls back and pushes again at every seam.
- `zOffset` is a small correction for the nozzle height, with a default
  of 0. The layers are already at the right height, so do not enter the
  layer height here.
- `robot`: without a Robot Definition the G-code is written for the
  SCARA arm of the lab, whose X and Y axes are mirrored. On an ordinary
  printer, connect a [[IAMLab Robot Definition]] with
  `generic-cartesian`. See [[Robots and Machine Control]].
- `firmware` picks the G-code dialect: Duet RRF, Marlin, or Klipper.
  Left unwired, it follows the connected Robot Definition's own default
  dialect (Duet RRF for the Potterbot or no definition at all).
- Fdm group: switches the extrusion model from the ram/pump math paste
  printing needs to a filament model for a thermoplastic printer -
  filament diameter, flow, retraction in millimetres of filament,
  hotend and bed temperatures (with first-layer overrides), the wait
  for temperature after homing, and the part-cooling fan with a
  from-layer and a boost. A thermoplastic Robot Definition switches
  this model on with sensible defaults even while the group itself is
  collapsed; `filamentMm` reports the filament length used instead of
  the ram distance.
- Arcs group: the component replaces runs of short straight segments by
  arc commands (G2/G3), which produces smoother motion and smaller
  files. The ArcWelder group uses the external ArcWelder program for the
  same job and needs the path to `ArcWelderConsole.exe`; if the program
  is missing or fails, the G-code stays unwelded and a message is
  reported. Use one of the two, not both.
- Pump group: for a pump instead of a ram, the component calculates
  the pump setting that delivers the required flow, from a measured
  table of setting against flow rate. The shipped table is a
  placeholder that must be replaced with your own measurements. The
  group never changes the extrusion values in the G-code.
- Varh group: enable after adaptive, conical, or heat-method slicing,
  because the layer height then changes from point to point. The two
  inputs limit how far the flow may deviate from the nominal value.
- Ecomp group: an experimental correction for extra flow on leaning
  walls. Leave the group off unless you have measured the value with
  a test print of an inclined wall.
- Skirt, Floors, and Header override: a purge ring before the part,
  solid base layers with an optional pause, and a replacement for the
  automatic header and footer.
- `start` and `startGcode` give the first print position and a short
  G-code block that moves the nozzle there, so that the nozzle height
  can be set before the print starts.
'@; image = "G-code Generator with the core inputs visible and the preview (blue) and travels (ochre) drawn in the viewport." }
$P["IAMLab G-code Restart"] = @{ intro = @'
IAMLab G-code Restart rebuilds a G-code file so that a print can
continue after a failure. You tell the component where the print
stopped, as a height, as the percentage shown on the Duet, or as the
last position, and the component removes everything that was already
printed, adds a safe approach to the restart point, and keeps the rest
of the job. The component has a compute button.
'@; notes = @'
- Connect only one of `z`, `percent`, and `point`. When more than one
  is connected, `point` wins over `percent`, and `percent` wins over
  `z`.
- `percent` works exactly only when `gcodePath` points to the original
  file, because the Duet counts bytes of the file. Without the file,
  the position is approximate.
- `point` is in Rhino document coordinates, for example a Live or
  Console position output. It is not raw Duet machine X/Y/Z; convert
  raw firmware coordinates to the Rhino frame before supplying it.
- Check the `printed` output against the part on the bed before
  starting the restart file: the curves must match what is already
  printed.
- `primeE` pushes some material before the first move to refill the
  nozzle after an air gap.
'@; image = "G-code Restart with the printed (grey) and remaining (blue) curves shown on a half-finished part." }
$P["IAMLab Save G-code"] = @{ intro = @'
IAMLab Save G-code writes the G-code to a file whose name contains the
date and time, so that no file is overwritten by accident. The file is
written only when `write` becomes true. Connect a Button component,
not a Toggle, so that every click writes exactly one file.
'@; notes = @'
- The file is written in the background. The component first reports
  "writing", and `written` becomes true on the following solve.
- With the Override group, the timestamp and the name format can be set
  by hand.
'@; image = "Save G-code with a Button connected to write and a panel showing the written path." }
$P["IAMLab Robot Definition"] = @{ intro = @'
IAMLab Robot Definition selects the machine. The `definition` output is
a short text that other components read from their `robot` input, so
that the machine is chosen once for the whole definition. Available
profiles: `none` (no machine, toolpath only), `generic-cartesian` (an
ordinary printer), `generic-duet-cartesian` (an ordinary printer with a
Duet board), `potterbot-scara-v4` (the SCARA arm of the lab),
`generic-klipper` (a Klipper machine controlled through Moonraker),
`generic-klipper-fdm` and `generic-marlin-fdm` (thermoplastic printers -
Klipper through Moonraker, or Marlin, with the filament extrusion model
on by default).
'@; notes = @'
- The profile can also be typed into a text panel: `"scara"` or
  `"none"` are accepted.
- Components that have no definition connected behave as before version
  2.0: the G-code Generator and the Machine Console assume the SCARA
  arm of the lab. See [[Robots and Machine Control]].
- `process` reports whether the profile prints paste (ram or pump) or
  thermoplastic (filament, heaters); `flavor` reports which G-code
  dialect the G-code Generator uses while its own `firmware` input is
  left unwired.
'@; image = "Robot Definition with the profile dropdown open, connected to the G-code Generator and Robot Simulation." }
$P["IAMLab Robot Simulation"] = @{ intro = @'
IAMLab Robot Simulation shows a print before it runs. The component
draws the toolpath and the printed material, and, when a Robot
Definition with an arm is connected, poses the arm along the path and
checks that every point can be reached within the joint limits and
speeds of the arm. Dragging the `sim` slider from 0 to 1 plays the
print. With the profile `none`, the component shows the toolpath and
the material only.
'@; notes = @'
- A point that cannot be reached sets `reachOK` to false, lists the
  point in `problems`, and raises a red error on the component. Do not
  run a program while that error is shown.
- Printed material is drawn as a bead; travel moves are drawn as thin
  ochre lines.
- The Bed group draws a bed of the given size; a connected
  [[IAMLab Print Bed]] supplies the same values.
- `armMode` sets which of the two possible arm positions the SCARA uses
  when the G-code does not start with a homing command.
'@; image = "Robot Simulation at sim = 0.6 with the posed SCARA, the bead printed so far, and the bed outline." }
$P["IAMLab Overhang"] = @{ intro = @'
IAMLab Overhang checks how far each part of a layer hangs out over the
layer below. Segments within the warning angle are green, segments
between the warning and the failure angle are orange, and segments
beyond the failure angle are red. The default angles of 25° and 45° are
placeholders and should be measured for your material with a test print
of a stepped cone. The component has a compute button.
'@; notes = @'
- `angles` measures the overhang relative to the layer below, which is
  what matters for non-planar layers. `anglesWorld` measures the angle
  against the vertical instead.
- The Export group writes a per-layer summary as a CSV file.
- `legend` is an `iamlab-legend:v1` wire carrying the same three colour
  classes drawn in the viewport - connect it to [[IAMLab Legend]] so a
  legend never disagrees with the painted colours.
'@; image = "A twisted vase after Overhang with green, orange, and red segments in the viewport." }
$P["IAMLab Interlayer Time"] = @{ intro = @'
IAMLab Interlayer Time shows, for every point of the print, how long
ago the material directly underneath it was laid down. This interlayer
time governs adhesion and collapse in clay: too short, and the layer
below has not stiffened enough to carry the next one; too long, and it
has skinned over and the next layer will not bond to it. The clock
comes from the G-code's own feeds, or from the timestamps of a
connected print log when one is wired in. The toolpath, or the printed
beads, are coloured on a Plasma scale between your two thresholds.
'@; notes = @'
- `layer` scrolls the preview: wire a slider to step through the print
  one layer at a time. It also cuts the `perLayer`, `times`, `path` and
  `colors` outputs down to the layers it leaves visible - `report`,
  `layers` and `info` always describe the whole print.
- `minTime` flags interlayer times that are too short (bead below still
  soft); `maxTime` flags times that are too long (bead below skinned
  over). Either is off at `0`.
- `shade` tells adjacent layers apart when their times are similar:
  `0` none, `1` alternating tint per layer (default), `2` a
  bottom-to-top ramp. Shading is display only - the `colors` output
  always carries the plain unshaded scale.
- With a print log wired to `log` (from [[IAMLab Live View]] or a saved
  CSV), the real recorded timestamps replace the estimate from feeds.
- The `legend` output is an `iamlab-legend:v1` wire - connect it to
  [[IAMLab Legend]] so the legend always matches the colours drawn here.
- The Feed override group only matters for the estimated clock; it has
  no effect once a print log supplies real timestamps.
'@; image = "Interlayer Time on a clay print, layer slider partway up, alternating layer shading visible." }
$P["IAMLab Legend"] = @{ intro = @'
IAMLab Legend draws a legend for any data range: a continuous ramp,
thresholded classes, named groups, or a size scale. It exists because
Grasshopper has no legend of its own. Wire the `legend` output of
[[IAMLab Overhang]] or [[IAMLab Interlayer Time]] and the legend always
matches the colours those components painted; wire your own `values`,
`min` and `max` instead to describe any other scale. Every explicit
input on the component overrides the matching field of the wire, so
one Legend can be nudged - a different title, a bigger font - without
detaching it from the source.
'@; notes = @'
- `type`: `0` continuous ramp, `1` classes (thresholded bands), `2`
  groups (named categories), `3` size (a circle-size scale).
- `scheme` picks a named colour scheme (`plasma`, `viridis`, the four
  IAMLab family ramps, `greys`, `traffic`) or `custom`, which reads the
  `colors` input instead.
- `placement`: `0` a fixed overlay anchored to a corner or edge of the
  viewport, `1` fixed to a `plane` in model space, `2` floating at a
  `point` as a camera-facing billboard whose screen size stays constant
  while you orbit or zoom.
- `orientation` chooses horizontal or vertical for any placement.
- The component doubles as a colouriser: the `colors` output gives one
  colour per `values` entry from the same scale, so any geometry can be
  painted consistently with the legend.
- `mesh`, `labels` and `frames` give a bakeable vertex-coloured legend
  and its text planes for the plane and floating placements.
- `spec` re-emits the resolved scale as an `iamlab-legend:v1` wire, so
  one Legend can feed the exact same scale to another.
'@; image = "Legend showing the Interlayer Time wire as a floating billboard next to the printed part." }
$P["IAMLab Print Stats"] = @{ intro = @'
IAMLab Print Stats reads a G-code file, or a toolpath, and reports the
printed length, the travel length, the amount of material in cubic
centimetres and grams, an estimated print time, and a table with one
row per layer. The report is a quick check before a long print.
'@; notes = @'
- With G-code input and `ramDiameter` set, the material amount is
  calculated from the extrusion values in the file. With a toolpath, or
  with `ramDiameter = 0`, the amount is estimated from `width` and
  `height`.
- `accel` switches on a time model that accounts for speeding up and
  slowing down at corners. Without a value, the time is a lower bound.
- With a Robot Definition for an arm connected, the time estimate also
  respects the joint speeds of the arm, which slow the print near the
  edge of the reachable area.
'@; image = "Print Stats with a panel on the report output." }
$P["IAMLab DoE Design"] = @{ intro = @'
IAMLab DoE Design plans a series of test prints. You name the settings
that should be tuned, give the lowest and highest value for each, and
choose a plan type. The component produces a run table with one row
per test print and one column per setting, plus empty columns for the
measurements. The table can be exported to Excel or CSV and later read
by [[IAMLab RSM Evaluate]].
'@; notes = @'
- `design`: DSD screens many settings with few prints; BBD is the usual
  choice when the effect of each setting should be modelled with
  curvature; Taguchi, FullFact2, and FracFact2 are further standard
  plans.
- The Power group estimates whether the plan has enough runs to detect
  an effect of a given size, using the scatter of a previous campaign
  (`powerSigma`). A warning is shown when the plan is too small.
- The Advanced group sets centre points, the number of levels for
  Taguchi plans, and a random run order.
'@; image = "DoE Design with a panel on the table output showing a BBD plan for three settings." }
$P["IAMLab RSM Evaluate"] = @{ intro = @'
IAMLab RSM Evaluate fits a formula to the measurements of a test
series. For each measured result, the formula describes how the result
depends on the settings. The component reports how strongly each
setting matters, how well the formula fits the data, and diagnostics
that show whether single runs or overlapping settings distort the fit.
The fitted models are passed on to [[IAMLab Desirability]]. The
component has a compute button.
'@; notes = @'
- `tablePath` accepts the Excel or CSV file exported by DoE Design with
  the measurement columns filled in. CSV files may use English (`1.5`)
  or German (`1,5`) numbers; a file that mixes both is refused.
- `model`: Linear fits straight effects, 2FI adds interactions between
  settings, Quadratic adds curvature.
- A weak fit (adjusted R² below 0.5) is flagged. A recommendation from
  a weak formula is only as reliable as that formula.
- The Direct group accepts the data as Grasshopper trees instead of a
  file.
'@; image = "RSM Evaluate with the on-canvas effect bars and a panel on the report output." }
$P["IAMLab Desirability"] = @{ intro = @'
IAMLab Desirability takes the fitted models and a goal for each
measured result, and finds the combination of settings that meets all
goals best. The result is a recommended set of settings, a score `D`
between 0 (no goal met) and 1 (all goals met), and a report. The
component has a compute button.
'@; notes = @'
- `goals` uses a short text per result: `max`, `min`, `target=7 in
  6..8` (aim for 7, accept 6 to 8), `range=6..8`, or `none`. Add `w=2`
  to weight a goal or `s=2` to change the shape of the ramp.
- `alternatives` lists other good combinations for the same overall
  goal. For the best trade-offs between the individual goals, enable
  the Pareto group and read the `pareto` output.
- The Sweep group produces one recipe per target value, for example one
  set of settings for each desired bead width.
- `interval` gives the uncertainty of the predicted results at the
  recommended settings.
- After the first solve, a small draggable curve appears below the
  desirability plot; the goal ramps can be adjusted there.
'@; image = "Desirability with the desirability profile plot and the goal-curve strip visible below it." }
$P["IAMLab Test Geometry"] = @{ intro = @'
IAMLab Test Geometry creates simple sample shapes for calibration
prints: cylinder, vase, twisted vase, frustum, wall, and box. The shape
is chosen from the dropdown on the component. Using the same shape for
every run of a test series makes the measurements comparable.
'@; notes = @'
- The Profile group shapes the vase (waves and twist), the polygon
  cross-section, and the frustum ratio. The Wall/box group sets the
  length and thickness of wall and box.
'@; image = "Test Geometry with the dropdown open and a twisted vase in the viewport." }
$P["IAMLab Calib Points"] = @{ intro = @'
IAMLab Calib Points generates a grid of marking positions for the SCARA
arm of the lab and drives the machine to each of them, one by one, so
that a mark can be made on the bed. Measuring where the marks actually
landed, and comparing with where they were commanded, is the input for
[[IAMLab Scara Fit]]. Every move is checked in the same way as a jog
move.
'@; notes = @'
- The marks are laid out on several radii and angles around the arm.
  At least three radii are recommended for the fit.
- `points` is connected to the `commanded` input of Scara Fit; the
  `sheet` output is a printable measurement protocol.
- Buttons on the component step through the marks and move the machine.
'@; image = "Calib Points with the mark grid drawn on the bed in the viewport." }
$P["IAMLab Machine Console"] = @{ intro = @'
IAMLab Machine Console is the one window for controlling a printer from
Grasshopper - a printer with a Duet board, or a Klipper printer through
Moonraker. It handles connecting, homing, jogging, capturing the bed
origin and the nozzle height, checking and uploading a job, starting,
pausing and cancelling it, sending single commands, driving to
calibration points, watching heaters and fans, and keeping the job
logs. It replaces the older Duet Connect, Status, Jog, Terminal and
Print components, which still open in saved files but are no longer on
the ribbon. Which machine it talks to follows the connected `robot`
profile: a Duet profile speaks the Duet's own protocol, and a
`generic-klipper` family profile speaks to Moonraker's HTTP API
instead.
'@; notes = @'
- `host` is `hostname[:port]` for either kind of machine. `password` is
  the Duet password on a Duet profile, and the Moonraker API key on a
  Klipper profile.
- The window has a Thermals page (shown for a thermoplastic profile, or
  once the machine reports heaters): every heater and fan, presets
  (PLA, PETG, ABS, Off), a temperature chart, manual extrude/retract,
  and speed/flow/pressure-advance tuning. Manual extrude and retract
  only work on Klipper - RRF does not report whether the hotend is warm
  enough to extrude.
- `dryRun` (a firmware simulation before the real print) only exists on
  a Duet; on a Klipper profile it is ignored with a remark.
- Klipper accepts only Cartesian kinematics. A delta, polar, or
  unrecognised kinematics refuses upload and start under every profile.
- Outputs 20-26 report the hotend, hotend target, bed, bed target, fan,
  speed factor, and flow factor as read from the machine; `-1` means
  the machine did not report that value.
- Without a Robot Definition, the console assumes the SCARA arm of the
  lab. With a Cartesian Duet or Klipper definition, it controls that
  printer instead and checks moves against its own travel limits.
- Right-click the component for "Trust connected machine", a raw
  object-model dump, and the console's renderer and motion-reduction
  settings.
'@; image = "The Machine Console dashboard with the Thermals tab open, heaters and fans visible." }
$P["IAMLab Duet Connect"] = @{ intro = @'
IAMLab Duet Connect opens the connection to a printer with a Duet
board over WiFi. The `host` output identifies the connection and is
connected to every other Duet component. Connect and Disconnect are
buttons on the component; reopening a saved file never connects on its
own.
'@; notes = @'
- The password is stored in plain text in the `.gh` file and sent in
  plain text over the local network. Clear the password before sharing
  a file.
- On the first connection, the component remembers which board
  answered. If a different board answers later at the same address,
  actions are blocked until the new board is accepted with "Trust
  connected machine" in the context menu of the component.
'@; image = "Duet Connect with the host and password inputs and a panel on the status output." }
$P["IAMLab Machine Files"] = @{ intro = @'
IAMLab Machine Files browses the file storage of the connected machine,
Duet or Klipper alike: the Duet's SD card, or the folders Moonraker
serves on a Klipper printer. Buttons on the component read a folder,
step through the entries, open a folder, download a file, rename or
move a file, and delete a file.
'@; notes = @'
- `host` accepts either a Duet or a Moonraker host string from
  [[IAMLab Machine Console]]; the component works the same way on both.
- `dir` is the folder on the machine, for example `0:/gcodes` on a Duet
  or `gcodes` on Moonraker.
- `savePath` is the local file or folder for a download.
'@; image = "Machine Files with the directory listing in a panel." }
$P["IAMLab Duet Jog"] = @{ intro = @'
IAMLab Duet Jog moves the machine in fixed steps from buttons on the
component, homes the axes, captures the bed origin and the nozzle
height, and provides an emergency stop. Every target is checked
against the limits of the machine before the command is sent.
'@; notes = @'
- The step sizes and the maximum speed are fixed so that a wrong click
  cannot produce a large or fast move.
- Without a Robot Definition, the component assumes the SCARA arm of
  the lab. With a `generic-duet-cartesian` definition, moves are checked
  against the travel limits stored in the firmware, and the Limits
  group is only needed to override them.
- `bedOrigin` and `zZero` are captured positions that can be connected
  to other components, for example as the bed centre.
'@; image = "Duet Jog with the jog buttons visible and the live position shown in a panel." }
$P["IAMLab Live View"] = @{ intro = @'
IAMLab Live View shows the machine while it prints: the bed plate, the
arm at the real nozzle position, and the printed bead growing as the
job progresses. It works the same way on a Duet board or on a Klipper
printer through Moonraker - connect either kind of host. The component
only reads from the machine and never sends commands. Optionally, the
nozzle path is recorded with timestamps to a CSV file.
'@; notes = @'
- `host` accepts a Duet or a Moonraker host string from
  [[IAMLab Machine Console]].
- Connect the job G-code to `gcode` so that the bead preview follows
  the print.
- The picture is drawn straight into the Rhino viewport and animated
  smoothly (up to 60 fps) without re-solving the canvas, so the
  definition can keep being edited while the print runs.
- The Log group records the nozzle path with timestamps, layer changes,
  and speed. `logFolder` writes the file while the print runs;
  `logExport` writes the recorded data to a new file.
- With a `generic-duet-cartesian` or Klipper Cartesian definition, the
  plate, the position, and the bead are shown without an arm.
'@; image = "Live View during a print: posed arm, bed plate, and the bead printed so far." }
$P["IAMLab Duet Print"] = @{ intro = @'
IAMLab Duet Print checks a G-code job against the limits of the
machine, uploads the job to the Duet board with a checksum, and starts,
pauses, resumes, or cancels the job from buttons on the component. A
job that violates the limits is not uploaded, and a job whose upload is
out of date is not started.
'@; notes = @'
- `dryRun` starts a firmware simulation of the job instead of the real
  print, which gives a time estimate without moving the machine.
- `danger` is true when the job was refused. `problems` and `messages`
  show where and why.
- `blockWarnings` refuses the upload on any warning, not only on hard
  limit violations.
- Without a Robot Definition, the component checks the job for the
  SCARA arm of the lab. With a `generic-duet-cartesian` definition, the
  job is checked against the travel limits stored in the firmware.
'@; image = "Duet Print with the Upload and Start buttons and a panel on the messages output." }
$P["IAMLab Duet Status"] = @{ intro = @'
IAMLab Duet Status shows the current state of the connected machine:
the nozzle position, whether the axes are homed, the file being
printed, the progress, and the last reply from the firmware. The
component refreshes on every poll of the connection.
'@; notes = @'
- `pos` is reported in Rhino coordinates. For the SCARA arm of the lab,
  the machine coordinates are mirrored; the description of the output
  gives an example.
'@; image = "Duet Status with panels on status, pos, and progress." }
$P["IAMLab Duet Terminal"] = @{ intro = @'
IAMLab Duet Terminal sends one raw G-code line to the board and shows
the reply. The line is sent without any of the checks that the other
components apply, so the component is meant for experts. Commands that
move the machine, change the configuration, or start a job are only
sent when `expert` is true and the Send button is double-clicked.
'@; notes = @'
- A safe first command is `M115`, which reports the firmware version.
'@; image = "Duet Terminal with M115 sent and the firmware reply in a panel." }
$P["IAMLab Scara Fit"] = @{ intro = @'
IAMLab Scara Fit calculates the real arm lengths of the SCARA from the
difference between the commanded and the measured mark positions of a
calibration run. The output includes corrected values for the firmware
configuration and for the robot description used by the plugin, plus a
report on how well the fit matches the marks. The component has a
compute button.
'@; notes = @'
- `commanded` comes from the `points` output of [[IAMLab Calib Points]];
  `measured` are the positions measured on the bed, in the same order.
- The Fit group chooses which values are fitted. `fitJ1` is normally
  off because a J1 offset cannot be told apart from a rotation of the
  measurement grid.
- Copy `m669` into `config.g` on the board and `robotXml` into the
  robot description of the plugin.
'@; image = "Scara Fit with the report output in a panel and the residual values listed." }

function Slug([string]$n) { ($n -replace ' ', '-') }
function TypeName($i) {
  $t = $i.type; $acc = $i.access
  $s = switch ($t) { "Time" { "Date/time" } default { $t } }
  if ($acc -eq "list") { $s += " (list)" } elseif ($acc -eq "tree") { $s += " (tree)" }
  $s
}
$Clean = [ordered]@{
  "Enforce the Cartesian M208 envelope" = "Check moves against the travel limits stored in the firmware (M208)";
  "validates the job against the M208 envelope only - unwired limit inputs then defer to the firmware's own M208 report" = "checks the job against the travel limits stored in the firmware only; limit inputs that are not connected are read from the firmware";
  "gates on the M208 envelope only - unwired limit inputs then defer to the firmware's own M208 report" = "checks moves against the travel limits stored in the firmware only; limit inputs that are not connected are read from the firmware";
  "M208 envelope" = "travel limits stored in the firmware (M208)";
  "Envelope X minimum, mm (firmware M208 X minimum)" = "Lowest allowed X, mm (firmware M208 X minimum)";
  "Envelope X maximum, mm (firmware M208 X maximum)" = "Highest allowed X, mm (firmware M208 X maximum)";
  "Envelope Y minimum, mm (firmware M208 Y minimum)" = "Lowest allowed Y, mm (firmware M208 Y minimum)";
  "Envelope Y maximum, mm (firmware M208 Y maximum)" = "Highest allowed Y, mm (firmware M208 Y maximum)";
  "Also collect the non-dominated trade-off front over the individual response desirabilities. Off by default - it evaluates every goal even where the scalarised D is 0" = "Also collect the best trade-offs between the individual goals. Off by default, because every goal is then evaluated even where the combined score D is 0";
  "Pareto front (non-dominated trade-off)" = "Pareto front (best trade-offs between goals)";
  "Non-dominated recipes (enable the pareto group)" = "Best trade-off recipes (enable the pareto group)";
  "Distinct local optima of the scalarised D (NOT a Pareto set - see the pareto output)" = "Other good recipes for the combined score D (not the best trade-offs between goals; see the pareto output)";
  "Front thinning cap (crowding distance; per-objective best always survives)" = "Maximum number of recipes kept in the front (the best recipe for each goal is always kept)";
  "With a kinematic profile the estimate also honours the robot's joint-speed limits, not Cartesian feed alone" = "With an arm profile, the estimate also respects the joint speeds of the arm, not only the nozzle speed";
  "Most-constrained available model: robot joint-speed limited when a kinematic Robot Definition is wired, else the trapezoidal accel estimate when accel > 0, else the kinematic lower bound" = "The most detailed model available: limited by the joint speeds of the arm when a Robot Definition for an arm is connected, otherwise the acceleration model when accel > 0, otherwise a simple lower bound";
  "Kinematics overrides (L1/L2, reach, elbow)" = "Arm geometry overrides (L1/L2, reach, elbow)";
  "Kinematic family" = "Machine type";
  "empty without robot kinematics" = "empty without an arm profile";
  "when a Robot Definition supplies kinematics" = "when a Robot Definition for an arm is connected";
  "UNCHECKED - commands bypass the kinematic validation" = "The line is sent without the safety checks of the other components";
  "Reach-annulus overrides (normally the robot's job)" = "Reach ring overrides (normally taken from the robot profile)";
  "Optional reach-annulus inner override, mm (0 = use the robot's own reach)" = "Optional inner radius of the reachable ring, mm (0 = use the reach of the robot profile)";
  "Optional reach-annulus outer override, mm (0 = use the robot's own reach)" = "Optional outer radius of the reachable ring, mm (0 = use the reach of the robot profile)";
  "leverage, studentized residuals, Cook's distance, VIF" = "leverage and scaled residuals per run, Cook's distance, and VIF per term";
  "unless the machine's Z zero is genuinely offset from the bed" = "unless the Z zero of the machine is offset from the bed";
  "fill responses → link to RSM component" = "fill in the responses, then load the file in the RSM component";
  "Smallest low→high response change worth detecting" = "Smallest change of the result between the low and the high level that is worth detecting";
  "default unit ↦ unit" = "default 0..1 to 0..1";
  "→" = "to"; "↦" = "to";
}
# case-sensitive pairs that would collide as hashtable keys
$CleanPairs = @(
  @("Unwired", "Not connected"), @("unwired", "not connected"),
  @("Wire a Button", "Connect a Button"), @("wire it into", "connect it to"), @("Wire it", "Connect it"),
  @("wire into", "connect to"), @("wire from", "connect from"), @("wired", "connected"), @("not just", "not only"), @("True when all targets pass the selected robot's reach checks; true when no kinematics are selected", "True when every target passes the reach checks of the selected robot; true when no arm profile is selected")
)
function Esc([string]$s) {
  foreach ($k in $Clean.Keys) { $s = $s.Replace($k, $Clean[$k]) }
  foreach ($p in $CleanPairs) { $s = $s.Replace($p[0], $p[1]) }
  (($s -replace '\|', '\|') -replace "`r?`n", " ")
}
function DefText([string]$d) {
  if (-not $d) { return "" }
  if ($d -match '^[A-Za-z]\w+$' -and $d -notin @("true","false","print","none","BBD","Linear","Quadratic","printlog","iamlab_print","M115")) { return "" }
  ($d -replace '\\n', '; ')
}
function AppendTable($sb, $rows) {
  $hasDef = ($rows | Where-Object { (DefText $_.default) -ne "" }).Count -gt 0
  if ($hasDef) { [void]$sb.AppendLine("| Name | Type | Default | Description |"); [void]$sb.AppendLine("|---|---|---|---|") }
  else { [void]$sb.AppendLine("| Name | Type | Description |"); [void]$sb.AppendLine("|---|---|---|") }
  foreach ($i in $rows) {
    if ($hasDef) { [void]$sb.AppendLine("| ``$($i.name)`` | $(TypeName $i) | $(Esc (DefText $i.default)) | $(Esc $i.desc) |") }
    else { [void]$sb.AppendLine("| ``$($i.name)`` | $(TypeName $i) | $(Esc $i.desc) |") }
  }
  [void]$sb.AppendLine()
}

$index = [ordered]@{}; $imgRows = @()
foreach ($c in ($comps | Where-Object { -not $_.hidden } | Sort-Object panel, name)) {
  $pr = $P[$c.name]; if (-not $pr) { "MISSING PROSE: $($c.name)"; continue }
  $slug = Slug $c.name
  $img = "comp-" + (($slug.ToLower() -replace '^iamlab-','') -replace '[^a-z0-9-]','') + ".png"
  $sb = New-Object Text.StringBuilder
  [void]$sb.AppendLine("# $($c.name)"); [void]$sb.AppendLine()
  [void]$sb.AppendLine($pr.intro.Trim()); [void]$sb.AppendLine()
  $hv = if ($c.heavy) { "yes (runs only when the button is clicked)" } else { "no (runs on every change)" }
  [void]$sb.AppendLine("Panel: ``$($c.panel)``. Nickname: ``$($c.nick)``. Compute button: $hv."); [void]$sb.AppendLine()
  [void]$sb.AppendLine("<!-- IMAGE TODO: $($pr.image) A short screen recording saved as GIF can replace the image. -->")
  [void]$sb.AppendLine("![$($c.name) on the canvas](images/$img)"); [void]$sb.AppendLine()
  $imgRows += "| ``$img`` | $($c.name) | $($pr.image) |"
  [void]$sb.AppendLine("## Inputs"); [void]$sb.AppendLine()
  $core = @($c.inputs | Where-Object { $_.group -eq "core" })
  $usedGroups = @($c.inputs | Where-Object { $_.group -ne "core" } | Select-Object -ExpandProperty group -Unique)
  if ($core.Count) { AppendTable $sb $core }
  foreach ($gid in $usedGroups) {
    $gl = ($c.groups | Where-Object { $_.id -eq $gid } | Select-Object -First 1).label; if (-not $gl) { $gl = $gid }; $gl = Esc $gl
    [void]$sb.AppendLine("### Optional group: $gl"); [void]$sb.AppendLine()
    [void]$sb.AppendLine("Right-click the component to switch this group on. The inputs appear only while the group is enabled."); [void]$sb.AppendLine()
    AppendTable $sb @($c.inputs | Where-Object { $_.group -eq $gid })
  }
  [void]$sb.AppendLine("## Outputs"); [void]$sb.AppendLine()
  [void]$sb.AppendLine("| Name | Type | Description |"); [void]$sb.AppendLine("|---|---|---|")
  foreach ($o in $c.outputs) { [void]$sb.AppendLine("| ``$($o.name)`` | $(TypeName $o) | $(Esc $o.desc) |") }
  [void]$sb.AppendLine()
  if ($pr.notes) { [void]$sb.AppendLine("## Notes"); [void]$sb.AppendLine(); [void]$sb.AppendLine($pr.notes.Trim()); [void]$sb.AppendLine() }
  [void]$sb.AppendLine("Back to [[Components]].")
  [IO.File]::WriteAllText("$w\$slug.md", $sb.ToString(), $enc)
  if (-not $index.Contains($c.panel)) { $index[$c.panel] = @() }
  $first = ($pr.intro.Trim() -replace "`r?`n", " " -split '(?<=\.)\s+')[0]
  $index[$c.panel] += "| [[$($c.name)]] | $first$(if ($c.heavy) { ' Has a compute button.' }) |"
}

# IAMLab Pareto Select has no $P entry on purpose: its wiki page carries a
# hand-written walkthrough and a "what happens when the front changes"
# section that go beyond what intro/notes/image can express, so it is
# maintained by hand rather than regenerated. Add it back into the index
# (sorted by name) so it still appears in Components.md and the sidebar.
if ($index.Contains("6 Calibrate")) {
  $paretoRow = "| [[IAMLab Pareto Select]] | IAMLab Pareto Select shows the trade-off front from Desirability and lets you pick one recipe from it by clicking; the choice stays with the document and is flagged when the front changes. |"
  $index["6 Calibrate"] = @($index["6 Calibrate"] + $paretoRow) | Sort-Object { [regex]::Match($_, '\[\[([^\]]+)\]\]').Groups[1].Value }
}

$ci = New-Object Text.StringBuilder
[void]$ci.AppendLine("# Components"); [void]$ci.AppendLine()
[void]$ci.AppendLine("Every component on the IAMLab tab has a page of its own with a short explanation, a table of inputs and outputs, and notes on the inputs that need more than one line. The pages are grouped by panel below. Components with a compute button run only when the button is clicked; all others run on every change."); [void]$ci.AppendLine()
[void]$ci.AppendLine("<!-- IMAGE TODO: ribbon screenshot with all panels expanded, or a collage of the seven panels. -->")
[void]$ci.AppendLine("![The IAMLab ribbon](images/components-ribbon.png)"); [void]$ci.AppendLine()
foreach ($panel in $index.Keys) {
  [void]$ci.AppendLine("## $panel"); [void]$ci.AppendLine()
  [void]$ci.AppendLine("| Component | Purpose |"); [void]$ci.AppendLine("|---|---|")
  foreach ($r in $index[$panel]) { [void]$ci.AppendLine($r) }
  [void]$ci.AppendLine()
}
[void]$ci.AppendLine("## Older components"); [void]$ci.AppendLine()
[void]$ci.AppendLine("IAMLab Robot from versions before 2.0 is hidden from the ribbon but still opens and works in saved files. New files should use [[IAMLab Robot Definition]] and [[IAMLab Robot Simulation]]."); [void]$ci.AppendLine()
[void]$ci.AppendLine("Since 3.0.0 the same applies to [[IAMLab Duet Connect]], [[IAMLab Duet Status]], [[IAMLab Duet Jog]], [[IAMLab Duet Terminal]] and [[IAMLab Duet Print]]: they still open and work in saved files, with the same inputs, outputs and safety checks, but they are no longer on the ribbon. New files should use [[IAMLab Machine Console]], which does everything they did in one window and works on Klipper machines as well as Duet ones. Their pages stay as a reference for old definitions.")
[IO.File]::WriteAllText("$w\Components.md", $ci.ToString(), $enc)

$sbar = New-Object Text.StringBuilder
[void]$sbar.AppendLine("**[[Home]]**"); [void]$sbar.AppendLine()
[void]$sbar.AppendLine("**Getting started**"); [void]$sbar.AppendLine("- [[Installation]]"); [void]$sbar.AppendLine("- [[Quick Start]]"); [void]$sbar.AppendLine()
[void]$sbar.AppendLine("**Guides**"); [void]$sbar.AppendLine("- [[Robots and Machine Control]]"); [void]$sbar.AppendLine("- [[Non Planar Slicing]]"); [void]$sbar.AppendLine("- [[Calibration and DoE]]"); [void]$sbar.AppendLine()
[void]$sbar.AppendLine("**[[Components]]**")
foreach ($panel in $index.Keys) {
  [void]$sbar.AppendLine("- $panel")
  foreach ($r in $index[$panel]) { $n = [regex]::Match($r, '\[\[([^\]]+)\]\]').Groups[1].Value; [void]$sbar.AppendLine("  - [[$n]]") }
}
[void]$sbar.AppendLine("- Older (hidden, still open in saved files)")
foreach ($n in @("IAMLab Duet Connect", "IAMLab Duet Status", "IAMLab Duet Jog", "IAMLab Duet Terminal", "IAMLab Duet Print")) { [void]$sbar.AppendLine("  - [[$n]]") }
[void]$sbar.AppendLine(); [void]$sbar.AppendLine("**Help**"); [void]$sbar.AppendLine("- [[Troubleshooting]]"); [void]$sbar.AppendLine("- [[FAQ]]"); [void]$sbar.AppendLine("- [[Release Notes]]")
[IO.File]::WriteAllText("$w\_Sidebar.md", $sbar.ToString(), $enc)

$it = [IO.File]::ReadAllText("$g\IMAGES-TODO.md")
$it = [regex]::Replace($it, "(?s)\r?\n## Component pages.*$", "")
$it = $it.TrimEnd() + "`n`n## Component pages`n`nOne image per component page. A short screen recording saved as a GIF works as well; use the same file name with ``.gif`` and change the reference in the page.`n`n| File | Page | Capture |`n|---|---|---|`n" + ($imgRows -join "`n") + "`n"
[IO.File]::WriteAllText("$g\IMAGES-TODO.md", $it, $enc)
"pages written: " + (Get-ChildItem "$w\IAMLab-*.md").Count
"index panels: " + ($index.Keys -join ", ")

