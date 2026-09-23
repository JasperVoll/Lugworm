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

| Name | Type | Default | Description |
|---|---|---|---|
| `host` | Text |  | Host output from Duet Console or Duet Connect, connected before using the file buttons |
| `dir` | Text | 0:/gcodes | Board directory path to list on Refresh (empty = 0:/gcodes) |
| `savePath` | Text |  | Local file or folder required before Download, which overwrites an existing file of the same name |
| `moveTo` | Text |  | Target required before Move: a new filename, a folder ending in /, or a full 0:/ path |

## Outputs

| Name | Type | Description |
|---|---|---|
| `files` | Text (list) | Last refreshed board directory listing with filenames and sizes in bytes (folders end with /) |
| `selected` | Text | Full board path of the selected entry |
| `size` | Number | Selected file size, bytes |
| `info` | Text | Selected directory, file and last-action details |

Back to [Components]({{ '/docs/components/' | relative_url }}).
