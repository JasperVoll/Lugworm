# 05 Settings files

Users save reusable input settings with **Save settings...** and restore them with
**Load settings...** in the inherited component menu. Lugworm writes versioned JSON
with the `.lwset.json` extension. This sample gives the file a stable component key.

Lugworm members used: `IamComponentBase.SettingsKey`, `InputSpecs`, `ValueLists` and
read helpers in [VariableParamComponentBase.cs](../../../Lugworm/Ui/VariableParamComponentBase.cs).
The inherited menus invoke `SettingsIo.Save` and `Load`, which use `Capture` and `Apply`
in [SettingsIo.cs](../../../Lugworm/Ui/SettingsIo.cs). No JSON or file-dialog code is needed.

To adapt [SettingsFilesSample.cs](SettingsFilesSample.cs):

1. Declare hinted inputs and choose a stable, unique `SettingsKey`. Keep it across renames.
2. Inherit the save/load menus. Wire and solve the inputs before saving their values.
3. For a later canvas check, save, change Width and Label, then load the file to restore
   the editable sources. Geometry should be reported as skipped. Feed Width from a
   component output and check that it is skipped too.

Capture is limited to numbers, integers, booleans and text, including small lists, from
inputs fed by one slider, toggle, panel or value list, or by nothing. Unwired inputs
need collected data to be captured. A fallback value inside `Compute` is not captured
automatically. Geometry, points, curves, planes, colors, component-output data and
unsupported or multiple sources are excluded. Lists over 64 items and text over 4096
characters are also skipped. Outputs, custom fields and computed results are not settings.
Input group choices are included. Applying settings leaves unsupported sources alone.

The folder-wide mechanism is the existing
[LoadSettingsComponent](../../../Lugworm/Components/LoadSettingsComponent.cs) and
`SettingsRegistry.Register`, `SettingsFor` in
[SettingsRegistry.cs](../../../Lugworm/Ui/SettingsRegistry.cs). It registers a settings
folder per document for inherited input wiring, without persisting that registry in `.gh`.
This sample does not reimplement it.

**Diagnostic limit:** `LoadSettingsComponent.KnownKeys` currently discovers only types
in Lugworm's own assembly and derives their default keys from type names. Its file report
can therefore label an external component's file as unmatched. This is a reporting limit:
`SettingsRegistry` retains the full index, and input wiring and Apply to canvas match actual
component instances by `SettingsKey`. No sample feature needs a Lugworm API change.
A future public key registration/discovery extension could correct that diagnostic.
File interaction, folder application and Rhino loading remain unverified.
