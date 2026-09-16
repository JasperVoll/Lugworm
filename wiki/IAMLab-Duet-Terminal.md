# IAMLab Duet Terminal

IAMLab Duet Terminal sends one raw G-code line to the board and shows
the reply. The line is sent without any of the checks that the other
components apply, so the component is meant for experts. Commands that
move the machine, change the configuration, or start a job are only
sent when `expert` is true and the Send button is double-clicked.

Panel: `7 Print`. Nickname: `Term`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Duet Terminal with M115 sent and the firmware reply in a panel. A short screen recording saved as GIF can replace the image. -->
![IAMLab Duet Terminal on the canvas](images/comp-duet-terminal.png)

## Inputs

| Name | Type | Description |
|---|---|---|
| `host` | Text | Host string from the Duet Connect component |
| `line` | Text | One G-code line (e.g. M115) |
| `expert` | Boolean | Unlock motion/config/job commands (double-click Send to confirm) |

## Outputs

| Name | Type | Description |
|---|---|---|
| `reply` | Text | Firmware reply to the last sent line |
| `log` | Text (list) | Session log (newest last) |
| `info` | Text | Diagnostics |

## Notes

- A safe first command is `M115`, which reports the firmware version.

Back to [[Components]].
