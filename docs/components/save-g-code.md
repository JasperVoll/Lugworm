---
title: "Save G-code"
nav_order: 21
parent: "Components"
description: "Save G-code: component inputs and outputs."
permalink: /docs/components/save-g-code/
redirect_from:
  - /docs/components/iamlab-save-g-code/
---

# Save G-code

Save G-code writes the G-code to a file whose name contains the
date and time, so that no file is overwritten by accident. The file is
written only when `write` becomes true. Connect a Button component,
not a Toggle, so that every click writes exactly one file.

Panel: `3 G-code`. Nickname: `Save`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-save-g-code" alt="Save G-code on the canvas" %}

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

Back to [Components]({{ '/docs/components/' | relative_url }}).
