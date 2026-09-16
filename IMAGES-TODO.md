# Wiki screenshot checklist

This list contains every image referenced by the wiki. Capture each
image, name the file exactly as listed, and place the file in the
`images/` folder of the wiki repository. No text edits are needed.
Canvas and viewport captures should be about 1600 px wide, in PNG
format.

| # | File | Page | Capture |
|---|---|---|---|
| 1 | `home-hero.png` | Home | The full pipeline wired (Test Geometry, Slice, Place, G-code Generator, Robot Simulation) and the Rhino viewport with the posed SCARA and a printed bead. |
| 2 | `install-ribbon.png` | Installation | The Grasshopper ribbon with the IAMLab tab open and all seven panels visible, cropped to the ribbon. |
| 3 | `quickstart-pipeline.png` | Quick Start | The five-component setup with wires and `info` panels. |
| 4 | `quickstart-compute.png` | Quick Start | Close-up of the compute button of a Heavy component. |
| 5 | `quickstart-preview.png` | Quick Start | Viewport with the `preview` bead path and the `travels` moves, seam column visible. |
| 6 | `components-ribbon.png` | Components | The ribbon with all panels expanded, or a collage of the seven panels. |
| 7 | `robots-definition.png` | Robots and Machine Control | The Robot Definition profile dropdown open, wired to Robot Simulation, with the posed SCARA in the viewport. |
| 8 | `robots-reach-error.png` | Robots and Machine Control | A deliberately out-of-reach placement: red error balloon and flagged problem points. |
| 9 | `robots-duet-live.png` | Robots and Machine Control | The Machine Console window beside the Rhino viewport with Live View: bed plate, posed arm, and the trail. |
| 10 | `nonplanar-conical.png` | Non Planar Slicing | The same dome sliced planar and conical, side by side. |
| 12 | `nonplanar-overhang.png` | Non Planar Slicing | Overhang analysis on a twisted vase with green, orange, and red segments. |
| 13 | `doe-loop.png` | Calibration and DoE | The four calibration components wired in sequence, on-canvas plots visible. |
| 14 | `doe-coupons.png` | Calibration and DoE | A photograph of printed calibration coupons from a real campaign. |
| 15 | `doe-desirability.png` | Calibration and DoE | The desirability profile plot with the goal-curve strip below. |

A missing image is rendered as its alt text, so a partial set can be
published.

## Component pages

One image per component page. A short screen recording saved as a GIF works as well; use the same file name with `.gif` and change the reference in the page.

| File | Page | Capture |
|---|---|---|
| `comp-graph-mapper.png` | IAMLab Graph Mapper | The Graph Mapper on the canvas with a custom polyline curve and a point being dragged. |
| `comp-load-settings.png` | IAMLab Load Settings | Load Settings pointed at a folder, with the report output listing matched and unmatched files. |
| `comp-slice.png` | IAMLab Slice | Slice with a cylinder from Test Geometry connected; the viewport shows the contour tree, one colour per layer. |
| `comp-version.png` | IAMLab Version | The Version component with a panel on the summary output. |
| `comp-place.png` | IAMLab Place | Place with seamMode = 1; the viewport shows the placed part with the seam points lined up in a column. |
| `comp-print-bed.png` | IAMLab Print Bed | Print Bed connected to Place and Robot Simulation; the viewport shows the plate outline and the print centre. |
| `comp-g-code-generator.png` | IAMLab G-code Generator | G-code Generator with the core inputs visible and the preview (blue) and travels (ochre) drawn in the viewport. |
| `comp-g-code-restart.png` | IAMLab G-code Restart | G-code Restart with the printed (grey) and remaining (blue) curves shown on a half-finished part. |
| `comp-save-g-code.png` | IAMLab Save G-code | Save G-code with a Button connected to write and a panel showing the written path. |
| `comp-robot-definition.png` | IAMLab Robot Definition | Robot Definition with the profile dropdown open, connected to the G-code Generator and Robot Simulation. |
| `comp-robot-simulation.png` | IAMLab Robot Simulation | Robot Simulation at sim = 0.6 with the posed SCARA, the bead printed so far, and the bed outline. |
| `comp-interlayer-time.png` | IAMLab Interlayer Time | Interlayer Time on a clay print, layer slider partway up, alternating layer shading visible. |
| `comp-legend.png` | IAMLab Legend | Legend showing the Interlayer Time wire as a floating billboard next to the printed part. |
| `comp-overhang.png` | IAMLab Overhang | A twisted vase after Overhang with green, orange, and red segments in the viewport. |
| `comp-print-stats.png` | IAMLab Print Stats | Print Stats with a panel on the report output. |
| `comp-desirability.png` | IAMLab Desirability | Desirability with the desirability profile plot and the goal-curve strip visible below it. |
| `comp-doe-design.png` | IAMLab DoE Design | DoE Design with a panel on the table output showing a BBD plan for three settings. |
| `comp-rsm-evaluate.png` | IAMLab RSM Evaluate | RSM Evaluate with the on-canvas effect bars and a panel on the report output. |
| `comp-test-geometry.png` | IAMLab Test Geometry | Test Geometry with the dropdown open and a twisted vase in the viewport. |
| `comp-calib-points.png` | IAMLab Calib Points | Calib Points with the mark grid drawn on the bed in the viewport. |
| `comp-live-view.png` | IAMLab Live View | Live View during a print: posed arm, bed plate, and the bead printed so far. |
| `comp-machine-console.png` | IAMLab Machine Console | The Machine Console dashboard with the Dashboard page open: connection state, job progress and the action buttons visible (the Thermals tab of 4.0.0 no longer exists). |
| `comp-machine-files.png` | IAMLab Machine Files | Machine Files with the directory listing in a panel. |
| `comp-scara-fit.png` | IAMLab Scara Fit | Scara Fit with the report output in a panel and the residual values listed. |
