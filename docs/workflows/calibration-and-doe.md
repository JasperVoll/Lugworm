---
title: "Calibration and DoE"
nav_order: 1
parent: "Workflows"
description: "Plan test prints, fit measured results, and select a material recipe."
permalink: /docs/workflows/calibration-and-doe/
---

# Calibration and DoE

Use design of experiments (DoE) to compare material settings through a planned series of prints. Keep the specimen shape consistent, measure each run, and use the results to choose settings for a confirmation print.

The sequence is **DoE Design → print and measure → RSM Evaluate → Desirability → Pareto Select**. Test Geometry supplies the specimen; printing and measurement happen between planning and fitting.

{% include figure.html src="workflows/calibration-loop.png" alt="The calibration loop on the canvas" %}

## 1. Plan the runs

In [IAMLab DoE Design]({{ '/docs/components/iamlab-doe-design/' | relative_url }}), enter two to six settings, such as `printSpeed`, `pressureOffset`, and `layerHeight`, with a low and high bound for each. Choose a design: DSD for screening, BBD for detailed modelling, or a Taguchi or factorial design.

Export the plan to Excel or CSV. Each row defines one print. The optional power group estimates whether the plan has enough runs to detect a specified effect and warns when it is too small.

## 2. Print and measure

Use [IAMLab Test Geometry]({{ '/docs/components/iamlab-test-geometry/' | relative_url }}) to keep the sample shape the same for every run. Print one sample per row, then enter measurements such as dimensional error, surface score, or collapse height in the exported table. Keep each measurement with the settings that produced it.

{% include figure.html src="workflows/doe-coupons.png" alt="Printed DoE coupons" %}

## 3. Fit and review the models

Set `tablePath` on [IAMLab RSM Evaluate]({{ '/docs/components/iamlab-rsm-evaluate/' | relative_url }}) to the completed Excel or CSV file. CSV accepts English (`1.5`) or German (`1,5`) numbers. Mixed formats are refused, with the affected line identified.

Choose **Linear**, **2FI** for interactions, or **Quadratic** for curvature. Read the fit quality, setting effects, and diagnostics for influential runs or correlated settings. Resolve weak-fit warnings before relying on recommendations: the recommendations depend on these models.

## 4. Define the goals

Connect the fitted models to [IAMLab Desirability]({{ '/docs/components/iamlab-desirability/' | relative_url }}). Set a goal for each response: `max`, `min`, `target=7 in 6..8`, or `range=2..5`. Use `w=` to weight a goal and `s=` to shape it when needed.

Read the recommended `settings`, the combined desirability `D` (0 to 1), and the report. After solving, drag the curve below the component to edit the goal ramp, target, and shape.

The `within-PI` marker identifies alternatives whose predictions all lie within the recommendation's prediction intervals. It does not rank those alternatives or prove they are equivalent. The marker is absent when model intervals are unavailable.

{% include figure.html src="workflows/desirability-goals.png" alt="Desirability profile with the goal-curve editor" %}

## 5. Choose and confirm a recipe

Enable `pareto` on Desirability to explore trade-offs. Connect `pareto`, the factor names, and the active goal names to [IAMLab Pareto Select]({{ '/docs/components/iamlab-pareto-select/' | relative_url }}). Choose a recipe on the two-goal scatter plot or the multi-goal parallel-axis plot.

The selected settings are stored in the Grasshopper document, so you can switch off the DoE components afterward. If you change goals or weights and recompute Desirability, the recipe stays in place and is flagged *front changed* until you select again.

Print the chosen recipe and compare the measurements with the prediction before adopting it for further work.

{% include figure.html src="workflows/pareto-pick.png" alt="Picking a recipe on the Pareto front" %}

## Other calibration tasks

- **Pump flow (MAI 2PUMP PICTOR-3D):** pump into a bucket and weigh the output at several settings. Generator's pump group uses the resulting table to recommend a setting. Replace the shipped placeholder table with measurements; the recommendation does not change G-code extrusion.
- **Overhang limits:** calibrate `warnDeg` and `failDeg` with a stepped-cone print. The defaults of 25° and 45° are placeholders.
- **SCARA geometry:** use [Calib Points]({{ '/docs/components/iamlab-calib-points/' | relative_url }}) with Machine Console to visit measurement marks, then [Scara Fit]({{ '/docs/components/iamlab-scara-fit/' | relative_url }}) to fit arm lengths from measured positions.
