# DoE Calibration Components

Print-parameter calibration workflow after the FBI2026 paper (DSD screening → BBD/RSM →
desirability optimisation), generalised to any number of factors and responses.
Grasshopper tab **Lugworm → 6 Calibrate**.

## Workflow

```
[Test Geometry] → [Slicer] → [Gcode] → print DoE runs
        ↑
[DoE Design] ──(xlsx/csv plan with empty response columns)──> lab measurements
                                                                        │
[RSM Evaluate] ←──(same file, filled)────────────────────────────┘
        │  models
[Desirability] → recommended recipe (+ sweep, e.g. one recipe per target width)
```

### Example data

`example_doe_measurements.xlsx` (next to this page) is a filled three-factor Box-Behnken
table — `layer_height` 2.5…4.5 mm, `print_speed` 30…50 mm/s, `pressure` 1.0…1.3 bar
against `layer_width`, `surface_defects` and `print_time` — for trying RSM Evaluate and
Desirability without a printer. The responses are simulated, not measured: a ground-truth
quadratic surface plus Gaussian measurement noise, written by
`tools/gen_doe_example.py` (`--check` guards the shipped file, and
`tests/tooling/test_doe_example_measurements.py` runs that check).

Quadratic fit, all three responses: R² ≥ 0.991, predicted-R² ≥ 0.964, lack of fit not
significant (p ≥ 0.94), every VIF 1.00, max Cook's D 0.225 — below the 4/n = 0.267 hint
line, so the file is the *clean* reference. Noise is set at about 3× instrument
repeatability on purpose: R² near 0.99 rather than 0.999 keeps the residual plots and
the prediction intervals worth looking at. The hand-typed table shipped before
2026-09-22 had `print_time` as whole minutes with all three centre points reading 6:
pure error was zero (lack-of-fit F infinite) and the ±0.25 rounding residuals reached
Cook's D = 1.50 on runs 6 and 7, i.e. the component warned about the example data rather
than about a real outlier. Corrupting one cell by a factor of ten is still the way to see
the influence warning fire.

The generator's seed is searched, and has to be: Cook's distance is scale invariant here,
so across 4000 simulated draws 27.5 % put some run above D = 1 and 90 % put one above the
4/n hint — at every noise level. A face point of a Box-Behnken carries leverage 0.75,
where D = 0.3·t², so |t| > 1.83 on one run is enough. An uneventful reference table is a
chosen draw on any design this size, not something a quieter instrument would give you.

## 1 - DoE Design (`DoE`)

Inputs: factor `names` / `lows` / `highs` (lists — any count), `design`, `responses`
(become empty columns in the export). Right-click → value list for the design type.

| Design | Factors | Runs | Use for |
|---|---|---|---|
| `DSD` | 4–12 | 2m+1 (even m) / 2m+3 (odd) | screening: which parameters matter |
| `BBD` | 3–7 | 15/27/46/54/62 | full quadratic RSM (the FBI2026 stage 2) |
| `Taguchi` | 2–13 | L4–L27 auto-selected | robust main-effects designs |
| `FullFact2` | 1–10 | 2^k | small exhaustive 2-level studies |
| `FracFact2` | 4–9 | 8 or 16 | regular 2^(k−p) fractions with standard generators; the resolution and aliasing are reported |

Advanced group: non-midpoint `centres` (e.g. pressure 1.15 in [1.0, 1.3]), centre-point
count, Taguchi `levels` (2/3), `seed` for run-order randomization.

| Advanced input | Default | Meaning |
|---|---|---|
| `seed` | 0 | Reproducible random order; -1 keeps standard order. |
| `replicates` | 1 | Copies of every row, including centres, shuffled together. Minimum 1; repeated labels end in r1…rk. |
| `augment` | `none` | `ccd-face` appends face-centred star and centre runs to a two-level plan, see below. |
Export group: `xlsxPath` (styled workbook) and/or `csvPath`.
DSD/conference matrices, BBD blocks and Taguchi arrays are generated deterministically
and verified by unit tests (orthogonality/balance), not copied from tables.

### Augmenting a plan

