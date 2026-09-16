# IAMLab Overhang

IAMLab Overhang checks how far each part of a layer hangs out over the
layer below. Colours come from the magma ramp: in the default class mode
segments within the warning angle are deep purple, segments between the
warning and the failure angle are red-orange, and segments beyond the
failure angle are light orange; `colorMode` 1 instead colours every
segment on a continuous magma ramp from 0° to the failure angle. The
default angles of 25° and 45° are placeholders and should be measured
for your material with a test print of a stepped cone. The `global`
toggle chooses what the angle is measured from: off, the layer's own
normal (local, the bead-on-bead stagger); on, world Z (global, the lean
against gravity). The two agree on planar layers and differ on conical
ones. The component has a compute button.

Panel: `5 Analyze`. Nickname: `Overhang`. Compute button: yes (runs only when the button is clicked).

<!-- IMAGE TODO: A twisted vase after Overhang with the configured magma scale and legend in the viewport. A short screen recording saved as GIF can replace the image. -->
![IAMLab Overhang on the canvas](images/comp-overhang.png)

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `contours` | Curve (tree) |  | Sliced contour tree from Slice or Place, one branch per layer, or per object and layer |
| `warnDeg` | Number | 25 | Warning threshold, deg from the build direction. The global input picks the local layer normal or world Z. Calibrate per material |
| `failDeg` | Number | 45 | Critical threshold, deg. Calibrate per material |
| `tol` | Number | 0.5 | Curve linearization tolerance, mm |
| `colorMode` | Integer | 0 | 0 = classes (safe, warning and critical in magma hues), 1 = continuous magma ramp from 0° to failDeg, saturating above |
| `global` | Boolean | false | Overhang reference. Off = local, the angle from the layer's own normal, the bead on bead stagger within the stack, which limits printability on non-planar layers. On = global, the angle from world Z, the lean against gravity. Identical on planar layers. Both angles are always output, this picks which one the thresholds, colors and legend use |

### Optional group: CSV export input

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `csvPath` | Text | Optional .csv file path for the summary per layer, written on compute |

## Outputs

| Name | Type | Description |
|---|---|---|
| `angles` | Number (tree) | Local overhang angle per segment relative to the layer, deg, one branch per contour. Always the local reading, whatever global says |
| `anglesWorld` | Number (tree) | Global overhang angle per segment against world Z, deg. Always the global reading |
| `summary` | Text (list) | Summary per layer as CSV lines: max overhang and the percentage above each threshold. The percentages follow global |
| `info` | Text | Diagnostics |
| `legend` | Text | Legend spec for IAMLab Legend: the three overhang classes, or the continuous magma ramp, at the thresholds and reference this analysis used |
| `colors` | Colour (tree) | Color per segment in the active color mode and reference, cut like angles, for a custom preview |

## Notes

- `angles` measures the overhang relative to the layer below (local),
  which is what matters for non-planar layers. `anglesWorld` measures
  the angle against the vertical (global) instead. Both are always
  output; `global` decides which one the thresholds classify, and with
  it the viewport colours, `colors`, the class percentages in `summary`
  and the legend (its title reads "overhang (global)" when on). With
  `global` on, `summary` starts with a comment line that says so.
- The Export group writes a per-layer summary as a CSV file.
- `legend` is an `iamlab-legend:v1` wire carrying the same colours drawn
  in the viewport - the three classes, or in `colorMode` 1 the magma ramp
  with the warning and critical thresholds marked - connect it to
  [[IAMLab Legend]] so a legend never disagrees with the painted colours.
- `colors` gives one colour per segment for a Custom Preview of your own;
  it follows `colorMode` too.

Back to [[Components]].
