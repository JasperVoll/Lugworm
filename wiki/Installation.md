# Installation

Requires Rhino 8 on Windows. Get the `.yak` package from
[Food4Rhino](https://www.food4rhino.com) or [Releases](../../releases), or build
from source. See [[No warranty|Home#no-warranty]] before use.

## Install with Yak (recommended)

1. Save `iamlab-slicer-<version>-rh8_0-*.yak` in its own folder, such as
   `C:\Temp\iamlab`.
2. Run PowerShell (`--source` points to that folder):

   ```powershell
   & "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\iamlab" iamlab-slicer
   ```

3. Restart Rhino 8, run `Grasshopper`, and look for the **IAMLab** ribbon tab:
   `1 Slice`, `2 Place`, `3 G-code`, `4 Robot`, `5 Analyze`, `6 Calibrate`, `7 Print`.

<!-- IMAGE TODO: screenshot of the Grasshopper ribbon with the IAMLab tab
     open and all seven panels visible. Crop to the ribbon. -->
![The IAMLab tab after installation](images/install-ribbon.png)

## Install by hand

Rename a copy of the `.yak` archive to `.zip`. Extract every file from
`net7.0-windows` (`IAMLabSlicer.gha` and three `.dll` files) into:

```
%APPDATA%\Grasshopper\Libraries\
```

Unblock downloaded files so Grasshopper can load them:

```powershell
Get-ChildItem "$env:APPDATA\Grasshopper\Libraries" -Recurse | Unblock-File
```

Restart Rhino and Grasshopper.

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
| Still nothing | In Grasshopper open File > Special Folders > Component Folders, open the report of unloaded or failed assemblies, and attach it to a [bug report](../../issues/new/choose). |

## Before sharing a `.gh` definition

IAMLab Machine Console's `password` input can be stored in the `.gh` file,
readable by anyone receiving it. Clear it before sharing, or leave the machine
on the firmware default `reprap` and rely on network isolation.
