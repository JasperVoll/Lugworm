# Legend

Find **Legend** in **Lugworm → 5 Analyze**. It explains a data range: a continuous
colour ramp, thresholded classes, named groups, or a size scale, drawn in the viewport,
placed on a plane, or floating at a point. It is also a colouriser — the `colors` output
paints any geometry with the same scale the legend shows, so a picture and its key can
never disagree.

Nothing about it is specific to Lugworm data. Wire your own numbers into `values` and it
works as a general-purpose legend.

## Wire it to Overhang or Interlayer Time

The quickest use is one wire. **Overhang** and **Interlayer Time** each have
a `legend` output carrying an `iamlab-legend:v1` description of the scale they painted:
the colour scheme, the domain, the grey "unavailable" swatch and any tick marks. Connect
that to the Legend component's `legend` input and the legend is finished — it shows the
colours those components actually used, including their thresholds.

Every other input **overrides the wire field by field**. Leave `title` unwired and the
wire's title is used; wire a title and yours wins. That is how you keep the scale honest
while restyling everything around it. The resolved result comes back out on `spec`, in the
same format, so you can feed one legend into another.

## Or describe the scale yourself

Without a wire, three inputs are usually enough:

- `values` — the data. Also sets the domain when `min` and `max` are unwired, and gets one
  colour per entry on the `colors` output.
- `min` / `max` — the domain start and end. Unwired they come from the wire, else from
  `values`, else 0 and 1. If every value is identical the domain is widened by 1 and the
  component says so on `info`. The `⇤ inputs` button makes them sliders: `min` 0–100 at 0,
  `max` 0–1000 at 100; widen a slider when a scale needs more.
- `scheme` — the colour scheme.

`type` picks what kind of legend it is:

| `type` | Legend | Uses |
|---|---|---|
| 0 | continuous ramp (default) | `min`, `max`, `ticks`, `decimals` |
| 1 | classes | `breaks` as ascending thresholds — *k* thresholds make *k+1* classes — and `labels`; a class without a name shows its own range |
| 2 | groups | `labels` as the category names, one colour each |
| 3 | size | a circle-size scale; `breaks` are the sample values shown, `thickness` is the largest radius |

`show` draws it in the viewport; the outputs solve either way, so you can switch the
drawing off and still bake the mesh. Switching it off clears the viewport at once: the
component keeps reporting a clipping box in every state (for the viewport overlay a point at
the world origin, which Zoom Extents then includes), because Rhino 8 does not regenerate a
preview that reports none - before 2026-09-16 a switched-off legend stayed on screen until the
camera moved.

## Colours

`scheme` takes one of 35 palettes chosen for engineering data, **by name or by number**. The
[palette table](../reference/legend-palette-table.md) lists every number with its colours,
and the value list the `⇤ inputs` button creates offers all 35, grouped by kind, e.g.
`Diverging: coolwarm (14)`. Typing `14` into a Panel works as well as typing `coolwarm`.

| Kind | Numbers | Palettes | Use for |
|---|---|---|---|
| Sequential, perceptual | 0–5 | `viridis`, `plasma`, `inferno`, `magma`, `cividis`, `turbo` | any magnitude: temperature, time, height, flow. `cividis` is colour-blind safe, `turbo` replaces jet |
| Sequential, single hue | 6–13 | `Greys`, `Blues`, `Greens`, `Oranges`, `Reds`, `Purples`, `YlOrRd`, `YlGnBu` | one quantity per figure, readable in print and greyscale |
| Diverging | 14–23 | `coolwarm`, `RdBu`, `RdYlBu`, `Spectral`, `PuOr`, `BrBG`, `PiYG`, `bwr`, `seismic`, `RdYlGn` | signed values around zero: deviation, residuals, over- and under-extrusion. `coolwarm` is the ParaView/FEA default, `PuOr` the colour-blind-safe one |
| Cyclic | 24–26 | `twilight`, `hsv`, `rainbow` | angles: seam position, orientation, raster direction |
| Qualitative | 27–33 | `tab10`, `tab20`, `Set1`, `Set2`, `Dark2`, `Paired`, `Okabe-Ito` | classes, objects, layers, DoE runs. `Okabe-Ito` is the colour-blind-safe reference |
| Lugworm | 34 | `traffic` | the safe / warn / critical three-step Overhang uses for its classes |

Numbers never change: a palette added later gets the next free number. Names are
case-insensitive. The old Lugworm ramp names still work: `blues`, `greens` and `greys` are the
matplotlib maps of the same name, `pinks` is `Purples`, `ambers` is `Oranges` and `gray` is
`Greys`. An unknown name or number raises a warning and falls back to `blues` rather than
guessing. The 0.1.0-beta baseline removed the ggsci journal and theme palettes (`npg`, `lancet`, …) and
the rarely used matplotlib and R ones. A document that still names one draws blues and says so.

