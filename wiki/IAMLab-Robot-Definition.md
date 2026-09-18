# IAMLab Robot Definition

IAMLab Robot Definition selects the machine. The `robot` output is
a short text that other components read from their `robot` input, so
that the machine is chosen once for the whole definition. Available
profiles: `none` (no machine, toolpath only), `generic-cartesian` (an
ordinary printer), `generic-duet-cartesian` (an ordinary printer with a
Duet board), `potterbot-scara-v4` (the SCARA arm of the lab),
`generic-klipper` and `generic-marlin` (Cartesian machines whose default
G-code dialect is Klipper or Marlin; neither is controlled over the
network - generate the file and transfer it yourself).

Panel: `4 Robot`. Nickname: `Robot Def`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Robot Definition with the profile dropdown open, connected to the G-code Generator and Robot Simulation. A short screen recording saved as GIF can replace the image. -->
![IAMLab Robot Definition on the canvas](images/comp-robot-definition.png)

## Inputs

| Name | Type | Description |
|---|---|---|
| `preset` | Text | Robot or machine preset. None keeps the toolpath and G-code workflows free of a model |

## Outputs

| Name | Type | Description |
|---|---|---|
| `robot` | Text | Robot definition. Connect it into the robot input of G-code Generator, Robot Simulation, Print Stats, Machine Console and Live View |
| `mechanism` | Text | Machine type |
| `program` | Text | Recommended program family |
| `control` | Text | Available direct control transport |
| `info` | Text | Selected profile summary |
| `process` | Text | Deposition process |
| `flavor` | Text | G-code dialect the generator uses while its firmware input is not connected |

## Notes

- The profile can also be typed into a text panel: `"scara"` or
  `"none"` are accepted.
- Without a connected definition, the G-code Generator and the Machine Console assume the SCARA
  arm of the lab. See [[Robots and Machine Control]].
- `process` reports whether the profile prints paste (ram or pump) or
  `Paste (LDM)`; `flavor` reports which G-code
  dialect the G-code Generator uses while its own `firmware` input is
  left unwired.

Back to [[Components]].
