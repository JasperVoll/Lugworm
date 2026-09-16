# Calibration and DoE

Print settings for a new material can be found with a planned series
of test prints instead of trial and error. The method is called design
of experiments (DoE). Four components cover the whole loop:

```
IAMLab DoE Design --> print and measure the runs --> IAMLab RSM Evaluate --> IAMLab Desirability
        ^
        |
IAMLab Test Geometry  (the same sample shape for every run)
```

<!-- IMAGE TODO: canvas screenshot of the four calibration components wired in
     sequence, with the on-canvas effect-bar and contour plots visible. -->
![The calibration loop on the canvas](images/doe-loop.png)

## 1. Plan the test prints (DoE Design)

Enter two to six settings that you want to tune, for example
`printSpeed`, `pressureOffset`, and `layerHeight`, each with a lowest
and a highest value. Choose a plan type: DSD when many settings should
be screened with few prints, BBD when the influence of each setting
should be modelled in detail, or a Taguchi or factorial plan. Export
the run table as Excel or CSV. Each row is one test print with its own
combination of settings.

The optional power group estimates, before any clay is mixed, whether
the plan has enough runs to detect an effect of a given size, for
example a 0.3 mm difference. A warning is shown when the plan is too
small.

## 2. Print and measure

Print one Test Geometry sample per row with the settings of that row.
Measure the results by hand, for example the dimensional error, a
surface score, or the height at which the part collapsed, and enter
the numbers into the exported table.

<!-- IMAGE TODO: photo of a row of printed calibration coupons (cylinders or
     vases) from an actual DoE campaign. -->
![Printed DoE coupons](images/doe-coupons.png)

## 3. Fit a formula to the measurements (RSM Evaluate)

Point `tablePath` at the completed table. Excel and CSV are accepted.
CSV files may use English (`1.5`) or German (`1,5`) numbers; the format
is detected per file, and a file that mixes both is refused with the
line named. Choose how detailed the formula should be: Linear, 2FI
(with interactions between settings), or Quadratic (with curvature).

The report shows how strongly each setting influences each measured
result, how well the formula fits the data, and diagnostics that reveal
whether single runs or overlapping settings distort the fit. A weak fit
is flagged, because a recommendation from a weak formula is only as
reliable as that formula.

## 4. Get a recommendation (Desirability)

State one goal per measured result in a short text: `max`, `min`,
`target=7 in 6..8` (aim for 7, accept 6 to 8), or `range=2..5`,
optionally with a weight `w=` and a shape `s=`. Read `settings`, `D`
(how well all goals are met, from 0 to 1), and the report.
Alternatives whose predictions all fall inside the prediction intervals
of the recommendation are marked `within-PI`: the data cannot rank them
against the recommendation (it is a coverage check, not a proof that they
are equal), and the marker is withheld when a formula has no interval.
The optional `pareto` group adds a list of the best trade-offs between
the goals for cases where no single setting satisfies all of them.

## 5. Pick a trade-off yourself (Pareto Select)

Wire Desirability's `pareto` output, the factor names and the names of
the active goals into [[IAMLab Pareto Select]]. The component draws the
front - a scatter for two goals, one line per recipe across one axis per
goal for more - and you click the recipe you want. Its settings come out
of the component and are stored with the document, so the DoE components
can be switched off afterwards. When Desirability is recomputed with
other goals or weights, the stored recipe is kept but flagged as *front
changed* until you pick again; it is never silently replaced.

<!-- IMAGE TODO: Pareto Select strip under Desirability with a selected recipe
     ringed and the optimum marked. -->
![Picking a recipe on the front](images/doe-pareto-select.png)

After the first solve, a small draggable curve appears below the
desirability plot (a prototype). The ramp, target, and shape of each
goal can be dragged there instead of editing the goal text.

<!-- IMAGE TODO: screenshot of the Desirability component with the profile
     plot and the goal-curve strip below it. -->
![Desirability profile with the goal-curve editor](images/doe-desirability.png)

## Related calibrations

- Pump (MAI 2PUMP PICTOR-3D): the pump group of the G-code Generator
  calculates a recommended pump setting from a measured table of pump
  setting against flow rate. Measuring the table takes about 15 minutes
  per material (run the pump into a bucket at several settings and
  weigh the result). The shipped table is a placeholder. The pump group
  never changes the extrusion values in the G-code.
- Overhang thresholds: `warnDeg` and `failDeg` (25° and 45°) are
  placeholders. Calibrate them with a test print of a stepped cone.
- SCARA arm geometry: the 7 Print components [[IAMLab Calib Points]] and
  [[IAMLab Scara Fit]] drive the machine to a set of marks, and fit the
  real arm lengths from the measured positions.