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

{% include figure.html src="components/iamlab-save-g-code" %}

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `gcode` | Text (list) |  | G-code as one string or a list of lines |
| `folder` | Text |  | Local folder required with nonempty gcode before triggering write |
| `prefix` | Text | print | Filename prefix (default 'print') |
| `write` | Boolean |  | Save once when false changes to true, with gcode and folder set (wire a Button) |

### Optional group: Custom date/time + format inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `dateTime` | Date/time | Override the date and time (default now) |
| `format` | Text | Timestamp format text (default yyyy-MM-dd_HH-mm), for example yyyyMMdd_HHmmss |

## Outputs

| Name | Type | Description |
|---|---|---|
| `path` | Text | Full target path, retaining the last successful save after write returns to false |
| `written` | Boolean | True if the file was written this solve |
| `info` | Text | Save result or the reason writing was refused |

Back to [Components]({{ '/docs/components/' | relative_url }}).
