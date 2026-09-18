---
title: "IAMLab Desirability"
nav_order: 2
parent: "Components"
description: "IAMLab Desirability: component inputs, outputs, and usage notes."
permalink: /docs/components/iamlab-desirability/
---

# IAMLab Desirability

IAMLab Desirability takes the fitted models and a goal for each
measured result, and finds the combination of settings that meets all
goals best. The result is a recommended set of settings, a score `D`
between 0 (no goal met) and 1 (all goals met), and a report. The
component has a compute button.

Panel: `6 Calibrate`. Nickname: `Desire`. Compute button: yes (runs only when the button is clicked).

{% include figure.html src="components/iamlab-desirability.png" alt="IAMLab Desirability on the canvas" %}

## Inputs

| Name | Type | Description |
|---|---|---|
| `models` | Generic (list) | Fitted RSM models from the RSM component, or their JSON strings, one per response |
| `goals` | Text (list) | Goal per response, same order as models: 'min', 'max', 'target=7 in 6..8', 'range=6..8' or 'none'. Add 'w=2' for weight or 's=2' for shape. min and max ramp over 'a..b' when given, else over the observed data range |

### Optional group: Factor priors (prefer low/high factor levels)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `factorPriors` | Text (list) | Optional prior per factor: 'none', 'low', 'high' or 'target=x'. Add 'w=' for weight. For example prefer a low layer height for bonding |

### Optional group: Search bounds / grid resolution

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `lows` | Number (list) | Search lower bound per factor (default: model coding range) |
| `highs` | Number (list) | Search upper bound per factor |
| `gridPerDim` | Integer | Grid resolution per factor, clamped so the total stays at or below 500k points |

### Optional group: Recipe sweep (one recipe per target value)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `sweepResponse` | Text |  | Response name to sweep. Its goal becomes target=v for each sweep value |
| `sweepValues` | Number (list) | 6.5; 7; 7.5; 8 | Target values for the sweep, one optimized recipe each |
| `sweepBand` | Number | 0.4 | Acceptance band around each sweep target, plus or minus, in response units |

### Optional group: Pareto front (best trade-offs between goals)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `paretoOn` | Boolean | Also collect the Pareto front of tradeoffs over the individual response desirabilities. Off by default because it evaluates every goal even where the scalarized D is 0 |
| `maxFront` | Integer | Front thinning cap by crowding distance. The best recipe per objective always survives |

### Optional group: Excel export input

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `xlsxPath` | Text | Write the optimization and sweep report as .xlsx |

## Outputs

| Name | Type | Description |
|---|---|---|
| `settings` | Number (list) | Recommended factor settings in actual units, factor order of the models |
| `D` | Number | Overall desirability, 0 to 1 |
| `predicted` | Number (list) | Predicted responses at the recommended settings |
| `report` | Text | Readable optimization report. Connect a panel |
| `alternatives` | Number (tree) | Distinct local optima of the scalarized D, not a Pareto set (see the pareto output). One branch per recipe: factor values, then D |
| `sweep` | Number (tree) | Sweep recipes, one branch per target: target, factors, D, predictions |
| `info` | Text | Diagnostics |
| `interval` | Number (tree) | 95% prediction interval at the recommendation, one branch per response: low, predicted, high. Empty when the model carries no covariance |
| `pareto` | Number (tree) | Pareto front recipes, with the pareto group enabled. One branch per recipe: factor values, d per active goal, D. Objectives follow the model order, skipping 'none' goals. See the report for axis names |

## Notes

- `goals` uses a short text per result: `max`, `min`, `target=7 in
  6..8` (aim for 7, accept 6 to 8), `range=6..8`, or `none`. Add `w=2`
  to weight a goal or `s=2` to change the shape of the ramp.
- `alternatives` lists other good combinations for the same overall
  goal. For the best trade-offs between the individual goals, enable
  the Pareto group and read the `pareto` output.
- The Sweep group produces one recipe per target value, for example one
  set of settings for each desired bead width.
- `interval` gives the uncertainty of the predicted results at the
  recommended settings.
- After the first solve, a small draggable curve appears below the
  desirability plot; the goal ramps can be adjusted there.

Back to [Components]({{ '/docs/components/' | relative_url }}).
