---
title: "IAMLab Version"
nav_order: 25
parent: "Components"
description: "IAMLab Version: component inputs and outputs."
permalink: /docs/components/iamlab-version/
---

# IAMLab Version

IAMLab Version shows which version of the plugin is installed, when the
build was made, and which .NET runtime Rhino loaded. The summary line is
the first thing to include in a bug report. The component can also
check whether a newer release is available, but only when `check` is
set to true; by default no network request is made.

Panel: `1 Slice`. Nickname: `Version`. Compute button: no (runs on every change).

{% include figure.html src="components/iamlab-version.png" alt="IAMLab Version on the canvas" %}

## Inputs

| Name | Type | Description |
|---|---|---|
| `check` | Boolean | Query the published release source once per Rhino session. Nothing is contacted while false |

## Outputs

| Name | Type | Description |
|---|---|---|
| `version` | Text | Installed plugin version (from the executing assembly) |
| `build` | Text | Build date (UTC, stamped at compile time) |
| `runtime` | Text | Compiled target framework and the runtime actually loaded |
| `summary` | Text | One line summary, with the update status appended when checked |

Back to [Components]({{ '/docs/components/' | relative_url }}).
