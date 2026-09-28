---
title: "Installation"
nav_order: 1
parent: "Getting started"
description: "Install the Rhino 8 Windows package and check that the plugin loads."
permalink: /docs/getting-started/installation/
---

# Installation

Lugworm requires **Rhino 8 on Windows**. The packaged `.yak` file includes the plugin and its dependencies.

{: .warning }
> **The public package is being prepared.** For early access, contact [jasper.vollmert@tuhh.de](mailto:jasper.vollmert@tuhh.de). Once published, the package will be available from [Releases](https://github.com/JasperVoll/Lugworm/releases). Use the instructions below when you have the package.

## Install with Yak (recommended)

1. Close Rhino and save `lugworm-<version>-rh8_0-*.yak` in its own folder, such as
   `C:\Temp\lugworm`.
2. Run PowerShell (`--source` points to that folder):

   ```powershell
   & "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\lugworm" lugworm
   ```

3. Restart Rhino 8, run `Grasshopper`, and look for the **Lugworm** ribbon tab:
   `1 Slice`, `2 Place`, `3 G-code`, `4 Robot`, `5 Analyze`, `6 Calibrate`, `7 Print`.


## Manual installation

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

## Reinstall or update

Close Rhino, then run:

```powershell
& "C:\Program Files\Rhino 8\System\Yak.exe" uninstall lugworm
& "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\lugworm" lugworm
```

Remove any manually installed copies from `Grasshopper\Libraries` to prevent
duplicate loading.

## Installation problems

| Symptom | Fix |
|---|---|
| No Lugworm tab appears | Usually blocked DLLs. Run the `Unblock-File` command above and restart Rhino. |
| Yak reports "package not found" | The path after `--source` must be the folder, not the `.yak` file. |
| The plugin does not load at all | This build targets Rhino 8. It does not load in Rhino 7. |
| Still nothing | In Grasshopper open File > Special Folders > Component Folders, open the report of unloaded or failed assemblies, and attach it to a [bug report](https://github.com/JasperVoll/Lugworm/issues). |

## Check the installation

Open Grasshopper and look for the Lugworm tab. Rhino's Package Manager lists the installed Lugworm version. Then follow [Make your first toolpath]({{ '/docs/getting-started/' | relative_url }}#1-make-your-first-toolpath).

## Before sharing a `.gh` definition

Duet Console's `password` input can be saved in the `.gh` document and read by anyone who receives it. Clear the stored password before sharing the definition.