A two-level plan (`FullFact2`, `FracFact2`) can be extended to a face-centred central
composite design after the fact, as an explicit choice: set `augment` to `ccd-face` and the
component appends 2m star runs at ±1 on each axis (α = 1, so no run leaves the declared
factor range) plus centre runs (`centerPts`, or 3 when it is -1), labelled `aug01…`. The
original rows keep their order and labels, so a block that has already been printed stays
valid; only the added block is shuffled with `seed`, and `info` states what was added. With
the star points a full quadratic becomes estimable where the plain 2^k aliased every pure
quadratic with the intercept. For any other design the component says *augment ignored for
this design* and continues. The fitter never suggests augmenting; lack of fit is reported as
before and the decision stays with you. `example_doe_augmented.csv` next to this page is the
example surface measured on such a plan.

## 2 - RSM Evaluate (`RSM`) — Heavy (Compute button)

| Input | Default | Meaning |
|---|---|---|
| `transform` | `none` | One scale per response: `none`, `log1p`, `sqrt`, `boxcox`. A single value broadcasts. Box-Cox selects and reports its exponent; values must be positive. |
| `computed` | `no` | `yes` or `no` per response, a single value broadcasts. A computed response, such as the slicer's own time estimate, is fitted like the others and enters D and the front, but reports no prediction or confidence interval; the within-PI marker, the confirmation check and the reliability columns skip it. Its report line says *computed response, no measurement error*. |

For counts use log1p or sqrt. When run lengths differ, record surface defects per metre
of layer. `example_doe_poisson.csv` provides simulated Poisson counts alongside the unchanged
Gaussian workbook. Intervals are computed on the fitted scale and inverted bound by bound.

Point `tablePath` at the **filled** plan file (or use the `direct` X/Y tree inputs).
`factorNames` come straight from the DoE component output; `responseNames` default to
all remaining numeric columns. Model: `Quadratic` / `2FI` / `Linear` — aliased terms
(e.g. quadratics on a DSD/Taguchi) are dropped and reported, never silently fitted.

Per response you get: coefficients with SE/t/p, partial-SS ANOVA incl. lack-of-fit
(needs replicate/centre runs), R², adj-R², predicted-R² (PRESS), equations in coded and
actual units, JSON export, and a styled `.xlsx` report.
**On-canvas graphs**: effect-bar chart (red = p < 0.05) + response-surface contour of
the two strongest factors per response. Warnings fire for weak fits (R²adj < 0.5) and
significant lack of fit.

## 3 - Desirability (`Desire`) — Heavy

The optional measurement column `feasible` is 0 for a failed run and 1 (or blank) for a
printable run. Failed rows are omitted from response fits and reported, while all flags
train a separate `feasible` model. Supply goals for the responses only. The bounds input
`feasibleThreshold` defaults to 0.5; predictions below it are excluded from the search
and the front. Front rows append predicted `feasible` when a mask is present.

Wire `models` from the RSM component. One goal string per response:
`min` - `max` - `target=7 in 6..8` - `range=6..8` - `none`, with optional `w=2`
(weight) and `s=2` (shape). Factor priors (group): `low w=1.5` etc. — the physical
priors of the paper (prefer low layer height, high pressure coefficient).
Derringer–Suich weighted geometric mean, exhaustive grid + Nelder–Mead refinement;
alternatives output lists distinct local optima. The sweep group reproduces the paper's
recipe sweep: `sweepResponse=layer_width`, `sweepValues=6.5…9` → one recipe per target.
On-canvas desirability profile shows every individual d and the overall D.

## 4 - Test Geometry (`TestGeo`)

On-canvas dropdown, 13 entries, selection saved with the definition:

- **Shapes** (native RhinoCommon geometry): Cylinder - Vase (sinusoidal) - TwistedVase -
  Frustum - Wall - Box.
- **Standard test specimens** (since 4.2.0, nominal dimensions from
  `Core/Calibration/TestSpecimens.cs`, scaled by `specimenScale`): BendingEN196 (DIN EN
  196-1) - BendingISO178 (DIN EN ISO 178) - BendingASTMD790 (ASTM D790) -
  CompressionEN12390Cube - CompressionEN12390Cylinder (both DIN EN 12390-1/-3) -
  CompressionASTMD695 (ASTM D695). **No conformity claim is made**: these are nominal
  geometries, and both the component remark and the page say so.
- **ZigzagLine** (since 4.2.0): a boustrophedon extrusion-test path. It leaves `geometry`
  empty and emits on the `path` output (index 3) straight into the G-code Generator,
  because putting a curve through the slicer would section a curve rather than a solid.

## 5 - Pareto Select (`Pareto`) — since 3.0.0

