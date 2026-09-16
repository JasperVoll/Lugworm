# IAMLab Print Bed

IAMLab Print Bed describes the print bed once: how large the plate is,
where the plate is in the Rhino document, and where a part is placed by
default. The `bed` output is connected to [[IAMLab Place]],
[[IAMLab Robot Simulation]], and [[IAMLab Live View]], so that all of
them use the same bed.

Panel: `2 Place`. Nickname: `Bed`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Print Bed connected to Place and Robot Simulation; the viewport shows the plate outline and the print centre. A short screen recording saved as GIF can replace the image. -->
![IAMLab Print Bed on the canvas](images/comp-print-bed.png)

## Inputs

| Name | Type | Description |
|---|---|---|
| `width` | Number | Bed plate width along Rhino X, mm (the full axis for an ellipse) |
| `depth` | Number | Bed plate depth along Rhino Y, mm (the full axis for an ellipse) |
| `center` | Point | Bed plate center in Rhino coordinates. The plate, grid and printable area follow it |
| `printCenter` | Point | Default print center, where a part's bottom center lands. This is not the plate center, so keep them separate |
| `shape` | Integer | Plate shape: 0 = rectangle (default), 1 = ellipse. Equal width and depth make a circle |

## Outputs

| Name | Type | Description |
|---|---|---|
| `bed` | Text | Print Bed definition. Connect it into the bed input of Place, Robot Simulation, Live View and Calib Points |
| `width` | Number | Bed width, mm |
| `depth` | Number | Bed depth, mm |
| `center` | Point | Bed plate center |
| `printCenter` | Point | Default print center |
| `outline` | Curve | Bed plate outline |
| `info` | Text | Bed summary |

## Notes

- `center` is the middle of the plate. `printCenter` is where the
  bottom centre of a part lands, which is usually not the middle of the
  plate. Keep the two separate.
- Reach - the ring-shaped area a SCARA arm can cover - is no longer set
  here. It stays the robot profile's alone; see [[IAMLab Robot Definition]].

Back to [[Components]].
