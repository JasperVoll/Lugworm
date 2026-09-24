# Interlayer Time

Find **Interlayer Time** in **Lugworm → 5 Analyze**. It measures, at each
extruding path sample, how long ago the material beneath it was deposited. The
viewport uses a Plasma colour scale; grey means no supporting sample was found.
The scale itself is published on the `legend` output for **Legend**, so
the drawn colours and the legend that explains them come from one definition.

## Start with G-code

1. Connect G-code text or lines to `gcode`, or a saved file path to `gcodePath`.
2. Connect `report` to a panel to see the clock source, totals and per-layer table.
3. Use an integer slider on `layer` to reveal the print up to that layer. Zero
   shows all layers; `isolate` shows only the selected top layer. The slider
   scrolls the preview **and** cuts `perLayer`, `times`, `path` and `colors` to
   the same layers, so what you measure downstream is what you see.
4. Set `minTime` and `maxTime` in seconds for your material and process. Zero
   disables that threshold. The initial maximum of 120 seconds is a display
   threshold, not a validated material limit.

The estimated clock uses the file's commanded feeds. Enable the **Feed override
inputs** context-menu group to set fallback print/travel feeds or the optional
acceleration model. This estimate does not measure machine execution time.
Operator pauses and heater waits have no known duration in the file; use a log
when their actual elapsed time matters.

## Use a recorded print

Connect Live View's CSV `log` output to `log`, or a `printlog_*.csv` / retained
job-log file to `logPath`. Pair it with the exact original `gcodePath` for byte
position anchors. Text input reconstructs byte offsets and reports that limitation.
The component reports whether it used file-position anchors, layer spans, or kept
the estimate. Timing between log anchors is interpolated using estimated pace.

A log without G-code uses the recorded positions as a sparse path. It cannot
recover the full nozzle trajectory between polls or distinguish extrusion from
travel for every intervening segment. Check `info` for the selected
job run, clock source and parsing diagnostics. File inputs are read in the
background; the previous result remains visible while a replacement loads.

## Geometry and display

Enable **Layer geometry inputs** to override `width`, `height` and the XY search
`radius`, all in millimetres. Width defaults to the G-code header or 10 mm; height
uses the input, header or inferred Z step. Supporting samples must also lie between
half and one-and-a-half layer heights below the current sample. This is a local
sample-based timing analysis, not a structural or adhesion prediction.
Straight moves contribute their endpoints; arcs are tessellated. Long, sparse
straight moves can therefore miss support between vertices.

Enable **Display inputs** and set `display` to 1 for coloured extruded layers
(0 gives toolpath lines). `legendMax` fixes the colour maximum in seconds; zero
chooses an automatic maximum. Layer scrolling does not change that scale. Values
above the maximum saturate at the top colour.

`shade` tells adjacent layers apart when their times are similar: 0 leaves every
layer at its scale colour, 1 (the default) tints every other layer down, and 2
ramps from a darker bottom layer to a full-brightness top layer. Layer meshes also
carry a fixed overhead light, because false-colour meshes are drawn unlit and
would otherwise read as a flat silhouette. Shading is a display aid only — it
says *which layer*, never *how many seconds* — so the `colors` output stays the
unshaded scale colour whatever `shade` is set to.

The component draws no legend of its own. Wire `legend` into **Legend** to
place one: it carries the Plasma scale, the legend maximum, the grey "no layer
below" swatch and tick marks at whichever of `minTime` / `maxTime` are enabled.
See [Legend](legend.md) for placement, fonts and the rest.

The icon sits in the Core/fabrication blues, the same family as the other
toolpath-analysis components. That family colour is unrelated to the Plasma
scale used for the data.

## Outputs

| Output | Content | Follows `layer` |
|---|---|---|
| `report` | Clock, thresholds, totals and per-layer table | no — always the whole print |
| `perLayer` | Branch per layer: layer, Z mm, start s, duration s, minimum/mean/maximum interlayer s, too-fast count, too-slow count | yes |
| `times` | Seconds per path vertex, in `{layer; run}` branches; −1 means unavailable | yes |
| `path` | Extrusion-run polylines in Rhino document units, with matching branches | yes |
| `colors` | Unshaded Plasma colour per path vertex, with matching branches | yes |
| `layers` | Layer count of the whole print, not of the visible cut | no |
| `info` | Source, matching and threshold diagnostics | no |
| `legend` | Legend spec for Legend | no |

Scrolling filters the viewport preview and the four per-layer trees together; the
analysis itself is computed once and is never re-run by moving the slider, so the
seconds a layer reports do not change as you scroll. Set `layer` to 0 to emit the
complete analysis. Sparse sampling, unmatched geometry and partial logs limit what
can be inferred from the colours.