Categorical palettes (`tab10`, `Set1`, `Okabe-Ito`, …) hand their colours to classes
and groups one by one, in palette order; a ramp is sampled evenly over the classes instead.

For your own colours, wire `colors`: ramp stops for a continuous or size legend, one colour
per class or group otherwise. Wiring `colors` while leaving `scheme` unwired switches the
scheme to `custom` for you.

To use Grasshopper's own **Gradient** component, wire its colour output straight into
`gradient`. The Legend reads the gradient's grips — each colour *and* its position — so an
uneven gradient is reproduced exactly, and the positions travel on the wire (`stops=`).
Any other colour list wired into `gradient` is treated as evenly spaced stops; when both
`colors` and `gradient` are wired, `gradient` wins.

`reverse` flips the scale direction. `naLabel` names the grey "unavailable" swatch. Left
unwired it takes whatever the wire carries; once wired, a text shows the swatch and an
empty string removes it, overriding the wire either way. `unit` is appended to every
number.

## Placement

`placement` chooses one of three, and `anchor` means something slightly different in each:

- **0, viewport overlay** (default) — a 2D overlay pinned to the viewport. `anchor` is one
  of eight positions (0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right, 4 top,
  5 bottom, 6 left, 7 right) and `margin` is the distance from the viewport edge in pixels.
  Zooming and orbiting do not move it.
- **1, fixed to `plane`** — real geometry in model space, in model units, with the plane
  origin at the corner `anchor` names. This is the one to bake: `mesh`, `labels` and
  `frames` describe it exactly.
- **2, floating at `point`** — a camera-facing billboard at `point`. It lives in the model
  but keeps a constant size on screen, so it stays readable at any zoom. `mesh` and `frames`
  are still emitted, at a nominal one model unit per pixel; the viewport draw rescales them
  each frame, which `info` notes.

The `mesh` and `frames` outputs are preview-hidden: the component draws the legend itself,
so Grasshopper's own preview of those outputs would only add wire edges, plane icons and — for
the floating placement — a second, static copy of the legend. Bake or wire them as usual;
they simply do not draw on their own.

`orientation` is 0 horizontal (default) or 1 vertical. `length` is the bar length and
`thickness` the bar thickness, swatch size, or largest size-circle radius — pixels for the
viewport and floating placements, model units on a plane. An invalid `plane` falls back to
World XY with a warning.

## Text

`font` accepts any installed face; the value list offers Segoe UI (the default), Arial,
Calibri, Consolas, Cambria, Verdana and Times New Roman. `textSize` is the text height, again pixels for
viewport and floating placements and model units on a plane. `textColor` sets the text and
tick colour. `decimals` fixes the decimal places, or `-1` (the default) derives them from
the tick step. `ticks` sets how many ticks a continuous scale gets, or `0` for automatic
"nice" ticks. `frame` draws a panel behind the legend, which is worth switching on over
busy geometry.

The `style`, `text` and `placement` inputs live in context-menu groups; switch a group on
from the component's right-click menu when you need it.

## Outputs

| Output | Content |
|---|---|
| `colors` | one colour per `values` entry, from this scale — wire it into a Custom Preview or a mesh's vertex colours to paint geometry consistently with the legend |
| `mesh` | the legend as a vertex-coloured mesh in model space (plane and floating placements; bakeable) |
| `labels` | the legend texts, one per `frames` plane |
| `frames` | the text planes matching `labels` — feed both into a Text Tag 3D, or bake them |
| `spec` | the resolved scale as an `iamlab-legend:v1` wire; forward it to another Legend |
| `info` | diagnostics: type, domain or class count, scheme, value count, placement and anchor |

## Recipes

- **Explain an Interlayer Time preview.** `legend` → `legend`, `placement` 0, `anchor` 2.
  One wire, nothing else set.
- **Explain an Overhang preview with your own wording.** Wire `legend`, then wire `title`
  and `labels` to override just those.
- **Bake a legend next to the print.** `placement` 1, a `plane` beside the bed, `textSize`
  in millimetres, then bake `mesh` and use `labels` + `frames` for the text.
- **Colour geometry without drawing a legend.** Wire `values`, `min`, `max` and `scheme`,
  take `colors`, and set `show` to false.
- **A pass/fail key.** `type` 1, `breaks` at the limits, `scheme` `traffic`, `labels` naming
  the bands.
- **A category key.** `type` 2, `labels` naming the groups, `scheme` `tab10` (or
  `Okabe-Ito` for a colour-blind-safe set) — each group takes the next palette colour.
- **Your own gradient.** Drop a Grasshopper Gradient, shape it, and wire its colour output
  into `gradient`; the legend and the `colors` output follow its grips exactly.
- **A legend that follows the model.** `placement` 2 with a `point` on the part — it stays
  the same size on screen while you zoom.