Graphical recipe selection on Desirability's trade-off front. Not a replacement plot: an
additive component that reads the `pareto` output and lets the operator **pick** a recipe.

### Why the front is wired to Desirability at all

In the literature the two are often presented as *alternatives*: either scalarise every
response into one Derringer–Suich D and maximise it, or run a multi-objective search on the
raw responses and hand the operator a Pareto front (e.g. Costa et al., Microchemical J. 2019,
who pit a GA-generated front against the desirability function). The plugin does neither in
pure form; it uses the hybrid that Trautmann & Weihs (SFB 475 TR 63/2004) and Lu,
Anderson-Cook & Robinson (Technometrics 2011, the DMRCS *Define–Measure–Reduce–Combine–Select*
process) argue for, because a desirability function does **two separate jobs**:

1. **Per-response scaling.** `d_i(y)` maps one response onto 0..1 "bigger is better" from its
   goal: `min`, `max`, `target=`, `range=` and the acceptance bounds. This is the only thing
   that makes a *target* or *range* goal Pareto-orderable at all - "closeness to 7 mm" has no
   monotone direction in millimetres. The front is computed **in this space**, so its
   dominance ordering uses the goal type and bounds and **never the weights `w=`**.
2. **Aggregation.** The weighted geometric mean folds those numbers into one D. This is the
   preference decision. The optimizer applies it *before* the operator has seen the trade-off;
   the selector applies it **after** - by clicking, or by wiring `weights` to see where a
   preference lands on the front without re-running the optimizer (the DMRCS *Combine* step).

Consequences worth knowing: a raw-response Pareto front and this one differ where a goal's
bounds clip - every recipe below a `max` goal's lower bound has `d = 0` and the whole region
collapses to one representative, and everything above its upper bound is `d = 1` alike. That is
the intended "preference-based Pareto" reading (Trautmann & Mehnen 2009): the bounds say what
is acceptable, the front then shows the trade-offs among acceptable recipes plus each
objective's extreme. The shape exponent `s=` is a monotone re-parametrisation of one axis, so it
cannot change which recipe dominates which, only where the points sit on the plot and which
interior points crowding-distance thinning keeps. Factor priors are preferences on the settings,
not competing responses; they are excluded from the axes and included in the D column, which is
why the optimizer's recommendation can sit off the front. And because the front's candidates
come from the same grid the scalarised search walks, it is a **sample** of the true trade-off
surface. Three kinds of row are Nelder–Mead-refined off the grid: the D-optimum, its
D-alternatives, and (since 2026-09-21) **each objective's extreme** - the grid cell where `d_i`
peaks is polished on that one `d_i` alone, never on D, so the front's endpoints are the local
best-per-objective recipes the report names and the weights still never touch the front. A
plateau extreme (`d_i` already 1) and an infeasible objective (`d_i` 0 everywhere) are left
as they are. After thinning, interior rows are locally refined with augmented weighted
Tchebycheff search (up to 200 evaluations each), and dominance is checked again. Rows can
therefore move off the grid without being dominated by another retained recipe.

**Inputs (all optional at the GH level, so an archived recipe still emits without upstream data):**

| Input | Meaning |
|---|---|
| `front` (number tree) | Desirability output 8: one branch per recipe, exactly `[x0..x(m-1), d0..d(k-1), D]`. No branch flattening; a malformed tree (wrong arity, non-finite factor, d or D outside 0–1) is rejected with the branch named and the last recipe is kept. |
| `factorNames` (text list) | Exactly m names in model order; values stay in actual factor units. |
| `objectiveNames` (text list) | Exactly k **active-goal** names in model order, excluding `none` goals - wire Desirability's `objectives` output, which is exactly that list. The axes are desirabilities (0–1), never raw response units. |
| `selectionKey` (text) | Headless request: a key this component issued. It binds a recipe to the front it was issued for; a key from another front is refused, never remapped. Takes precedence over `selectionIndex`. |
| `selectionIndex` (integer) | Headless request: zero-based branch ordinal of the **current** front. A one-time request - the same ordinal is not re-applied when the front changes. |
| `optimumSettings` (number list) | Wire Desirability's `settings` to mark its recommendation. Without it the largest supplied D is marked as *best D in supplied front* - not necessarily the optimizer's recommendation, because factor priors shape D and the response-only front cannot reconstruct it. |
| `weights` (number list) | One importance per active objective, `objectiveNames` order, each ≥ 0 with at least one > 0. Marks the recipe with the greatest weighted geometric mean of the front's **own** `d_i` (green square, legend *best for w=…*). This is the Derringer–Suich aggregation applied after the front, over response goals only - a preference probe, never a replacement for D. A weight of 0 drops that axis; wrong count, a negative value or an all-zero list is refused with a warning, never guessed. Not archived: it is a live-input decoration. |

