---
title: "IAMLab Pareto Select"
nav_order: 14
parent: "Components"
description: "IAMLab Pareto Select: component inputs, outputs, and usage notes."
permalink: /docs/components/iamlab-pareto-select/
---

# IAMLab Pareto Select

IAMLab Pareto Select shows the trade-off front that IAMLab Desirability
found and lets you pick one recipe from it by clicking. The chosen
recipe - its settings, its score per goal and its overall `D` - comes out
of the component and stays with the document, even when the DoE components
upstream are switched off or their front changes.

Panel: `6 Calibrate`. Nickname: `Pareto`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-pareto-select.png" alt="IAMLab Pareto Select on the canvas" %}

## Inputs

| Name | Type | Description |
|---|---|---|
| `front` | Number (tree) | The `pareto` output of IAMLab Desirability: one branch per recipe with the factor values, one desirability per active goal, and `D` |
| `factorNames` | Text (list) | The factor names in model order (from RSM Evaluate or DoE Design) |
| `objectiveNames` | Text (list) | The names of the **active** goals in model order - skip goals set to `none` |
| `selectionKey` | Text | Pick a recipe without the mouse: the `key` this component gave out earlier. A key from a different front is refused, not remapped. Wins over `selectionIndex` |
| `selectionIndex` | Integer | Pick a recipe without the mouse: the branch number (from 0) of the front that is wired right now. A one-time request - when the front changes, the same number is not applied again |
| `optimumSettings` | Number (list) | Optional: wire Desirability's `settings` to mark its recommendation on the plot. Without it, the recipe with the largest `D` on the front is marked as *best D in supplied front* |

All inputs are optional. Without a front the component still emits a recipe it stored earlier.

## Outputs

| Name | Type | Description |
|---|---|---|
| `settings` | Number (list) | The chosen factor values, in the order of `factorNames` |
| `di` | Number (list) | The chosen recipe's desirability per active goal (0 to 1) |
| `D` | Number | The chosen recipe's overall desirability, as it was on the front |
| `key` | Text | A token that names this recipe on this front - wire it into `selectionKey` to reselect it later |
| `status` | Text | `current`, `front changed`, `restored`, `ambiguous`, or why a request was refused |
| `current` | Boolean | True only when the stored recipe is verified on the front wired right now |

## How to use it

1. Enable the `pareto` group on IAMLab Desirability and compute; wire its `pareto`
   output to `front`, the factor names to `factorNames`, and a panel with the active
   goal names to `objectiveNames`.
2. Click a point on the strip under the component (two goals: a scatter; three or
   more: one line per recipe across one axis per goal; one goal: a strip). Press
   **Pick…** for a larger window; there the arrow keys walk the front and Enter
   confirms. Hovering shows the full recipe.
3. Read `settings` for the print. **Reset** forgets the choice; both are undoable.

The axes are desirabilities from 0 to 1, not measured units: a value of 0.8 on the
`width` axis means the recipe meets the width goal to 80 %.

## What happens when the front changes

The choice is stored by its numbers, not by its position in the list. If Desirability
recomputes and the same recipe is still on the front, the choice stays `current`, even
when the list is in a different order. If the front is different - a goal, a weight or
a bound changed - the component keeps the stored numbers, sets `current` to false, and
says *front changed* on the component; it never quietly switches to another recipe. A
recipe that still matches is shown dashed as a candidate; click it to accept the new
front. Two identical recipes on one front are reported as ambiguous.

## Notes

- The front that Desirability produces is a thinned sample of the trade-off, with the
  optimum re-inserted (so it can hold one recipe more than `maxFront`). With factor
  priors, the recommendation from Desirability may not be on the front at all; the plot
  then says so instead of marking the wrong point.
- Reopening a document restores the recipe, the last front (labelled *archived*) and the
  marker without running the DoE components, and never opens a window.

Back to [Components]({{ '/docs/components/' | relative_url }}).
