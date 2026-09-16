# IAMLab Place

IAMLab Place moves the sliced layers to the print position on the bed
and decides where each layer starts and ends (the seam). The bottom
centre of the part lands on `printCenter`. The seam can be kept as it
is, aligned in one direction, rotated a little on every layer, placed
nearest to a point, guided along a curve, or kept above the seam of the
layer below.

Panel: `2 Place`. Nickname: `Place`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Place with seamMode = 1; the viewport shows the placed part with the seam points lined up in a column. A short screen recording saved as GIF can replace the image. -->
![IAMLab Place on the canvas](images/comp-place.png)

## Inputs

| Name | Type | Description |
|---|---|---|
| `contours` | Curve (tree) | Layer contour tree to place |
| `printCenter` | Point | Where this part's bottom center lands, world XYZ. A placement, not a bed setting. When connected it overrides the bed's default print center |
| `zOffset` | Number | Extra Z after dropping, mm |
| `move` | Vector | Extra translation always applied to the part, mm |
| `seamMode` | Integer | 0 = keep, 1 = align (default), 2 = stagger, 3 = nearest, 4 = guide, 5 = track |

### Optional group: Print Bed definition input

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `bed` | Text | Print Bed definition. Supplies the default print center |

### Optional group: Seam direction / target inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `seamAngle` | Number | Direction for mode 1, or the step per layer for mode 2, deg |
| `seamPoint` | Point | Target point for mode 3 (nearest). Optional first layer target for mode 5 (track) |
| `seamCurve` | Curve | Guide curve for mode 4. Each layer's seam lands at the closest point of this world space curve, so draw it where the placed part stands, for example a helix or a vertical line up the back |

## Outputs

| Name | Type | Description |
|---|---|---|
| `moved` | Curve (tree) | Placed and reseamed contour tree |
| `seams` | Point (tree) | Seam start point per contour |
| `info` | Text | Diagnostics |

## Notes

- `printCenter` overrides the print centre of a connected
  [[IAMLab Print Bed]]. Without either, the built-in default is the
  usual position on the lab bed.
- `seamMode`: `0` keep, `1` align (default; `seamAngle` sets the
  direction), `2` stagger (`seamAngle` is the step per layer), `3`
  nearest (`seamPoint`), `4` guide (`seamCurve`, drawn in world
  coordinates where the placed part stands), `5` track (each seam above
  the seam of the layer below).
- Whether the placed part can be reached by the robot is checked by
  [[IAMLab Robot Simulation]], not here.

Back to [[Components]].
