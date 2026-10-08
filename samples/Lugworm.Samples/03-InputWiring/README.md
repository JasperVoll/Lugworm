# 03 Input wiring

Users press **+ inputs** or choose **Wire inputs (sliders / toggles / lists)** to create
editable sources for visible, unconnected inputs. This example produces a width slider,
an enabled toggle, a text panel and a value list with Single and Double choices.

Lugworm members used:

- `ParamSpec.Hint`, `HintBool`, `HintText`, `IamComponentBase.InputSpecs`, `ValueLists`,
  `GetNumber`, `GetBool`, `GetInt` and `GetString` in
  [VariableParamComponentBase.cs](../../../Lugworm/Ui/VariableParamComponentBase.cs).
- The inherited strip/menu delegates to `SettingsIo.WireInputs` in
  [SettingsIo.cs](../../../Lugworm/Ui/SettingsIo.cs), then `InputWiringHelper.WireAll` in
  [InputWiringHelper.cs](../../../Lugworm/Ui/InputWiringHelper.cs).
  [ValueListHelper.cs](../../../Lugworm/Ui/ValueListHelper.cs) also exposes
  `ValueListHelper.Attach` for explicit attachment. This sample needs no direct call.

To adapt [InputWiringSample.cs](InputWiringSample.cs):

1. Declare each input with a `ParamSpec` and the appropriate hint method.
2. Add a `ValueLists` entry whose `param` exactly matches the input name. Supply readable
   labels and Grasshopper expressions as strings, here `"0"` and `"1"`.
3. Supply the same defaults to the read helpers. Hints configure newly created controls,
   not input persistent data or validation of existing sources.
4. For a later canvas check, wire all inputs, choose Double, then toggle Enabled off.

Limits: already wired inputs are left alone by ordinary wiring. The slider range does
not constrain values arriving from other components. Hidden inputs are not wired.
Settings registered for the document can supply defaults instead of hints.
Control creation and placement are unverified in Rhino.
