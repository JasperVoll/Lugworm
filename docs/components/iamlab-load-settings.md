---
title: "IAMLab Load Settings"
nav_order: 10
parent: "Components"
description: "IAMLab Load Settings: component inputs and outputs."
permalink: /docs/components/iamlab-load-settings/
---

# IAMLab Load Settings

IAMLab Load Settings points the document at a folder of saved settings
files, one per component. Every IAMLab component can save its current
inputs with "Save settings…" on its right-click menu, which writes a
small JSON file next to the ones this component reads. From then on,
the "⇤ inputs" button on any IAMLab component builds its sliders,
toggles, panels and value lists from the matching file instead of the
built-in defaults, and falls back to the defaults where the folder has
nothing to say. This makes it easy to keep one settings folder per
material or per machine and switch between them.

Panel: `1 Slice`. Nickname: `Settings`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-load-settings.png" alt="IAMLab Load Settings on the canvas" %}

## Inputs

| Name | Type | Description |
|---|---|---|
| `folder` | Text | Folder with *.iamset.json files, one per component type |
| `watch` | Boolean | Read the folder again whenever its file names, sizes or timestamps change. False scans once per folder path. Turning it off while editing files by hand is not needed, it only avoids the directory listing on every solve |

## Outputs

| Name | Type | Description |
|---|---|---|
| `files` | Text (list) | One line per match: the component key and the file it takes its inputs from |
| `unmatched` | Text (list) | Files that were not used: unreadable ones with their error, duplicate claims on one component, and files naming a component this build does not have |
| `info` | Text | Diagnostics |

Back to [Components]({{ '/docs/components/' | relative_url }}).
