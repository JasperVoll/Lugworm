---
title: "Component Map"
nav_order: 2
parent: "Getting started"
description: "Follow the main slicing route and find the tools for each stage."
permalink: /docs/getting-started/component-map/
---

# Component Map

Start with the three components in the main route, then add the tools you need. Each card opens the component reference and shows its **Grasshopper ribbon group**. The icons match the plugin.

[Main route](#main-route) · [Setup](#setup-and-inputs) · [Review](#review-and-save) · [Analysis](#analyse-the-print) · [Machine control](#operate-a-duet-machine) · [Calibration](#calibrate-a-material) · [Utilities](#machine-calibration-and-utilities)

## Main route

Read from top to bottom. The labels between cards name the output and input to connect in Grasshopper.

<div class="component-route" aria-label="Main slicing route">
<p class="component-wire">Your geometry or Test Geometry → Slice <code>geo</code></p>
{% include component-card.html slug="slice" title="Slice" icon="IamSlicer" panel="1 Slice" description="Cut geometry into a tree of layer contours." %}
<p class="component-wire"><span aria-hidden="true">↓</span> Slice <code>contours</code> → Place <code>contours</code></p>
{% include component-card.html slug="place" title="Place" icon="IamMover" panel="2 Place" description="Position the layers and set their seams." %}
<p class="component-wire"><span aria-hidden="true">↓</span> Place <code>moved</code> → Generator <code>crvs</code></p>
{% include component-card.html slug="g-code-generator" title="G-code Generator" icon="IamGcode" panel="3 G-code" description="Turn placed curves into machine instructions." %}
<p class="component-wire"><span aria-hidden="true">↓</span> Generator <code>gcode</code> → review, analysis, saving, or upload</p>
</div>

Keep the layer branches intact along this route: `{object; layer}` for multiple objects. Recompute Slice and Generator after changing their inputs, and read their `info` reports. For a guided example, follow [Make your first toolpath]({{ '/docs/getting-started/' | relative_url }}#make-your-first-toolpath).

## Setup and inputs

These tools feed the main route. Connect Robot Definition explicitly: an unwired Generator uses the laboratory SCARA frame. Choose `none` for a toolpath-only preview.

<div class="component-grid">
{% include component-card.html slug="test-geometry" title="Test Geometry" icon="IamTestGeo" panel="6 Calibrate" description="Create a specimen; send its geometry to Slice." %}
{% include component-card.html slug="print-bed" title="Print Bed" icon="IamPrintBed" panel="2 Place" description="Supply the bed definition to Place, Simulation, and Live View." %}
{% include component-card.html slug="robot-definition" title="Robot Definition" icon="IamRobotDefinition" panel="4 Robot" description="Share one machine profile across Generator, Simulation, and machine tools." %}
{% include component-card.html slug="graph-mapper" title="Graph Mapper" icon="IamGraphMapper" panel="1 Slice" description="Remap numbers, or connect map to Slice's profile for graph-wave infill." %}
</div>

## Review and save

Send Generator's `gcode` to Robot Simulation for review and to Save G-code when you are ready to write a file. These are parallel destinations; Simulation does not pass G-code on to Save.

<div class="component-grid">
{% include component-card.html slug="robot-simulation" title="Robot Simulation" icon="IamRobot" panel="4 Robot" description="Preview Generator's gcode and check the selected robot's motion." %}
{% include component-card.html slug="save-g-code" title="Save G-code" icon="IamGcodeSave" panel="3 G-code" description="Write Generator's gcode to a file after review." %}
{% include component-card.html slug="g-code-restart" title="G-code Restart" icon="IamGcodeRestart" panel="3 G-code" description="Prepare continuation G-code, then review and save the result." %}
</div>

## Analyse the print

Overhang reads the **contour tree**. Print Stats and Interlayer Time can read the **generated G-code**. Connect an analysis component's `legend` output to Legend's `legend` input to explain its colours.

<div class="component-grid">
{% include component-card.html slug="overhang" title="Overhang" icon="IamOverhang" panel="5 Analyze" description="Read contours from Slice or Place to inspect bead support." %}
{% include component-card.html slug="print-stats" title="Print Stats" icon="IamStats" panel="5 Analyze" description="Read gcode to estimate time, travel, and material use." %}
{% include component-card.html slug="interlayer-time" title="Interlayer Time" icon="IamInterlayer" panel="5 Analyze" description="Use gcode or a retained print log to analyse time between beads." %}
{% include component-card.html slug="legend" title="Legend" icon="IamLegend" panel="5 Analyze" description="Display the legend specification from an analysis component." %}
</div>

## Operate a Duet machine

Connect reviewed G-code to Machine Console for checking and upload. Live View and Machine Files work with the connected machine. Use the same machine profile throughout; see [Robots and Machine Control]({{ '/docs/workflows/robots-and-machine-control/' | relative_url }}) for setup and operating checks.

<div class="component-grid">
{% include component-card.html slug="machine-console" title="Machine Console" icon="IamDuetConnect" panel="7 Print" description="Check and upload gcode; connect, home, jog, and run a Duet job." %}
{% include component-card.html slug="live-view" title="Live View" icon="IamDuetLive" panel="7 Print" description="Monitor the connected machine and deposited bead." %}
{% include component-card.html slug="machine-files" title="Machine Files" icon="IamDuetFiles" panel="7 Print" description="Browse and manage files on the connected Duet machine." %}
</div>

## Calibrate a material

Follow these stages in order. Between DoE Design and RSM Evaluate, **print the planned specimens and enter your measurements in the exported table**. This is a test-and-measure workflow, not a direct wire between those two components.

<div class="component-grid">
{% include component-card.html slug="doe-design" title="DoE Design" icon="IamDoeDesign" panel="6 Calibrate" description="Export a run table with the settings for each test print." %}
{% include component-card.html slug="rsm-evaluate" title="RSM Evaluate" icon="IamRsm" panel="6 Calibrate" description="Read the completed measurement table and fit response models." %}
{% include component-card.html slug="desirability" title="Desirability" icon="IamDesirability" panel="6 Calibrate" description="Use the fitted models and your goals to recommend settings." %}
{% include component-card.html slug="pareto-select" title="Pareto Select" icon="IamParetoSelect" panel="6 Calibrate" description="Read Desirability's pareto output and keep a chosen recipe." %}
</div>

Use Test Geometry for a consistent specimen shape. Apply the selected settings to the relevant Slice and Generator inputs, then run a confirmation print. [Calibration and DoE]({{ '/docs/workflows/calibration-and-doe/' | relative_url }}) walks through the full process.

## Machine calibration and utilities

For SCARA calibration, send Calib Points to Machine Console, measure the positions, then use Scara Fit to estimate arm lengths for Robot Definition. Load Settings and Version support the document without sitting in the toolpath route.

<div class="component-grid">
{% include component-card.html slug="calib-points" title="Calib Points" icon="IamCalibPoints" panel="7 Print" description="Supply measurement positions to Machine Console for SCARA calibration." %}
{% include component-card.html slug="scara-fit" title="Scara Fit" icon="IamCalibFit" panel="7 Print" description="Fit measured SCARA positions; use the arm lengths in Robot Definition." %}
{% include component-card.html slug="load-settings" title="Load Settings" icon="IamSettings" panel="1 Slice" description="Restore saved component settings in a Grasshopper document." %}
{% include component-card.html slug="version" title="Version" icon="IamVersion" panel="1 Slice" description="Report the installed plugin version and runtime." %}
</div>
