---
title: "Calibration and DoE"
nav_order: 1
parent: "Workflows"
description: "Calibration and DoE: instructions and practical checks for IAMLab Slicer."
permalink: /docs/workflows/calibration-and-doe/
---

# Calibration and DoE

Use design of experiments (DoE) to find material settings with planned prints:

```
IAMLab DoE Design --> print and measure the runs --> IAMLab RSM Evaluate --> IAMLab Desirability
        ^
        |
IAMLab Test Geometry  (the same sample shape for every run)
```

{% include figure.html src="workflows/calibration-loop.png" alt="The calibration loop on the canvas" %}

## 1. Plan (DoE Design)

Enter two to six settings, such as `printSpeed`, `pressureOffset`, `layerHeight`,
with low/high bounds. Choose DSD for screening, BBD for detailed modelling,
or Taguchi/factorial. Export Excel or CSV; each row defines one print.
The optional power group estimates whether enough runs exist to detect a
specified effect and warns if the plan is too small.

## 2. Print and measure

Print one Test Geometry sample per row. Enter measured dimensional error,
surface score or collapse height in the exported table.

{% include figure.html src="workflows/doe-coupons.png" alt="Printed DoE coupons" %}

## 3. Fit (RSM Evaluate)

Set `tablePath` to completed Excel or CSV data. CSV accepts English (`1.5`) or
German (`1,5`) numbers; mixed formats are refused with the line identified.
Choose Linear, 2FI (interactions) or Quadratic (curvature).
Read fit quality, setting effects and diagnostics for distorting runs or
correlated settings. Weak fits are flagged: recommendations depend on fit quality.

## 4. Recommend (Desirability)

Set each goal: `max`, `min`, `target=7 in 6..8`, or `range=2..5`, optionally
weighted with `w=` and shaped with `s=`. Read `settings`, `D` (0 to 1) and the report.
`within-PI` marks alternatives whose predictions all lie inside the recommendation's
prediction intervals. This coverage check cannot rank them or prove equality;
no marker appears without model intervals. Enable `pareto` for trade-offs.

## 5. Select (Pareto Select)

Wire `pareto`, factor names and active goal names into [IAMLab Pareto Select]({{ '/docs/components/iamlab-pareto-select/' | relative_url }}).
Click a recipe on the two-goal scatter or multi-goal parallel-axis plot. Its
settings are stored in the document, allowing DoE components to be switched off.
Recomputing Desirability with changed goals/weights preserves the recipe but
flags *front changed* until you select again; it is never silently replaced.

{% include figure.html src="workflows/pareto-pick.png" alt="Picking a recipe on the front" %}

After solving, drag the curve below Desirability to edit goal ramp, target and shape.

{% include figure.html src="workflows/desirability-goals.png" alt="Desirability profile with the goal-curve editor" %}

## Related calibrations

- **Pump (MAI 2PUMP PICTOR-3D):** measure flow at several settings by pumping into
  a bucket and weighing. Generator's pump group recommends a setting from this
  table; the shipped table is a placeholder. It never changes G-code extrusion.
- **Overhang:** `warnDeg` and `failDeg` (25° and 45°) are placeholders;
  calibrate with a stepped-cone print.
- **SCARA:** [IAMLab Calib Points]({{ '/docs/components/iamlab-calib-points/' | relative_url }}) drives to marks; [IAMLab Scara Fit]({{ '/docs/components/iamlab-scara-fit/' | relative_url }}) fits
  arm lengths from measured positions.
