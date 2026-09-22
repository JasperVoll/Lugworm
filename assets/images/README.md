# Screenshots

Images and animations for the documentation site. Drop a file at the path
listed below and the page renders it automatically with the caption shown here;
no page edit is needed. Until the file exists, the page shows a pending note
naming it.

The paths below carry no extension. A slot accepts `.png`, `.gif`, `.mp4` or
`.webm`; when several exist the page picks the first in the order mp4, webm,
gif, png. Video files render as a muted, looping, autoplaying video.

Capture at 2x (retina) if possible, trimmed to the relevant area, with the
Grasshopper canvas at 100% zoom. Keep machine passwords, IP addresses and file
paths out of frame.

Keep every file small enough for a web page: stills as PNG, screen recordings
as MP4 (H.264) rather than GIF. A GIF of a full-window recording runs to tens
of megabytes; the same clip as MP4 is a few megabytes. GitHub rejects files
over 100 MB and warns above 50 MB, and the site check fails on any asset over
10 MB. Convert with ffmpeg:

```powershell
ffmpeg -i recording.gif -movflags faststart -pix_fmt yuv420p -vf "scale=trunc(iw/2)*2:trunc(ih/2)*2" -crf 28 recording.mp4
```

## Home (1)

| File | Caption | Page |
|---|---|---|
| `home/overview_home` | IAMLab Slicer in Rhino 8: the SCARA simulation in the viewport and the slicing-to-print pipeline on the Grasshopper canvas | `index.md` |

## Getting started (1)

| File | Caption | Page |
|---|---|---|
| `getting-started/iamlab-tab` | The IAMLab tab after installation | `docs/getting-started/installation.md` |

## Components (18)

Components not listed here have no screenshot slot on their page.

| File | Caption | Page |
|---|---|---|
| `components/iamlab-desirability` | IAMLab Desirability on the canvas | `docs/components/iamlab-desirability.md` |
| `components/iamlab-doe-design` | IAMLab DoE Design on the canvas | `docs/components/iamlab-doe-design.md` |
| `components/iamlab-g-code-generator` | IAMLab G-code Generator: input groups on the canvas and the generated G-code in a panel | `docs/components/iamlab-g-code-generator.md` |
| `components/iamlab-graph-mapper` | IAMLab Graph Mapper: switching the profile from sine to spline, with the wave infill following in the viewport | `docs/components/iamlab-graph-mapper.md` |
| `components/iamlab-interlayer-time` | IAMLab Interlayer Time: stepping through layers with the isolate toggle and the time legend in the viewport | `docs/components/iamlab-interlayer-time.md` |
| `components/iamlab-legend` | IAMLab Legend on the canvas | `docs/components/iamlab-legend.md` |
| `components/iamlab-live-view` | IAMLab Live View on the canvas | `docs/components/iamlab-live-view.md` |
| `components/iamlab-overhang` | IAMLab Overhang: layers classed as safe, warning and critical | `docs/components/iamlab-overhang.md` |
| `components/iamlab-pareto-select` | IAMLab Pareto Select on the canvas | `docs/components/iamlab-pareto-select.md` |
| `components/iamlab-place` | IAMLab Place: choosing a seam mode and turning the seam angle, with the seams drawn in the viewport | `docs/components/iamlab-place.md` |
| `components/iamlab-print-bed` | IAMLab Print Bed: the bed outline in the viewport and the component on the canvas | `docs/components/iamlab-print-bed.md` |
| `components/iamlab-print-stats` | IAMLab Print Stats on the canvas | `docs/components/iamlab-print-stats.md` |
| `components/iamlab-ribbon` | The IAMLab ribbon | `docs/components/index.md` |
| `components/iamlab-robot-simulation` | IAMLab Robot Simulation: moving the sim slider steps the SCARA arm through the print | `docs/components/iamlab-robot-simulation.md` |
| `components/iamlab-rsm-evaluate` | IAMLab RSM Evaluate on the canvas | `docs/components/iamlab-rsm-evaluate.md` |
| `components/iamlab-save-g-code` | IAMLab Save G-code on the canvas | `docs/components/iamlab-save-g-code.md` |
| `components/iamlab-slice` | IAMLab Slice with its standard inputs and the wave infill group | `docs/components/iamlab-slice.md` |
| `components/iamlab-test-geometry` | IAMLab Test Geometry with the specimen menu open | `docs/components/iamlab-test-geometry.md` |

## Workflows (9)

| File | Caption | Page |
|---|---|---|
| `workflows/calibration-loop` | The calibration loop on the canvas | `docs/workflows/calibration-and-doe.md` |
| `workflows/desirability-goals` | Desirability profile with the goal-curve editor | `docs/workflows/calibration-and-doe.md` |
| `workflows/doe-coupons` | Printed DoE coupons | `docs/workflows/calibration-and-doe.md` |
| `workflows/live-control` | Live control from the Grasshopper canvas | `docs/workflows/robots-and-machine-control.md` |
| `workflows/overhang-nonplanar` | Overhang analysis on a non-planar part | `docs/workflows/non-planar-slicing.md` |
| `workflows/pareto-pick` | Picking a recipe on the Pareto front | `docs/workflows/calibration-and-doe.md` |
| `workflows/planar-vs-conical` | Planar and conical slicing of the same part | `docs/workflows/non-planar-slicing.md` |
| `workflows/reach-violation` | An out-of-reach placement detected in Robot Simulation | `docs/workflows/robots-and-machine-control.md` |
| `workflows/robot-profile` | Selecting a robot profile | `docs/workflows/robots-and-machine-control.md` |

29 slots in total.
