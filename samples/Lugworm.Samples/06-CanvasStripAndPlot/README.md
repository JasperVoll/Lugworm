# 06 Canvas strip and plot

Users drag a value bar from 0 to 1. Hover changes its color. Releasing the mouse updates
the output and a bitmap containing a point, quantity bar and legend.

Lugworm members used:

- `ICurveEditorHost.CurveEditor` and every `ICanvasStrip` member in
  [GoalCurveEditor.cs](../../../Lugworm/Ui/GoalCurveEditor.cs).
- `IGraphHost.GraphImage` and inherited `IamAttributes` mouse capture in
  [IamAttributes.cs](../../../Lugworm/Ui/IamAttributes.cs).
- `IamPlot.Theme`, `Current`, `NewPanel`, `Frame2D`, `Map`, `HBar`, `Legend` and `Small`
  in [IamPlot.cs](../../../Lugworm/Ui/IamPlot.cs). The theme accessor is
  `IamPlot.Current()`, not `Theme.Current`.

To adapt [CanvasStripAndPlotSample.cs](CanvasStripAndPlotSample.cs):

1. Implement `ICurveEditorHost` and `ICanvasStrip`. Here the component itself is the
   strip, keeping the example in one class. Cache the hit rectangle in `Render`.
2. Render only strip content because the inherited attributes draw its capsule.
   Return hover changes, set `Dragging` after a consumed press, and finish on `MouseUp`.
3. Call Grasshopper's `RecordUndoEvent` before changing the value. Override `Write` and
   `Read`, including base calls, so undo and `.gh` saving can restore it. Solve on release.
4. Build a bitmap with the current theme and expose it through `GraphImage`. Dispose the
   graphics context, replaced bitmap and the final bitmap on removal or document close.
5. For a later canvas check, hover, drag beyond the bar, release, undo and save/reopen.
   Change the canvas theme and recompute to check the plot colors.

Limits: one linear value only, no custom curves or double-click action. The strip follows
theme changes on repaint. The bitmap follows them on the next solve. The graph and output
retain the last solved value during a drag. Interaction, undo, theme rendering and assembly
loading are unverified in Rhino.
For full editors see [GraphMapEditor.cs](../../../Lugworm/Ui/GraphMapEditor.cs) and
[GoalCurveEditor.cs](../../../Lugworm/Ui/GoalCurveEditor.cs). For larger plots see
[GraphRenderer.cs](../../../Lugworm/Ui/GraphRenderer.cs) and
[DesirabilityComponent.cs](../../../Lugworm/Components/DesirabilityComponent.cs).