**Outputs:** `settings`, `di`, `D`, `key`, `status`, `current`. No selection → empty recipe
outputs and an explicit status. A stale selection → the stored numbers, a warning and
`current = false`.

**Picking:** k = 2 draws a scatter over d0/d1 (0–1 both ways) with the selection ringed, the
optimum marked (amber diamond) and the weighted best (green square) when `weights` is wired;
k > 2 draws parallel coordinates over every active desirability axis,
selecting the nearest polyline; k = 1 draws a one-axis strip. Click on the canvas strip, or
press **Pick…** for a larger window (arrow keys walk the front in d0 order, Enter confirms,
Esc closes). Overlaps resolve deterministically (nearest, then lowest branch index). All three
paths run the same Core selection and schedule one component expiry; **Reset** is explicit and
undoable, and reopening never auto-selects.

**Identity and persistence:** the recipe is stored by its **values** (x, d, D) together with a
canonical front hash (SHA-256 over schema version, ordered names and canonically sorted
records; branch order does not change it, any value, D or name does), never by row index.
Reordering keeps the selection current; a replaced or reweighted front marks it **stale** and
keeps the stored numbers until you select again (a within-tolerance match is reported as a
candidate, dashed, but needs explicit acceptance); duplicates are **ambiguous**. Tolerance is
the versioned policy `|a-b| ≤ 1e-9×max(1, |a|, |b|)`. The document archive holds the recipe,
the displayed front (limits: 10,000 recipes, 256 fields, 256 characters per name, 32 MiB) and
the optimum marker, so the plot and outputs come back without running DoE; a restored plot is
labelled *archived*; reads never open a window.

**Producer semantics to know:** the front Desirability collects is **sampled** from the search
grid and refinement and **thinned** by crowding distance; it can hold one more recipe than
`maxFront` (the optimum is re-inserted) and is a subset of the true trade-off surface. The
selector never re-runs dominance or recomputes D from the visible d (that would omit priors and
weights).

For further reference on optimising in desirability space rather than raw response space, and on applying the weighting after the front, see Trautmann & Mehnen (2009), Engineering Optimization 41(1), 23–38, doi:10.1080/03052150802347926; Wagner & Trautmann (2010), IEEE Transactions on Evolutionary Computation 14(5), 688–701, doi:10.1109/TEVC.2010.2058119; Lu, Anderson-Cook & Robinson (2011), Technometrics 53(4), 353–365, doi:10.1198/TECH.2011.10087; Costa & Lourenço (2015), Transactions on Engineering Technologies, doi:10.1007/978-94-017-9804-4_27.

A recipe's ± value is its 95% prediction-interval half-width: if you print this recipe,
the measured value lands within the prediction interval 19 times out of 20 under the model
assumptions. Transformed intervals are asymmetric, so ± summarises their width; confirmation
uses the stored exact bounds. Hollow means farther from the tested recipes than any run:
the model is extrapolating. Hover or select to see the response values below the window plot.
Units are shown only when the response name includes them, for example `layer_width [mm]`.
A computed response (`computed = yes` on RSM Evaluate) shows its prediction without ±: it has
no measurement error to cover, so no interval is claimed for it and the extrapolation flag
still comes from the design alone.

The current number-tree layout is `[factors…, d…, D, feasible, predictions…, PiHalf…,
PiLo…, PiHi…, leverage, extrapolation, computed…]`, with one 0/1 `computed` flag per
objective. Unknown uncertainty and an absent mask use NaN. The legacy `[factors…, d…, D]`
layout, rows without the `computed` flags and version-1 archives still load.

**Confirm.** Print the selected recipe, enable `confirm`, and wire one measured value per
active response into `confirmValues` in `objectiveNames` order. `confirmReport` lists inside
PI or outside PI, the measured and predicted values, and exact bounds. Missing intervals
are reported as unavailable, and a computed response is listed as `computed, not checked`. Confirmation is archived with the recipe and cleared on a
new selection; unwiring the input preserves the saved confirmation.

