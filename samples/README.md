# Developer samples

These small Grasshopper components show external plug-in authors how to reuse Lugworm's
canvas usability features. They are thin wrappers around public and protected members of
`Lugworm.Ui.IamComponentBase`. They do not copy Lugworm's implementation. Each component
lives in its own subcategory under **Lugworm Samples** and has a separate GUID.

1. [CanvasButtons](Lugworm.Samples/01-CanvasButtons/README.md): stateful action capsules and a dropdown.
2. [FeatureGroups](Lugworm.Samples/02-FeatureGroups/README.md): optional input groups, a dependency cascade and output categories.
3. [InputWiring](Lugworm.Samples/03-InputWiring/README.md): one-click sliders, toggles, panels and named value lists.
4. [ManualCompute](Lugworm.Samples/04-ManualCompute/README.md): manual computation, timing and cached output.
5. [SettingsFiles](Lugworm.Samples/05-SettingsFiles/README.md): per-component settings files and capture limits.
6. [CanvasStripAndPlot](Lugworm.Samples/06-CanvasStripAndPlot/README.md): a draggable bar and a themed bitmap plot.

## Dependency model

Your plug-in references an installed `Lugworm.gha`, and Lugworm remains a runtime
dependency. Install the exact Lugworm version your plug-in was built against. In your own
project, use an assembly reference instead of this repository's project reference:

```xml
<ItemGroup>
  <Reference Include="Lugworm">
    <HintPath>$(LugwormInstall)\Lugworm.gha</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

Set `LugwormInstall` to the installed framework folder, for example
`%APPDATA%\McNeel\Rhinoceros\packages\8.0\lugworm\<version>\net7.0`.
Replace `<version>` with the exact version and use `net48` for the .NET Framework runtime.
Resolve the placeholder to a real local path in your own build configuration.
Keep your own Grasshopper SDK reference and framework configuration.
Do not copy `Lugworm.gha` next to your `.gha`: that would give Grasshopper two copies to
load, the installed plug-in and your bundled copy. `Private=false` prevents that copy.
Lugworm's installation supplies its own runtime dependencies.

**Stability warning:** Lugworm is 0.x, and `Lugworm.Ui` is not a stable API. Pin the exact
Lugworm version and expect breaking changes between versions. These examples do not
establish a compatibility promise. When adapting a sample, generate new component and
assembly GUIDs for your plug-in.

## Build and verification limits

From the repository root, with the .NET SDK and the Windows targeting tools available:

```powershell
dotnet build samples/Lugworm.Samples/Lugworm.Samples.csproj -c Debug
```

The solution also builds this project for `net7.0-windows`, `net7.0` and `net48`.
It references `../../Lugworm/Lugworm.csproj` with `Private=false`. The only direct NuGet
reference is the same Grasshopper SDK as Lugworm. The bare `net7.0` target uses the SDK's
Windows Forms framework reference to compile its canvas UI without another package.
These are Windows examples. The sample assembly stays out of release staging, the `.yak`
and the send zip. Do not install the sample assembly as part of a Lugworm release.

**UNVERIFIED:** Loading a third-party `.gha` that references `Lugworm.gha` inside Rhino 8
on both the `net48` and `net7.0` runtimes, assembly resolution order, and the samples'
on-canvas behavior have not been tested. These samples are only compile-checked.
The per-sample interaction steps describe intended behavior for later operator testing.
