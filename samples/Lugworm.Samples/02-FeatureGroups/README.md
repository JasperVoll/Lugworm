# 02 Feature groups

Users show optional inputs from the right-click menu and optional outputs from
**Outputs shown**. The core Value input and pinned Result output remain visible.

Lugworm members used, all in
[VariableParamComponentBase.cs](../../../Lugworm/Ui/VariableParamComponentBase.cs):
`ParamSpec.Group`, `InputSpecs`, `ToggleGroups`, `Active`, `OnGroupsChanged`,
`OutputSpec.Pin`, `OutputSpecs`, `OutputToggleGroups`, `GetNumber` and `SetOut`.
Inherited `IamComponentBase.Write` and `Read` persist `Active` and `ActiveOutputs`
in the saved `.gh` file without sample serialization code.

To adapt [FeatureGroupsSample.cs](FeatureGroupsSample.cs):

1. Mark mandatory inputs with group `core`. Declare optional groups in `ToggleGroups`.
2. In `OnGroupsChanged`, update `Active` for dependent groups. Do not recursively call
   `ToggleGroup`. Here Offset requires Scale, so enable Scale first.
3. Declare outputs with `OutputSpec`, pin the required ones and list optional categories
   in `OutputToggleGroups`. Read inputs and emit outputs by name because indices move.
4. For a later canvas check, enable Scale, Offset and Details, then save/reopen the `.gh`.
   Turn Scale off and observe Offset disappear too.

Limits: input wires are retained when toggling groups within the current session, but
wires to hidden inputs do not survive save/reopen. Dormant parameter data is persisted.
A wired output category refuses to hide until its recipients are disconnected.
Offset cannot be enabled on its own. Persistence and interactions are unverified in Rhino.
