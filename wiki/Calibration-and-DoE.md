# Calibration and DoE

Use design of experiments (DoE) to find material settings with planned prints:

```
IAMLab DoE Design --> print and measure the runs --> IAMLab RSM Evaluate --> IAMLab Desirability
        ^
        |
IAMLab Test Geometry  (the same sample shape for every run)
```

<!-- IMAGE TODO: canvas screenshot of the four calibration components wired in
     sequence, with the on-canvas effect-bar and contour plots visible. -->
![The calibration loop on the canvas](images/doe-loop.png)

## 1. Plan (DoE Design)

Enter two to six settings, such as `printSpeed`, `pressureOffset`, `layerHeight`,
with low/high bounds. Choose DSD for screening, BBD for detailed modelling,
or Taguchi/factorial. Export Excel or CSV; each row defines one print.
The optional power group estimates whether enough runs exist to detect a
specified effect and warns if the plan is too small.

## 2. Print and measure

Print one Test Geometry sample per row. Enter measured dimensional error,
surface score or collapse height in the exported table.

<!-- IMAGE TODO: photo of a row of printed calibration coupons (cylinders or
     vases) from an actual DoE campaign. -->
![Printed DoE coupons](images/doe-coupons.png)

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

Wire `pareto`, factor names and active goal names into [[IAMLab Pareto Select]].
Click a recipe on the two-goal scatter or multi-goal parallel-axis plot. Its
settings are stored in the document, allowing DoE components to be switched off.
Recomputing Desirability with changed goals/weights preserves the recipe but
flags *front changed* until you select again; it is never silently replaced.

<!-- IMAGE TODO: Pareto Select strip under Desirability with a selected recipe
     ringed and the optimum marked. -->
![Picking a recipe on the front](images/doe-pareto-select.png)

After solving, drag the curve below Desirability to edit goal ramp, target and shape.

<!-- IMAGE TODO: screenshot of the Desirability component with the profile
     plot and the goal-curve strip below it. -->
![Desirability profile with the goal-curve editor](images/doe-desirability.png)

## Related calibrations

- **Pump (MAI 2PUMP PICTOR-3D):** measure flow at several settings by pumping into
  a bucket and weighing. Generator's pump group recommends a setting from this
  table; the shipped table is a placeholder. It never changes G-code extrusion.
- **Overhang:** `warnDeg` and `failDeg` (25° and 45°) are placeholders;
  calibrate with a stepped-cone print.
- **SCARA:** [[IAMLab Calib Points]] drives to marks; [[IAMLab Scara Fit]] fits
  arm lengths from measured positions.