## Usability (all Lugworm components)


- **`⇤ inputs` button** (also in the right-click menu): one click creates sliders
  (correct ranges/defaults per input), boolean toggles, value lists and text panels for
  every visible unconnected input and wires them up.
- Redesigned glyph icon per component (runtime-drawn, no assets).
- Component message bubble shows the key state (design + run count, model count, D).

## Inference, trade-off front & power (since 2.3.0)

- **DoE Design → power group.** Wire `powerSigma` (a previous campaign's RMSE or the
  measurement repeatability) and `powerEffect` (the smallest low→high response change
  worth detecting; one value broadcasts to all factors) and the `power` output reports
  Monte-Carlo power per factor *before any clay is mixed*. Match `powerModel` to the
  analysis you will actually run - power depends on its residual df. Under 0.8 raises a
  warning: add runs, widen ranges, or accept the risk knowingly.
- **RSM Evaluate → `diag` output.** Per run: leverage, studentized residual, Cook's
  distance (a single bad print run steering the whole surface is the *normal* clay
  failure - Cook's D > 1 warns on the component); per term: VIF (> 10 = the coefficient
  the effect-bar chart shows is not individually interpretable).
- **Desirability → `interval` output.** 95 % prediction interval per response at the
  recommendation - the honest width of "what will the next print measure". Alternatives
  whose predictions all sit inside these intervals are marked `≈best` in the report:
  D = 0.81 vs 0.79 is then explicitly *not* a ranking.
- **Desirability → goal-curve strip (PROTOTYPE).** Below the profile graph, one goal
  at a time can be shaped by dragging: ramp ends (Low/High), the target, and the
  amber shape handle (the exponent `s`). Click the strip title to cycle responses.
  The edit is stored as an *override* of the wired goal text (never modifies it),
  survives save/reopen, is announced as a Remark each solve, and the ✕ box drops it.
  No undo yet - use ✕ (feasibility note of 2026-08-31, archived with the lab memos).
- **Desirability → pareto group.** `paretoOn` collects the non-dominated recipes over
  the individual desirabilities (off by default - it also evaluates regions the
  scalarised D annihilates at d = 0, which is exactly where hidden best-in-class recipes
  live). Branch layout of the `pareto` output: `[factors…, d per active goal…, D]`; the
  `objectives` output names the axes (the active goals in model order) and the report
  names the extreme recipe per objective. Dominance is decided on the goal desirabilities
  and ignores the weights `w=`; they enter only the D column and the re-inserted optimum
  (see §5 for why the front lives in desirability space at all). `maxFront` thins
  by crowding distance, always keeping each objective's best. The `alternatives` output
  remains what it always was - local optima of the scalarised D, not a Pareto set.

### Report wording and the power sheet (3.0.0)

- Desirability's report marks an alternative **within-PI** when every response has a prediction
  interval at the recommendation and every prediction of the alternative lies inside it. This
  is a *coverage check*, not an equivalence test, and the wording says so; when any response has
  no interval (pre-2.3.0 model JSON) the marker is withheld and the gap is reported. The former
  "≈best … statistically indistinguishable" wording overclaimed and is gone. Rankings, goals and
  numbers are unchanged.
- DoE Design's `.xlsx` export gains a **Power** sheet when the power group ran: sigma, alpha,
  model, simulations, seed, MC error and per-factor effect/power (aliased terms named). The
  `DoE_Plan` sheet is cell-for-cell what it is without it. Both exports (and Desirability's) run
  in the background; the plan is captured before the solve returns.

## Core API (Lugworm.Core.Doe)

`DoeDesign` / `TaguchiArrays` (plans) - `RsmFitter`/`RsmModel` (OLS + ANOVA + JSON) ×
`RsmInference` (intervals, Cook's D / leverage, VIF) - `Goal`/`DesirabilityOptimizer` ×
`ParetoFront` (non-dominated sort + crowding thinning) - `PowerAnalysis` (MC power) ×
`XlsxWorkbook`/`XlsxReader`/`TableIo` (dependency-free Excel + CSV, decimal-comma
tolerant) - `DoeExport` (styled reports).
Tests: `DoeTests.cs` (end-to-end plan→measure→fit→optimize loop), `RsmInferenceTests`,
`ParetoTests`, `PowerAnalysisTests` - inference fixtures are hand-derived in the test
comments, not generated by any external tool.
