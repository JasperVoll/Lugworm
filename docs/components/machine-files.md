---
title: "Machine Files"
nav_order: 12
parent: "Components"
description: "Machine Files: component inputs and outputs."
permalink: /docs/components/machine-files/
redirect_from:
  - /docs/components/iamlab-machine-files/
---

# Machine Files

Machine Files browses the file storage of the connected machine,
the Duet's SD card. Buttons on the component read a folder,
step through the entries, open a folder, download a file, rename or
move a file, and delete a file.

Panel: `7 Print`. Nickname: `Files`. Compute button: no (runs on every change).

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

Back to [Components]({{ '/docs/components/' | relative_url }}).
