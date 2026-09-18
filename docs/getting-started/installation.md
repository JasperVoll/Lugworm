---
title: "Installation"
nav_order: 1
parent: "Getting started"
description: "Installation: instructions and practical checks for IAMLab Slicer."
permalink: /docs/getting-started/installation/
---

# Installation

Requires Rhino 8 on Windows. Get the `.yak` package from
[Food4Rhino](https://www.food4rhino.com) or [Releases](https://github.com/JasperVoll/iamlab-slicer-documentation/releases). See [No warranty]({{ '/' | relative_url }}#no-warranty) before use.

{: .warning }
> **The package is not published yet.** The first public release is being
> prepared, so the Releases page and the Food4Rhino entry are still empty. The
> instructions below are final and will work as written once the package is up.
> For earlier access, write to
> [jasper.vollmert@tuhh.de](mailto:jasper.vollmert@tuhh.de).

## Install with Yak (recommended)

1. Save `iamlab-slicer-<version>-rh8_0-*.yak` in its own folder, such as
   `C:\Temp\iamlab`.
2. Run PowerShell (`--source` points to that folder):

   ```powershell
   & "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\iamlab" iamlab-slicer
   ```

3. Restart Rhino 8, run `Grasshopper`, and look for the **IAMLab** ribbon tab:
   `1 Slice`, `2 Place`, `3 G-code`, `4 Robot`, `5 Analyze`, `6 Calibrate`, `7 Print`.

{% include figure.html src="getting-started/iamlab-tab.png" alt="The IAMLab tab after installation" %}

## Install by hand

Rename a copy of the package from `.yak` to `.zip`. Extract all files
from the payload folder matching your Rhino runtime into:

```text
%APPDATA%\Grasshopper\Libraries\
```

Keep its subfolders intact and do not mix runtime payloads. Unblock downloaded
files before restarting Rhino and Grasshopper:

```powershell
Get-ChildItem "$env:APPDATA\Grasshopper\Libraries" -Recurse | Unblock-File
```

## Reinstall

Close Rhino, then run:

```powershell
& "C:\Program Files\Rhino 8\System\Yak.exe" uninstall iamlab-slicer
& "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\iamlab" iamlab-slicer
```

Remove any manually installed copies from `Grasshopper\Libraries` to prevent
duplicate loading.

## Installation problems

| Symptom | Fix |
|---|---|
| No IAMLab tab appears | Usually blocked DLLs. Run the `Unblock-File` command above and restart Rhino. |
| Yak reports "package not found" | The path after `--source` must be the folder, not the `.yak` file. |
| The plugin does not load at all | This build targets Rhino 8. It does not load in Rhino 7. |
| Still nothing | In Grasshopper open File > Special Folders > Component Folders, open the report of unloaded or failed assemblies, and attach it to a [bug report](https://github.com/JasperVoll/iamlab-slicer-documentation/issues). |

## Before sharing a `.gh` definition

IAMLab Machine Console's `password` input can be stored in the `.gh` file,
readable by anyone receiving it. Clear it before sharing, or leave the machine
on the firmware default `reprap` and rely on network isolation.
