---
title: "Graph Mapper"
nav_order: 7
parent: "Components"
description: "Graph Mapper: component inputs and outputs."
permalink: /docs/components/graph-mapper/
redirect_from:
  - /docs/components/iamlab-graph-mapper/
---

# Graph Mapper

Graph Mapper changes a list of numbers by passing them through a
curve that you draw on the component. A typical use is a flow or speed
value that should change gradually from the bottom to the top of a
print: feed the layer numbers in, draw the curve, and read the remapped
values out. The component does the same job as the Graph Mapper that
ships with Grasshopper, with more curve types and editable points.

Panel: `1 Slice`. Nickname: `Graph Map`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-graph-mapper" %}

## Inputs

| Name | Type | Description |
|---|---|---|
| `t` | Number (list) | Values to remap through the curve |

### Optional group: Input / output domains (default unit ↦ unit)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `inMin` | Number | 0 | Input domain start (maps to the graph's left edge) |
| `inMax` | Number | 1 | Input domain end (maps to the graph's right edge) |
| `outMin` | Number | 0 | Output range at the graph's bottom edge |
| `outMax` | Number | 1 | Output range at the graph's top edge |
| `clip` | Boolean | true | Control the out-of-domain remark when false, with values outside the domain pinned to the graph edges either way |

## Outputs

| Name | Type | Description |
|---|---|---|
| `y` | Number (list) | Remapped values, one per input |
| `equation` | Text | The curve as a formula over normalized t |
| `info` | Text | Graph type, domains, value count |
| `map` | Text | Curve definition text for Slice's profile input, used with infill 6 and Wave inputs enabled |

Back to [Components]({{ '/docs/components/' | relative_url }}).
