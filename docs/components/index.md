---
title: "Components"
nav_order: 3
description: "Reference for all IAMLab components, grouped by Grasshopper panel."
permalink: /docs/components/
has_children: true
has_toc: false
---

# Components

Every component on the IAMLab tab has a page of its own with a short explanation, a table of inputs and outputs, and notes on the inputs that need more than one line. The pages are grouped by panel below. Components with a compute button run only when the button is clicked; all others run on every change.

{% include figure.html src="components/iamlab-ribbon.png" alt="The IAMLab ribbon" %}

## 1 Slice

| Component | Purpose |
|---|---|
| [IAMLab Graph Mapper]({{ '/docs/components/iamlab-graph-mapper/' | relative_url }}) | IAMLab Graph Mapper changes a list of numbers by passing them through a curve that you draw on the component. |
| [IAMLab Load Settings]({{ '/docs/components/iamlab-load-settings/' | relative_url }}) | IAMLab Load Settings points the document at a folder of saved settings files, one per component. |
| [IAMLab Slice]({{ '/docs/components/iamlab-slice/' | relative_url }}) | IAMLab Slice cuts a shape into layers. Has a compute button. |
| [IAMLab Version]({{ '/docs/components/iamlab-version/' | relative_url }}) | IAMLab Version shows which version of the plugin is installed, when the build was made, and which .NET runtime Rhino loaded. |

## 2 Place

| Component | Purpose |
|---|---|
| [IAMLab Place]({{ '/docs/components/iamlab-place/' | relative_url }}) | IAMLab Place moves the sliced layers to the print position on the bed and decides where each layer starts and ends (the seam). |
| [IAMLab Print Bed]({{ '/docs/components/iamlab-print-bed/' | relative_url }}) | IAMLab Print Bed describes the print bed once: how large the plate is, where the plate is in the Rhino document, and where a part is placed by default. |

## 3 G-code

| Component | Purpose |
|---|---|
| [IAMLab G-code Generator]({{ '/docs/components/iamlab-g-code-generator/' | relative_url }}) | IAMLab G-code Generator turns the placed layers into the G-code file that the printer runs. Has a compute button. |
| [IAMLab G-code Restart]({{ '/docs/components/iamlab-g-code-restart/' | relative_url }}) | IAMLab G-code Restart rebuilds a G-code file so that a print can continue after a failure. Has a compute button. |
| [IAMLab Save G-code]({{ '/docs/components/iamlab-save-g-code/' | relative_url }}) | IAMLab Save G-code writes the G-code to a file whose name contains the date and time, so that no file is overwritten by accident. |

## 4 Robot

| Component | Purpose |
|---|---|
| [IAMLab Robot Definition]({{ '/docs/components/iamlab-robot-definition/' | relative_url }}) | IAMLab Robot Definition selects the machine. |
| [IAMLab Robot Simulation]({{ '/docs/components/iamlab-robot-simulation/' | relative_url }}) | IAMLab Robot Simulation shows a print before it runs. |

## 5 Analyze

| Component | Purpose |
|---|---|
| [IAMLab Interlayer Time]({{ '/docs/components/iamlab-interlayer-time/' | relative_url }}) | IAMLab Interlayer Time shows, for every point of the print, how long ago the material directly underneath it was laid down. |
| [IAMLab Legend]({{ '/docs/components/iamlab-legend/' | relative_url }}) | IAMLab Legend draws a legend for any data range: a continuous ramp, thresholded classes, named groups, or a size scale. |
| [IAMLab Overhang]({{ '/docs/components/iamlab-overhang/' | relative_url }}) | IAMLab Overhang checks how far each part of a layer hangs out over the layer below. Has a compute button. |
| [IAMLab Print Stats]({{ '/docs/components/iamlab-print-stats/' | relative_url }}) | IAMLab Print Stats reads a G-code file, or a toolpath, and reports the printed length, the travel length, the amount of material in cubic centimetres and grams, an estimated print time, and a table with one row per layer. |

## 6 Calibrate

| Component | Purpose |
|---|---|
| [IAMLab Desirability]({{ '/docs/components/iamlab-desirability/' | relative_url }}) | IAMLab Desirability takes the fitted models and a goal for each measured result, and finds the combination of settings that meets all goals best. Has a compute button. |
| [IAMLab DoE Design]({{ '/docs/components/iamlab-doe-design/' | relative_url }}) | IAMLab DoE Design plans a series of test prints. |
| [IAMLab Pareto Select]({{ '/docs/components/iamlab-pareto-select/' | relative_url }}) | IAMLab Pareto Select shows the trade-off front from Desirability and lets you pick one recipe from it by clicking; the choice stays with the document and is flagged when the front changes. |
| [IAMLab RSM Evaluate]({{ '/docs/components/iamlab-rsm-evaluate/' | relative_url }}) | IAMLab RSM Evaluate fits a formula to the measurements of a test series. Has a compute button. |
| [IAMLab Test Geometry]({{ '/docs/components/iamlab-test-geometry/' | relative_url }}) | IAMLab Test Geometry creates simple sample shapes for calibration prints: cylinder, vase, twisted vase, frustum, wall, and box. |

## 7 Print

| Component | Purpose |
|---|---|
| [IAMLab Calib Points]({{ '/docs/components/iamlab-calib-points/' | relative_url }}) | IAMLab Calib Points generates a grid of marking positions for the SCARA arm of the lab and drives the machine to each of them, one by one, so that a mark can be made on the bed. |
| [IAMLab Live View]({{ '/docs/components/iamlab-live-view/' | relative_url }}) | IAMLab Live View shows the machine while it prints: the bed plate, the arm at the real nozzle position, and the printed bead growing as the job progresses. |
| [IAMLab Machine Console]({{ '/docs/components/iamlab-machine-console/' | relative_url }}) | IAMLab Machine Console is the one window for controlling a printer with a Duet board from Grasshopper. |
| [IAMLab Machine Files]({{ '/docs/components/iamlab-machine-files/' | relative_url }}) | IAMLab Machine Files browses the file storage of the connected machine - the Duet's SD card. |
| [IAMLab Scara Fit]({{ '/docs/components/iamlab-scara-fit/' | relative_url }}) | IAMLab Scara Fit calculates the real arm lengths of the SCARA from the difference between the commanded and the measured mark positions of a calibration run. Has a compute button. |
