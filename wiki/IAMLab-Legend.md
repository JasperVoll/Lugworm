# IAMLab Legend

IAMLab Legend draws a legend for any data range: a continuous ramp,
thresholded classes, named groups, or a size scale. It exists because
Grasshopper has no legend of its own. Wire the `legend` output of
[[IAMLab Overhang]] or [[IAMLab Interlayer Time]] and the legend always
matches the colours those components painted; wire your own `values`,
`min` and `max` instead to describe any other scale. Every explicit
input on the component overrides the matching field of the wire, so
one Legend can be nudged - a different title, a bigger font - without
detaching it from the source.

Panel: `5 Analyze`. Nickname: `Legend`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Legend showing the Interlayer Time wire as a floating billboard next to the printed part. A short screen recording saved as GIF can replace the image. -->
![IAMLab Legend on the canvas](images/comp-legend.png)

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `legend` | Text |  | Legend spec wire, the legend output of IAMLab Overhang or Interlayer Time. Every explicit input below overrides its field |
| `values` | Number (list) |  | Data to color. Also sets the domain when min and max are not connected. The colors output gives one color per value |
| `min` | Number | 0 | Domain start. Not connected takes the wire's, else the values' minimum, else 0 |
| `max` | Number | 1 | Domain end. Not connected takes the wire's, else the values' maximum, else 1 |
| `type` | Integer | 0 | 0 = continuous ramp, 1 = classes (threshold bands), 2 = groups (named categories), 3 = size (circle size scale) |
| `scheme` | Text |  | Color scheme: any matplotlib colormap (viridis, plasma, magma, Blues, RdBu, tab10 …), R grDevices palette (Okabe-Ito, rainbow …) or ggsci palette (npg, nejm, d3-category10 …), traffic, or custom, which uses colors or gradient. The value list shows them all by family |
| `title` | Text |  | Title above the legend |
| `show` | Boolean | true | Draw the legend in the viewport (outputs solve either way) |

### Optional group: Style inputs (custom colours, breaks, labels, unit, reverse, n/a swatch)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `colors` | Colour (list) | Custom colors: ramp stops for continuous and size, or one per class or group |
| `gradient` | Colour (list) | Wire the Gradient component here. Its color stops and their positions are read directly. Any other color list gives evenly spaced stops. Wins over colors |
| `breaks` | Number (list) | Classes: ascending thresholds, k thresholds give k+1 classes. Size: the sample values shown |
| `labels` | Text (list) | Class or group names. Classes without a name show their range |
| `unit` | Text | Unit appended to every number |
| `reverse` | Boolean | Flip the scale direction |
| `naLabel` | Text | Label of the grey 'unavailable' swatch (empty = no swatch unless the wire carries one) |

### Optional group: Text inputs (font, size, colour, decimals, ticks, frame)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `font` | Text | Segoe UI | Font face: Segoe UI, Arial, Calibri, Consolas, Cambria, Verdana, Times New Roman (any installed face works) |
| `textSize` | Number | 11 | Text height: px for viewport and floating, model units for a plane |
| `textColor` | Colour |  | Text and tick color |
| `decimals` | Integer | -1 | Decimal places (-1 = from the tick step) |
| `ticks` | Integer | 0 | Tick count on a continuous scale (0 = nice ticks) |
| `frame` | Boolean |  | Draw a panel behind the legend |

### Optional group: Placement inputs (viewport / plane / floating, anchor, orientation, geometry)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `placement` | Integer | 0 | 0 = viewport overlay, 1 = fixed to plane in model units, 2 = floating billboard at point, facing the camera at constant screen size |
| `anchor` | Integer | 2 | Viewport: 0 = top left, 1 = top right, 2 = bottom left, 3 = bottom right, 4 = top, 5 = bottom, 6 = left, 7 = right. Plane and floating: the corner that sits on the origin or point |
| `orientation` | Integer | 0 | 0 = horizontal, 1 = vertical |
| `plane` | Plane |  | Plane for placement 1 (origin = the anchored corner) |
| `point` | Point |  | Anchor point for placement 2 |
| `length` | Number | 220 | Bar length: px for viewport and floating, model units for a plane |
| `thickness` | Number | 14 | Bar thickness, swatch size or largest size circle radius, same unit as length |
| `margin` | Number | 18 | Viewport: distance from the viewport edge, px |

## Outputs

| Name | Type | Description |
|---|---|---|
| `colors` | Colour (list) | One color per values entry from this scale. Paints any geometry consistently with the legend |
| `mesh` | Mesh | The legend as a vertex colored mesh in model space for plane and floating placements. Bakeable |
| `labels` | Text (list) | Legend texts, one per frames plane, for plane and floating placements. Feed a Text Tag 3D or bake |
| `frames` | Plane (list) | Text planes matching labels |
| `spec` | Text | The resolved scale as a legend spec wire. Forward it to another Legend |
| `info` | Text | Diagnostics |

## Notes

- `type`: `0` continuous ramp, `1` classes (thresholded bands), `2`
  groups (named categories), `3` size (a circle-size scale).
- `scheme` picks a named palette from three families - matplotlib
  colormaps (`mpl`), R `grDevices` palettes (`R`) and the ggsci package
  (`ggsci`) - or `traffic`, or `custom`, which reads the `colors` input
  instead. Names are case-insensitive; the old IAMLab ramp names
  (`blues`, `greens`, `greys`, `pinks`, `ambers`) still resolve.
  Categorical palettes hand their colours to classes and groups one by
  one; ramps are sampled evenly.
- `gradient` reads Grasshopper's Gradient component directly - colour
  stops and their positions - so an uneven gradient is reproduced
  exactly; the positions travel on the wire as `stops=`.
- `mesh` and `frames` are preview-hidden outputs: the component draws
  the legend itself, so their automatic preview would only add wire
  edges, plane icons and a second static copy in the floating placement.
- `placement`: `0` a fixed overlay anchored to a corner or edge of the
  viewport, `1` fixed to a `plane` in model space, `2` floating at a
  `point` as a camera-facing billboard whose screen size stays constant
  while you orbit or zoom.
- `orientation` chooses horizontal or vertical for any placement.
- The component doubles as a colouriser: the `colors` output gives one
  colour per `values` entry from the same scale, so any geometry can be
  painted consistently with the legend.
- `mesh`, `labels` and `frames` give a bakeable vertex-coloured legend
  and its text planes for the plane and floating placements.
- `spec` re-emits the resolved scale as an `iamlab-legend:v1` wire, so
  one Legend can feed the exact same scale to another.

Back to [[Components]].
