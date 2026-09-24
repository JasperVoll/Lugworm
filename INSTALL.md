# Installing Lugworm

A Grasshopper plug-in for clay / LDM 3D printing — it slices geometry,
generates G-code (Marlin / Duet RRF / Klipper), and simulates the SCARA print arm.

You install the `.yak` package directly. Get the `.yak` from the plugin's download
page or from the maintainer (it ships inside `send-Lugworm-<version>.zip`, next
to the raw build output), or build it yourself from source — see
[RUNNING_IN_VS.md](docs/dev/RUNNING_IN_VS.md) (§7, *Yak package*).

> Want to edit and debug the code, not just run it? Follow
> [RUNNING_IN_VS.md](docs/dev/RUNNING_IN_VS.md) instead — this guide is the just-install-it path.

**Requirements:** Rhino 8 on Windows. Nothing else to install.

> Coming from **IAMLab Slicer** (the name before the 0.1.0-beta baseline)? Uninstall that package first; see
> [UPGRADE.md](UPGRADE.md). Your `.gh` files and settings files keep working.

---

## Install (recommended)

1. Save `lugworm-<version>-rh8_0-*.yak` into a folder of its own, e.g. `C:\Temp\lugworm`.
2. Open **PowerShell** and run:

   ```powershell
   & "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\lugworm" lugworm
   ```

   The `--source` flag points Yak at your folder instead of the public server, so it
   installs the file you have rather than looking online.
3. Start (or restart) Rhino 8 and run the `Grasshopper` command.

That single command is all it takes — the package already bundles the plug-in *and* every
library it depends on, and Yak places them where Grasshopper expects them.

**Check it worked:** in Grasshopper, look for a **Lugworm** tab on the component ribbon with
seven groups — **1 Slice - 2 Place - 3 G-code - 4 Robot - 5 Analyze - 6 Calibrate - 7 Print**.
If they're there, you're set.

---

## Updating to a new version

Follow **[UPGRADE.md](UPGRADE.md)** — it covers removing the old version cleanly (also
hand-copied leftovers), what changes in existing .gh definitions, and upgrade-specific
troubleshooting. Short version: close Rhino, `yak uninstall`, `yak install` the new file.

---

## Alternative: install by hand (no terminal)

A `.yak` is just a renamed `.zip`. Make a copy, rename it to `.zip`, and open it. The
package holds two payloads: **`net7.0\`** (Rhino 8 in its default .NET Core mode) and
**`net48\`** (Rhino 8 started with `/netfx`). From the folder that matches your Rhino
mode, copy **all** files into:

```
%APPDATA%\Grasshopper\Libraries\
```

The payload is more than one `.gha` and one `.dll`: `Lugworm.gha`,
`Lugworm.Core.dll`, `MathNet.Numerics.dll`, `Clipper2Lib.dll`, the SkiaSharp and
OpenTK libraries that draw the Duet Console window (`SkiaSharp.dll`,
`SkiaSharp.Views.Desktop.Common.dll`, `SkiaSharp.Views.WindowsForms.dll`, `OpenTK.dll`,
`OpenTK.GLControl.dll`) and the native `libSkiaSharp.dll` - which sits at the folder root in
`net7.0\` and under `x64\` in `net48\`. Copy the folder as it is; do not flatten or move
the native, and do not mix the two payloads.

Then do the step everyone forgets:

> ⚠️ **Unblock the files first.** Windows tags anything that arrived by email, Teams, or
> download as "blocked," and Grasshopper silently skips blocked assemblies — the plug-in
> simply won't appear, with no error message. Run this once before starting Rhino:
>
> ```powershell
> Get-ChildItem "$env:APPDATA\Grasshopper\Libraries" -Recurse | Unblock-File
> ```
>
> (Or right-click each file → **Properties** → tick **Unblock** → **OK**.)

Restart Rhino and Grasshopper.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| No **Lugworm** tab appears | Almost always blocked DLLs — run the `Unblock-File` command above and restart Rhino. |
| Yak says "package not found" | Check the path after `--source` is the **folder**, not the `.yak` file, and that the folder contains the file. |
| Still nothing | Grasshopper → **File ▸ Special Folders ▸ Component Folders**, open the *unloaded / failed assemblies* report, and send it to the maintainer with a screenshot. |
| Won't load at all | This build targets **Rhino 8**; it will not load in Rhino 7. |

---

## Before you share a `.gh` definition

The **Duet Console** component's `password` input (and the hidden Duet Connect
component's, in older definitions) can be *internalized* into the Grasshopper
document. When it is, the secret is stored **inside the `.gh` file** as plain
text, and it is sent as plain HTTP on the local network - anyone you send that file
to can read it. Grasshopper offers no "secret" input type, so this is a property of
the format, not a bug.

That input carries the **Duet password**.

Before sharing or publishing a definition that talks to a machine, clear the
password input (or set the machine's password to the RepRapFirmware default
`reprap` and rely on network isolation instead). Connection and session state are deliberately
*not* persisted — only the password is.

Maintainer contact: Jasper Vollmert — jasper.vollmert@tuhh.de (IDAC @ TUHH).
