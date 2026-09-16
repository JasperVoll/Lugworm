# IAMLab Save G-code

IAMLab Save G-code writes the G-code to a file whose name contains the
date and time, so that no file is overwritten by accident. The file is
written only when `write` becomes true. Connect a Button component,
not a Toggle, so that every click writes exactly one file.

Panel: `3 G-code`. Nickname: `Save`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Save G-code with a Button connected to write and a panel showing the written path. A short screen recording saved as GIF can replace the image. -->
![IAMLab Save G-code on the canvas](images/comp-save-g-code.png)

## Inputs

| Name | Type | Description |
|---|---|---|
| `gcode` | Text (list) | G-code as one string or a list of lines |
| `folder` | Text | Target folder path |
| `prefix` | Text | Filename prefix (default 'print') |
| `write` | Boolean | Write the file when true. Connect a Button |

### Optional group: Custom date/time + format inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `dateTime` | Date/time | Override the date and time (default now) |
| `format` | Text | DateTime format (default yyyy-MM-dd_HH-mm) |

## Outputs

| Name | Type | Description |
|---|---|---|
| `path` | Text | Full target file path |
| `written` | Boolean | True if the file was written this solve |
| `info` | Text | Diagnostics |

## Notes

- The file is written in the background. The component first reports
  "writing", and `written` becomes true on the following solve.
- With the Override group, the timestamp and the name format can be set
  by hand.

Back to [[Components]].
