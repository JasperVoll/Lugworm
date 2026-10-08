# 01 Canvas buttons

Users increment a counter or reset it with action capsules. A dropdown selects a step
of 1 or 5. Labels follow the counter and step, and actions disable when unavailable.

Lugworm members used:

- `IamComponentBase.CreateAttributes`, `InputSpecs`, `Compute`, `Emit` in
  [VariableParamComponentBase.cs](../../../Lugworm/Ui/VariableParamComponentBase.cs).
- `IButtonRowHost.ButtonRows`, `OnCapsulePressed`, `ButtonDef`,
  `IDropdownHost.DropdownLabel`, `FillDropdown` and `IamAttributes` in
  [IamAttributes.cs](../../../Lugworm/Ui/IamAttributes.cs).

To adapt [CanvasButtonsSample.cs](CanvasButtonsSample.cs):

1. Derive from `IamComponentBase`. Inherit `CreateAttributes` unchanged.
2. Implement the two host interfaces. Return current state from their properties.
3. Handle actions on the UI thread, record undo before mutation, then expire layout
   and the solution. Serialize custom fields with `Write` and `Read`, calling the base.
4. For a later canvas check, select Step 5, add to 100, check that Add disables, then
   reset and check that Reset disables. Exercise undo and save/reopen.

Limits: the counter is intentionally capped at 100. No icon or custom attributes class
is needed. The inherited input strip is present but has nothing to wire. UI behavior is
unverified. Full product examples are
[LoadSettingsComponent](../../../Lugworm/Components/LoadSettingsComponent.cs),
[SampleGeometryComponent](../../../Lugworm/Components/SampleGeometryComponent.cs) and
[GraphMapperComponent](../../../Lugworm/Components/GraphMapperComponent.cs).
