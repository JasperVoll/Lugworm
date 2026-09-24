# Running Lugworm in Visual Studio

How to build, load, and debug the plugin in Rhino 8 from Visual Studio.

## Which repository Visual Studio uses

Open the solution from the outer repository root. That working tree is the
private development repository and its `origin` is the university GitLab
instance. Visual Studio's Git Changes window should therefore show the
`Lugworm` GitLab repository.

`docs/github/` is an independent, ignored Git repository used only for curated
public documentation and source releases. It is not part of the solution. Do
not add it to the solution or stage it from the outer repository. Public
release commits should be prepared separately with explicit `git -C` commands,
not through the solution's normal Git Changes workflow.

## Prerequisites

- **Rhino 8** (Windows) installed at `C:\Program Files\Rhino 8\`.
- **Visual Studio 2022** with the *.NET desktop development* workload.
- **.NET SDK** (the repo builds with SDK 10; `net7.0` targets restore their reference
  assemblies automatically, `net48` via the `Microsoft.NETFramework.ReferenceAssemblies`
  NuGet — no separate targeting pack needed).

## 1. Open the solution

Open `Lugworm.sln` (repo root). Its main and support projects are:

| Project | What it is |
|---|---|
| `Lugworm.Core` | Pure C# engine (no Rhino). The slicing/G-code/robot math. |
| `Lugworm` | The Grasshopper plugin (`.gha`). The components + UI. |
| `Lugworm.Core.Tests` | xUnit tests for the engine. |
| `Lugworm.FrameworkSmoke` | Cross-framework build/load smoke checks. |
| `Clipper2Lib` | Vendored Boost-licensed polygon engine. |

## 2. Build

Set **`Lugworm`** as the startup project (right-click → *Set as Startup Project*),
then **Build ▸ Build Solution** (Ctrl+Shift+B).

The `.gha` is written to:

```
<repo>\Lugworm\bin\Debug\net7.0-windows\Lugworm.gha
```

(Output goes to a local `bin\` folder, not `C:\Program Files\Rhino 8\Plug-ins`, so no
admin rights are needed.)

## 3. Debug (F5) — auto-loads the plugin

The project ships ready-to-debug launch profiles (from the **Rhino Visual Studio
extension** / McNeel template) in `Properties\launchSettings.json`:

- **Rhino 8 - netcore** — launches Rhino in .NET 7 mode (matches the `net7.0-windows`
  build). Use this one normally.
- **Rhino 8 - netfx** — launches Rhino in .NET Framework mode (debugs the `net48` build).

Each profile starts `Rhino.exe`, auto-runs `_Grasshopper`, and sets `RHINO_PACKAGE_DIRS`
to the build-output folder — so **Rhino loads your freshly built `.gha` automatically.
No manual search-path setup is needed.**

Steps:

1. Set **`Lugworm`** as the startup project.
2. Pick **Rhino 8 - netcore** in the debug-target dropdown (next to the green ▶ button).
3. Press **F5**. Rhino opens, Grasshopper opens, the plugin is loaded.
4. Find the components under the **Lugworm** tab: **1 Slice - 2 Place - 3 G-code - 4 Robot - 5 Analyze - 6 Calibrate - 7 Print**.
5. Set breakpoints in `SolveInstance` (component `.cs`) or anywhere in
   `Lugworm.Core` — both hit (debug symbols are built).

> After changing C# code: **stop debugging, rebuild, F5 again.** Rhino holds the loaded
> `.gha` while running, so it can't be overwritten in place.

## 4. Manual load (only if you start Rhino yourself, not via F5)

Point Grasshopper at the build output once: *Grasshopper ▸ File ▸ Special Folders ▸
Component Folders* (Developer Settings) → add
`<repo>\Lugworm\bin\Debug\net7.0-windows\` → restart Grasshopper.
Or copy the `.gha` + its DLLs (`Lugworm.Core.dll`, `Clipper2Lib.dll`,
`MathNet.Numerics.dll`) into `%APPDATA%\Grasshopper\Libraries\`.

## 5. What to check first (live behaviour)

The engine is unit-tested; the Grasshopper UI glue is what to verify by hand:

- **Compute button** (Slice, G-code): in Manual mode, changing inputs should NOT
  re-solve — the button shows `Compute *` (amber). Click it to run; it shows the
  runtime. Right-click ▸ *Auto-compute* switches to live mode.
- **Feature toggles**: right-click a component → toggle e.g. *Arcs*, *Skirt*,
  *Adaptive*, *Seam* → the matching inputs appear/disappear on the canvas. Existing
  wires to surviving inputs should be preserved.
- **Value lists**: right-click → *… create value list* → a named dropdown drops next to
  the input (Mode, Infill, Firmware, Seam, Elbow).
- **Robot**: feed the G-code component's `gcode` output into Robot, drag the `sim` slider
  → the arm geometry redraws along the path; `problems`/`messages` flag reach /
  singularity / speed / firmware issues.

## 6. Run the engine tests

From the repo root:

```
dotnet test Lugworm.Core.Tests/Lugworm.Core.Tests.csproj
```

or in VS: **Test ▸ Run All Tests**. Expect all tests to pass.

## 7. Commit and push private development work

Use Visual Studio's Git Changes window normally for implementation work:

1. Confirm the selected repository is the outer `Lugworm` checkout.
2. Review and stage only the intended source, tests, and private documentation.
3. Commit to the private development branch.
4. Push to `origin`; in this working tree `origin` means GitLab.

Do not use **Stage All** without reviewing the untracked `docs/` files. The
nested `docs/github/` repository is ignored, but other private documentation
may still be untracked and should be committed only deliberately.

## 8. Optional bits

- **ArcWelder**: the G-code component's *via ArcWelder* toggle needs
  `ArcWelderConsole.exe`. Build it once from the official repository
  (https://github.com/FormerLurker/ArcWelderLib, CMake / VS; AGPL-3.0 — kept
  external, never bundled) and put its path in the component's `Exe` input.
  Without it, the built-in arc fitter is used.
- **Yak package**: with Rhino's `Yak.exe` present, the build target produces a
  `.yak` in the output folder — a **development artifact** (contains PDBs and
  build strays). Never distribute it as-is; follow
  [RELEASING.md](RELEASING.md) for release packaging.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Components don't appear in GH | If using F5: confirm you launched the **Rhino 8 - netcore** profile (it sets `RHINO_PACKAGE_DIRS`). If starting Rhino manually: add the search path (§4). Check Rhino's *Grasshopper ▸ unloaded/failed assemblies* report. A ribbon that shows the previous build's components means the previous assembly is loaded: a Rhino left open from an earlier F5 keeps its `.gha`, so close every Rhino instance, rebuild, F5 again (verified 2026-09-03: a fresh `bin\Debug\net7.0-windows\` build registers all 28 Lugworm objects including Duet Print Console). |
| Build error: net48 reference assemblies | Restore NuGet (the `Microsoft.NETFramework.ReferenceAssemblies` package handles it); run `dotnet restore`. |
| Numbers in G-code use commas | Should not happen — output is forced to InvariantCulture. If you see it, report it. |
| `.gha` is locked / can't rebuild | Close Rhino first; Rhino holds the loaded `.gha`. |
| Breakpoints not hit | Ensure Debug config, and that GH loaded the build from your `bin\Debug\net7.0-windows\` (not an old copy in `%APPDATA%`). |
