# Installing Lugworm

You install the `.yak` package directly. Get the `.yak` from the plugin's download
page `send-Lugworm-<version>.zip`.

**Requirements:** Rhino 8 on Windows. Nothing else to install.

---

## Install (recommended)

1. Save `lugworm-<version>-rh8_0-*.yak` into a folder of its own, e.g. `C:\Temp\lugworm`.
2. Open **PowerShell** and run:

   ```powershell
   & "C:\Program Files\Rhino 8\System\Yak.exe" install --source "C:\Temp\lugworm" lugworm
   ```
3. Start (or restart) Rhino 8 and run the `Grasshopper` command.


---

## Alternative: install by hand (no terminal)

A `.yak` is just a renamed `.zip`. Make a copy, rename it to `.zip`, and open it. The
package holds two folders: **`net7.0\`** (Rhino 8 in its default .NET Core mode) and
**`net48\`** (Rhino 8 started with `/netfx`). From the folder that matches your Rhino
mode, copy **all** files into:

```
%APPDATA%\Grasshopper\Libraries\
```


**Unblock the files.** 

```powershell
Get-ChildItem "$env:APPDATA\Grasshopper\Libraries" -Recurse | Unblock-File
```

(Or right-click each file → **Properties** → tick **Unblock** → **OK**.)

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
to can read it.

That input carries the **Duet password**.

Before sharing or publishing a definition that talks to a machine, clear the
password input (or set the machine's password to the RepRapFirmware default
`reprap` and rely on network isolation instead). 

Maintainer contact: Jasper Vollmert — jasper.vollmert@tuhh.de (IDAC @ TUHH).
