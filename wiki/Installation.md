# Installation

Requirements: Rhino 8 on Windows. Nothing else needs to be installed.

The plugin ships as a `.yak` package. Get it from
[Food4Rhino](https://www.food4rhino.com), from the repository's
[Releases](../../releases) page, or build it from source.

## Install with Yak (recommended)

1. Save `iamlab-slicer-<version>-rh8_0-*.yak` into a folder of its own,
   for example `C:\Temp\iamlab`.
2. Open PowerShell and run:

   ```powershell
   & "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\iamlab" iamlab-slicer
   ```

   The `--source` flag points Yak at your folder instead of the public
   package server.
3. Start or restart Rhino 8 and run the `Grasshopper` command.

To confirm the installation, look for an **IAMLab** tab on the
component ribbon. It has seven panels: `1 Slice`, `2 Place`,
`3 G-code`, `4 Robot`, `5 Analyze`, `6 Calibrate` and `7 Print`.

<!-- IMAGE TODO: screenshot of the Grasshopper ribbon with the IAMLab tab
     open and all seven panels visible. Crop to the ribbon. -->
![The IAMLab tab after installation](images/install-ribbon.png)

## Install by hand

A `.yak` file is a renamed zip archive. Copy it, rename the copy to
`.zip`, open it, and copy every file from the `net7.0-windows` folder
(`IAMLabSlicer.gha` and three `.dll` files) into:

```
%APPDATA%\Grasshopper\Libraries\
```

Then unblock the files. Windows marks downloaded files as blocked, and
Grasshopper skips blocked assemblies without any error message, so the
plugin would not appear at all:

```powershell
Get-ChildItem "$env:APPDATA\Grasshopper\Libraries" -Recurse | Unblock-File
```

Restart Rhino and Grasshopper.

## Updating

Close Rhino, uninstall the old version, then install the new file:

```powershell
& "C:\Program Files\Rhino 8\System\Yak.exe" uninstall iamlab-slicer
& "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\iamlab" iamlab-slicer
```

If you ever copied files into `Grasshopper\Libraries` by hand, remove
them as well. An old `.gha` next to a new package makes Grasshopper
load the plugin twice.

## Installation problems

| Symptom | Fix |
|---|---|
| No IAMLab tab appears | Usually blocked DLLs. Run the `Unblock-File` command above and restart Rhino. |
| Yak reports "package not found" | The path after `--source` must be the folder, not the `.yak` file. |
| The plugin does not load at all | This build targets Rhino 8. It does not load in Rhino 7. |
| Still nothing | In Grasshopper open File > Special Folders > Component Folders, open the report of unloaded or failed assemblies, and attach it to a [bug report](../../issues/new/choose). |

## Before sharing a `.gh` definition

The password input of the Duet Connect component can be internalized
into the Grasshopper document. When it is, the password is stored inside
the `.gh` file, where anyone who receives the file can read it. Clear
the password input before sharing a definition that connects to a
machine, or leave the machine on the firmware default `reprap` and rely
on network isolation.
