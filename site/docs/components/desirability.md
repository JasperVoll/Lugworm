---
title: "Desirability"
nav_order: 2
parent: "Components"
description: "Desirability: component inputs and outputs."
permalink: /docs/components/desirability/
redirect_from:
  - /docs/components/iamlab-desirability/
---

# Desirability

Desirability takes the fitted models and a goal for each
measured result, and finds the combination of settings that meets all
goals best. The result is a recommended set of settings, a score `D`
between 0 (no goal met) and 1 (all goals met), and a report. The
component has a compute button.

Panel: `6 Calibrate`. Nickname: `Desire`. Compute button: yes (runs only when the button is clicked).

{% include figure.html src="components/iamlab-desirability" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `models` | Generic (list) |  | RSM Evaluate models or JSON output, one fitted model per response |
| `goals` | Text (list) | target=7 in 6..8 w=2; min; min | One goal per model in matching order: min, max, target=7 in 6..8, range=6..8 or none (optional w=2, s=2) |

### Optional group: Factor priors (prefer low/high factor levels)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `factorPriors` | Text (list) | low w=1.5; none; high w=1.5 | Optional prior per factor: 'none', 'low', 'high' or 'target=x'. Add 'w=' for weight. For example prefer a low layer height for bonding |

### Optional group: Search bounds / grid resolution

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `lows` | Number (list) |  | Search minima in actual units, one per factor in model order (otherwise model limits) |
| `highs` | Number (list) |  | Search maxima in actual units, one per factor in model order (otherwise model limits) |
| `gridPerDim` | Integer | 15 | Grid resolution per factor, clamped so the total stays at or below 500k points |
| `feasibleThreshold` | Number | 0.5 | Predicted feasibility below this value is treated as unprintable |

### Optional group: Recipe sweep (one recipe per target value)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `sweepResponse` | Text |  | Model response name to optimize at each sweepValues target, required for the sweep |
| `sweepValues` | Number (list) | 6.5; 7; 7.5; 8 | Target values in sweepResponse units, one optimized recipe per value |
| `sweepBand` | Number | 0.4 | Acceptance band around each sweep target, plus or minus, in response units |

### Optional group: Pareto front (tradeoffs)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `paretoOn` | Boolean |  | Collect the non-dominated recipes over the individual goal desirabilities, for Pareto Select. Goal bounds shape the front, weights do not |
| `maxFront` | Integer | 200 | Maximum retained tradeoff recipes, preserving each objective's best recipe even if this cap is exceeded |

### Optional group: Excel export input

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `xlsxPath` | Text | Local .xlsx file or folder to export on Compute with Export enabled, overwriting the target |

## Outputs

| Name | Type | Description |
|---|---|---|
| `settings` | Number (list) | Recommended factor settings in actual units, factor order of the models |
| `D` | Number | Overall desirability, 0 to 1 |
| `predicted` | Number (list) | Predicted responses at the recommended settings |
| `report` | Text | Readable optimization report. Connect a panel |
| `alternatives` | Number (tree) | Other local best recipes, one branch each: factor values in model order, then overall desirability D |
| `sweep` | Number (tree) | Sweep recipes, one branch per target: target, factors, D, predictions |
| `info` | Text | Diagnostics |
| `interval` | Number (tree) | 95% prediction limits in response units, one branch per response: low, predicted, high (empty without model uncertainty data) |
| `pareto` | Number (tree) | Tradeoff recipes with Pareto enabled and paretoOn true, one branch each: factors, active-goal desirabilities, D. Wire to Pareto Select front |
| `objectives` | Text (list) | Names of the active goals in model order, without none goals. The axes of the pareto output. Wire to Pareto Select objectiveNames |

Back to [Components]({{ '/docs/components/' | relative_url }}).
