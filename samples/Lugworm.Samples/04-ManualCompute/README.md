# 04 Manual compute

Users decide when a calculation runs. In manual mode, the amber **Compute *** button
marks stale inputs. Pressing it computes the square and shows elapsed time in the label.
The **Auto-compute** menu toggle enables normal recomputation and a gray **Auto** button.

Lugworm members used: `IamComponentBase.Heavy`, `Compute`, `Emit`, `InputSpecs` and
`GetNumber` in [VariableParamComponentBase.cs](../../../Lugworm/Ui/VariableParamComponentBase.cs).
The base owns `OnComputePressed`, `AutoCompute`, `CacheStale` and `ButtonLabel`, and uses
`SolveCache.Store` and `TryGet` in [SolveCache.cs](../../../Lugworm/Ui/SolveCache.cs).
The sample must not create a second cache.

To adapt [ManualComputeSample.cs](ManualComputeSample.cs):

1. Override `Heavy => true` and leave the inherited attributes and menus intact.
2. Read inputs and do the work in `Compute`. Return a result containing everything
   needed for output. Keep `Emit` limited to that result, never current inputs.
3. For a later canvas check, wire Value, press Compute, then change Value. The previous
   square should re-emit until Compute is pressed again. Toggle Auto-compute to compare.

Limits: the square is intentionally cheap, so timing may round to 0 ms. This is a compute
gate, not background execution or cancellation. Before the first compute there is no
cached output. The cache is session-only and keyed per instance and data iteration.
Changing input tree structure can leave an iteration without a cached result.
Auto/manual mode is saved in the `.gh`, but computed results are not. UI behavior is
unverified. See [RsmEvalComponent.cs](../../../Lugworm/Components/RsmEvalComponent.cs)
for a larger product example.
