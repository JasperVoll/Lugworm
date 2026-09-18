---
title: "IAMLab Graph Mapper"
nav_order: 6
parent: "Components"
description: "IAMLab Graph Mapper: component inputs, outputs, and usage notes."
permalink: /docs/components/iamlab-graph-mapper/
---

# IAMLab Graph Mapper

IAMLab Graph Mapper changes a list of numbers by passing them through a
curve that you draw on the component. A typical use is a flow or speed
value that should change gradually from the bottom to the top of a
print: feed the layer numbers in, draw the curve, and read the remapped
values out. The component does the same job as the Graph Mapper that
ships with Grasshopper, with more curve types and editable points.

Panel: `1 Slice`. Nickname: `Graph Map`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-graph-mapper.png" alt="IAMLab Graph Mapper on the canvas" %}

## Inputs

| Name | Type | Description |
|---|---|---|
| `t` | Number (list) | Values to remap through the curve |

### Optional group: Input / output domains (default 0..1 to 0..1)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `inMin` | Number | 0 | Input domain start (maps to the graph's left edge) |
| `inMax` | Number | 1 | Input domain end (maps to the graph's right edge) |
| `outMin` | Number | 0 | Output range at the graph's bottom edge |
| `outMax` | Number | 1 | Output range at the graph's top edge |
| `clip` | Boolean | true | Clamp inputs into the input domain before mapping. The curve is always evaluated inside the graph area, so inputs outside the domain pin to the graph edges either way. This switch only changes the 'unclipped' remark |

## Outputs

| Name | Type | Description |
|---|---|---|
| `y` | Number (list) | Remapped values, one per input |
| `equation` | Text | The curve as a formula over normalized t |
| `info` | Text | Graph type, domains, value count |
| `map` | Text | The curve itself, for components that evaluate it. Connect it into the profile input of IAMLab Slice to drive a graph wave infill |

## Notes

- Curve types are chosen from the dropdown on the component. Polyline
  and Spline are fully editable: double-click adds a point, double-click
  on a point removes it, and any other curve type can be converted into
  them.
- Spline points carry tangent handles. Hover a point to reveal them; a
  plain drag keeps the point smooth (both handles follow together),
  Shift moves one side only and turns the point into a corner, Ctrl
  snaps the dragged handle's slope to a quarter step, and double-click
  a handle returns it to the automatic tangent. A point converted from
  another curve type starts with automatic handles the same way.
- While dragging a point, Ctrl snaps to steps of 0.05 and Shift to
  steps of 0.01. Ctrl+Z undoes the last edit; the reset button restores
  the current curve type.
- The Domain group sets which input values map to the left and right
  edge of the graph and which output values the bottom and top edge
  represent.

Back to [Components]({{ '/docs/components/' | relative_url }}).
