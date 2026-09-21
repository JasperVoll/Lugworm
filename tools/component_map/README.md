# Component map generator

`component-map.json` describes the figure on the Component Map page. `build_component_map.py` turns it into `_includes/component-map.svg`, which the page includes inline. Edit the JSON, rerun the script, commit both.

```
python tools/component_map/build_component_map.py
```

Python 3, standard library only. `DESIGN.md` records the decisions behind the layout.

## Nodes

One entry per box. `column` indexes into `columns`; `y` is the top edge in pixels. `kind` picks the size and style (`spine`, `hub`, `satellite`). `slug` links to `docs/components/iamlab-<slug>.md`; `null` means no link. Optional `x_offset`, `width`, `height` override the defaults.

Icons come from `assets/images/icons/svg/<icon>.svg`. Those are the plugin's icon sources; `GhGeometry.svg` is the Grasshopper Geometry parameter icon and `IamVersion.svg` is a vector redraw of the runtime-drawn Version icon.

## Edges

Anchors have the form `id.side[+offset]`, for example `slice.t-30`: node `slice`, top side, 30 px left of centre. Sides are `l`, `r`, `t`, `b`.

- `{ "style": ..., "from": "a.r", "to": "b.l" }` routes orthogonally between two anchors.
- `{ "style": ..., "points": [...] }` draws an explicit polyline. Each point is an anchor string, an absolute `[x, y]`, or a step `[x, null]` / `[null, y]` that keeps the other coordinate. A coordinate may also be an anchor string to borrow that axis, as in `[null, "gen.r"]`.
- `"arrow": false` drops the arrowhead. `"fan_out": { "targets": [...], "side": "r" }` adds a short arrow from the last point's x into each target's side.

Styles: `spine`, `direct`, `thin`, `dashed`. Widths and colours are in `style`.
