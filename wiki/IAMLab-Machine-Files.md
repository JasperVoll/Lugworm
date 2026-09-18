# IAMLab Machine Files

IAMLab Machine Files browses the file storage of the connected machine,
the Duet's SD card. Buttons on the component read a folder,
step through the entries, open a folder, download a file, rename or
move a file, and delete a file.

Panel: `7 Print`. Nickname: `Files`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Machine Files with the directory listing in a panel. A short screen recording saved as GIF can replace the image. -->
![IAMLab Machine Files on the canvas](images/comp-machine-files.png)

## Inputs

| Name | Type | Description |
|---|---|---|
| `host` | Text | Host string from the Machine Console host output |
| `dir` | Text | Directory to browse on the board |
| `savePath` | Text | Local file or folder for Download. A folder keeps the file name from the board |
| `moveTo` | Text | Move target: a new name, a directory ending in '/', or a full 0:/ path |

## Outputs

| Name | Type | Description |
|---|---|---|
| `files` | Text (list) | Directory listing with name and size. Folders end with / |
| `selected` | Text | Full board path of the selected entry |
| `size` | Number | Selected file size, bytes |
| `info` | Text | Diagnostics |

## Notes

- `host` accepts a Duet host string from
  [[IAMLab Machine Console]]; the component works the same way on both.
- `dir` is the folder on the machine, for example `0:/gcodes` on a Duet
.
- `savePath` is the local file or folder for a download.

Back to [[Components]].
