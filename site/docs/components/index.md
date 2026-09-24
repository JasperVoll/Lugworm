---
title: "Components"
nav_order: 3
description: "Reference for all Lugworm components, grouped by Grasshopper panel."
permalink: /docs/components/
has_children: true
has_toc: false
---

# Components

Every component on the Lugworm tab has a page of its own with a short explanation and a table of inputs and outputs. The pages are grouped by panel below. Components with a compute button run only when the button is clicked; all others run on every change. For how the components connect to each other, see the [Component Map]({{ '/docs/getting-started/component-map/' | relative_url }}).

{% include figure.html src="components/iamlab-ribbon" %}

## 1 Slice

| Component | Purpose |
|---|---|
| [Graph Mapper]({{ '/docs/components/graph-mapper/' | relative_url }}) | Graph Mapper changes a list of numbers by passing them through a curve that you draw on the component. |
| [Load Settings]({{ '/docs/components/load-settings/' | relative_url }}) | Load Settings points the document at a folder of saved settings files, one per component. |
| [Slice]({{ '/docs/components/slice/' | relative_url }}) | Slice cuts a shape into layers. Has a compute button. |

## 2 Place

| Component | Purpose |
|---|---|
| [Place]({{ '/docs/components/place/' | relative_url }}) | Place moves the sliced layers to the print position on the bed and decides where each layer starts and ends (the seam). |
| [Print Bed]({{ '/docs/components/print-bed/' | relative_url }}) | Print Bed describes the print bed once: how large the plate is, where the plate is in the Rhino document, and where a part is placed by default. |

## 3 G-code

| Component | Purpose |
|---|---|
| [G-code Generator]({{ '/docs/components/g-code-generator/' | relative_url }}) | G-code Generator turns the placed layers into the G-code file that the printer runs. Has a compute button. |
| [G-code Restart]({{ '/docs/components/g-code-restart/' | relative_url }}) | G-code Restart rebuilds a G-code file so that a print can continue after a failure. Has a compute button. |
| [Save G-code]({{ '/docs/components/save-g-code/' | relative_url }}) | Save G-code writes the G-code to a file whose name contains the date and time, so that no file is overwritten by accident. |

## 4 Robot

| Component | Purpose |
|---|---|
| [Robot Definition]({{ '/docs/components/robot-definition/' | relative_url }}) | Robot Definition selects the machine. |
| [Robot Simulation]({{ '/docs/components/robot-simulation/' | relative_url }}) | Robot Simulation shows a print before it runs. |

## 5 Analyze

| Component | Purpose |
|---|---|
| [Interlayer Time]({{ '/docs/components/interlayer-time/' | relative_url }}) | Interlayer Time shows, for every point of the print, how long ago the material directly underneath it was laid down. |
| [Legend]({{ '/docs/components/legend/' | relative_url }}) | Legend draws a legend for any data range: a continuous ramp, thresholded classes, named groups, or a size scale. |
| [Overhang]({{ '/docs/components/overhang/' | relative_url }}) | Overhang checks how far each part of a layer hangs out over the layer below. Has a compute button. |
| [Print Stats]({{ '/docs/components/print-stats/' | relative_url }}) | Print Stats reads a G-code file, or a toolpath, and reports the printed length, the travel length, the amount of material in cubic centimetres and grams, an estimated print time, and a table with one row per layer. |

## 6 Calibrate

| Component | Purpose |
|---|---|
| [Desirability]({{ '/docs/components/desirability/' | relative_url }}) | Desirability takes the fitted models and a goal for each measured result, and finds the combination of settings that meets all goals best. Has a compute button. |
| [DoE Design]({{ '/docs/components/doe-design/' | relative_url }}) | DoE Design plans a series of test prints. |
| [Pareto Select]({{ '/docs/components/pareto-select/' | relative_url }}) | Pareto Select shows the trade-off front from Desirability and lets you pick one recipe from it by clicking; the choice stays with the document and is flagged when the front changes. |
| [RSM Evaluate]({{ '/docs/components/rsm-evaluate/' | relative_url }}) | RSM Evaluate fits a formula to the measurements of a test series. Has a compute button. |
| [Test Geometry]({{ '/docs/components/test-geometry/' | relative_url }}) | Test Geometry creates the specimen for a calibration print: six simple shapes, six standard bending and compression specimens (DIN EN and ASTM), and a zig-zag extrusion test path. |

## 7 Print

| Component | Purpose |
|---|---|
| [Live View]({{ '/docs/components/live-view/' | relative_url }}) | Live View shows the machine while it prints: the bed plate, the arm at the real nozzle position, and the observed nozzle path drawn behind it. |
| [Duet Console]({{ '/docs/components/duet-console/' | relative_url }}) | Duet Console is the one window for controlling a printer with a Duet board from Grasshopper. |
| [Machine Files]({{ '/docs/components/machine-files/' | relative_url }}) | Machine Files browses the file storage of the connected machine - the Duet's SD card. |
