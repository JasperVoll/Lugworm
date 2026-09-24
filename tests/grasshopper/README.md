# Rhino-hosted compatibility checks

`component-contracts-v3.json` was captured from unchanged plugin source at
`ad991b579dce9bc48c35b90570e4cc1842a3d5ad` on 2026-09-07. It records all 29
component GUIDs, default/expanded socket contracts, declared groups and active groups.
The baseline is data, not a generator to rerun whenever a compatibility test fails.
Descriptions are intentionally excluded; names, nicknames, types, access, optional
flags and ordering are pinned. A new component needs an explicitly allowed new GUID.

Run from the repository root after a Release build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-rhino-contracts.ps1 -Framework net48
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-rhino-contracts.ps1 -Framework net7.0
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-rhino-contracts.ps1 -Framework net7.0-windows
```

The override applies only to the launched PowerShell process. Each probe uses an
isolated Rhino scheme and disabled test documents, never solves or connects to a
robot, and closes only its own process. It creates actual plugin components, wires
every registered input and output, binary-serializes a GH document and restores it
through GH_Archive. Both default and fully expanded documents must retain their
contracts and exact wire endpoints. Reflection is test-only, including GH's internal
single-file loader; unsupported host APIs fail explicitly.


## The 4.1.0 invocation

`component-contracts-v3.json` is still the only baseline: **no v4 baseline was ever
captured**, because 4.0.0's host run was never performed. 4.1.0 therefore declares its
deliberate breaks against v3 alongside 4.0.0's, rather than against a file that does not
exist. Run from the repository root after a Release build (one line):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-rhino-contracts.ps1 -Framework net48 `
  -AllowedAdditions 'a1a2c3d4-0030-4beb-9c3d-aa0000000030','a1a2c3d4-0031-4bfb-9c3d-aa0000000031','a1a2c3d4-0032-4c0b-9c3d-aa0000000032','a1a2c3d4-0033-4c1b-9c3d-aa0000000033' `
  -AllowedRemovals 'a1a2c3d4-0011-4abb-9c3d-aa0000000011','a1a2c3d4-0012-4acb-9c3d-aa0000000012','a1a2c3d4-0020-4b4b-9c3d-aa0000000020','a1a2c3d4-0021-4b5b-9c3d-aa0000000021','a1a2c3d4-0028-4bcb-9c3d-aa0000000028' `
  -AllowedChanges 'a1a2c3d4-0001-4a1b-9c3d-aa0000000001:all','a1a2c3d4-0010-4aab-9c3d-aa0000000010:all','b1a2c3d4-0002-4a2b-9c3d-aa0000000002:inputs,all_input_specs,groups,active_groups','c1a2c3d4-0003-4a3b-9c3d-aa0000000003:all','f1a2c3d4-0006-4a6b-9c3d-aa0000000006:all','a1a2c3d4-0022-4b6b-9c3d-aa0000000022:name','a1a2c3d4-0023-4b7b-9c3d-aa0000000023:all','a1a2c3d4-0024-4b8b-9c3d-aa0000000024:all','a1a2c3d4-0025-4b9b-9c3d-aa0000000025:all','a1a2c3d4-0026-4bab-9c3d-aa0000000026:all','a1a2c3d4-0027-4bbb-9c3d-aa0000000027:outputs','a1a2c3d4-0029-4bdb-9c3d-aa0000000029:name,outputs','a1a2c3d4-0014-4aeb-9c3d-aa0000000014:inputs,all_input_specs'
```

Repeat with `-Framework net7.0` and `-Framework net7.0-windows`.

What 4.1.0 adds to the 4.0.0 allowance list:

| GUID | Component | Why |
|---|---|---|
| `a1a2c3d4-0027-…0027` | Graph Mapper | **new entry** (4.1.0): `outputs` — the `map` output is appended at index 3 |
| `a1a2c3d4-0010-…0010` | Test Geometry | **new entry** (4.2.0): `all` — the `standard` and `zigzag` input groups and the appended `path` output |
| `a1a2c3d4-0014-…0014` | G-code Restart | **new entry** (2026-09-16): `inputs,all_input_specs` — `robot` appended last, so the `point` selector and the previews follow the definition's coordinate frame |
| `a1a2c3d4-0001-…0001` | Slice | already `all` — now also the `wave`, `bricklayer` and `nonplanar` groups (16 inputs) and the extended `infill` value list |
| `c1a2c3d4-0003-…0003` | G-code Generator | already `all` — 4.0.0's `fdm` group and `filamentMm` output are **removed again**, so against v3 the net difference shrinks |
| `a1a2c3d4-0029-…0029` | Duet Console | already `name,outputs` — the five heater/fan outputs are **removed** (its only non-append output change ever); `speedFactor`/`flowFactor` stay |
| `a1a2c3d4-0025-…0025` | Robot Definition | already `all` — the `process` socket is kept (reporting `Paste (LDM)`) but the two FDM presets leave the value list |

**Run on 2026-09-09 and again on 2026-09-10, and it passes**, on Rhino
8.34.26223.11001, for all three payloads. At 4.1.0: 31 components, 374 default and 586
expanded wires. At 4.2.0: 375 default and 594 expanded - the Test Geometry component's
one appended output and seven new inputs, and nothing else. Every declared socket change
matched and nothing undeclared differed.

This was the **first execution of this probe on a host**, and it immediately found the
probe itself was broken: `verify-component-contracts.py` invoked
`SyncInputs(bool recordUndo = true, bool recompute = true)` through reflection with no
arguments, which raises `Parameter count mismatch` because reflection does not fill
optional parameters. The defect predates the v3 baseline (the signature has had its two
optional parameters since 2026-06-24) and had simply never been exercised. Both
arguments are now passed explicitly as `False` - which is also more correct than the
defaults would have been, since this probe records no undo events and must never solve.

The **baseline capture is still owed**, and separately: a capture must follow the manual
Rhino round, which has not happened. Capture it with
`-Capture -Baseline tests/grasshopper/component-contracts-v4.1.json` once it has.


Run `-InjectFailure` to verify failure propagation; **expect exit 1**, a fresh failed
report, and `Injected host assertion failure`. A timeout is not this negative test's
expected result. Unique per-run reports live in ignored `bin/` subdirectories and
identify plugin path, module ID, target framework, actual CLR and Rhino version.

Initial passing host: Rhino 8.34.26223.11001, net48 on CLR 4.0.30319.42000;
net7.0/net7.0-windows plugin payloads on Rhino's CLR 8.0.30. The focused xUnit project
separately executes Core on CLR 7 as well as net48. These host tests required an
out-of-sandbox launch in the managed environment; sandbox startup timed out without
executing the script. Parenthesized script paths inside `/runscript` are required
instead of nested escaped quotes ([McNeel guidance](https://discourse.mcneel.com/t/launching-rhino-from-bat-and-running-command-with-space-in-path/177242/7)).

Limits: this is a current-source archive/wire baseline, not a historical 2.6 fixture,
a solved functional definition, saved RSM/goal/GraphMapper value coverage, a package
installation test, a viewport/frame-rate test, or hardware acceptance. Those remain
separate manual/integration gates. No historical .gh/.ghx fixtures were found in the
in-scope checkout outside build outputs and excluded external/lab directories.

## Offline solving probes (2026-09-07, not yet executed on a host)

One probe rides on the same launcher and solves only its own hand-derived definition;
it neither connects to a machine nor opens a window:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-rhino-contracts.ps1 -Framework net48 -Probe pareto-selection
```

- `pareto-selection-roundtrip.py`: select a recipe on the plan's A/B/C front, reorder the
  front, save the component to a `GH_Archive`, destroy and recreate it, read it back with no
  upstream data, then refuse a key from another front and check copy isolation.

Since Pareto Select exists, the contract comparison needs its GUID allowed:
`-AllowedAdditions a1a2c3d4-0030-4beb-9c3d-aa0000000030`. Do not regenerate the baseline
to absorb it.

Interlayer Time also adds `a1a2c3d4-0031-4bfb-9c3d-aa0000000031`. From PowerShell,
pass both GUIDs as an array when invoking the script directly:

```powershell
& ./tools/verify-rhino-contracts.ps1 -Framework net48 -AllowedAdditions @('a1a2c3d4-0030-4beb-9c3d-aa0000000030', 'a1a2c3d4-0031-4bfb-9c3d-aa0000000031')
& ./tools/verify-rhino-contracts.ps1 -Framework net48 -Probe interlayer-time
```

The Interlayer probe exercises repeated component solves, document-unit and exact
file-content cache invalidation, coloured layer meshes and invalid-input cleanup.
Like the other probes, it does not command hardware.

## Declared removals and socket changes (4.0.0)

`-AllowedAdditions` only covers new GUIDs; two more flags handle a component that was
deliberately removed or reshaped rather than added. `-AllowedRemovals` takes GUIDs that
may be missing from the current run without failing the gate (a retired component, never
reused). `-AllowedChanges` takes `guid:key,key` entries (`;`-separated when passed as one
string, or one array element per GUID from PowerShell) naming which contract keys may
differ for that GUID, or `guid:all` to allow any key on that record. 4.0.0 retires the
UR Program and UR Robot components and drops three inputs from Lugworm
Robot Simulation, so it compares against the untouched v3 baseline with:

```powershell
& ./tools/verify-rhino-contracts.ps1 -Framework net48 `
    -AllowedRemovals @('a1a2c3d4-0011-4abb-9c3d-aa0000000011', 'a1a2c3d4-0012-4acb-9c3d-aa0000000012') `
    -AllowedChanges @('a1a2c3d4-0026-4bab-9c3d-aa0000000026:inputs,all_input_specs')
```

Do not regenerate the baseline to absorb the removal or the changed sockets — the same
allowances apply until a v4 baseline is captured.

## Print bed contract (2026-09-09)

The bed contract makes `bed` the only plate input on every consumer, removes Live View's
`bedWidth`/`bedDepth`/`bedCenter` and its `beddef` group, moves Place's `bed` into the
always-visible input list, and appends a `shape` input to Print Bed. Three more
`-AllowedChanges` entries are needed on top of the 4.0.0 ones above; Print Bed and Robot
Simulation are already `all` and stay that way (Robot Simulation's sockets did not change at
all - only its fallback plate went). The SCARA calibration pair is gone entirely, so its two
GUIDs join the retired UR ones in `-AllowedRemovals` rather than taking a change entry:

```powershell
& ./tools/verify-rhino-contracts.ps1 -Framework net48 `
    -AllowedRemovals @('a1a2c3d4-0011-4abb-9c3d-aa0000000011', 'a1a2c3d4-0012-4acb-9c3d-aa0000000012',
                       'a1a2c3d4-0020-4b4b-9c3d-aa0000000020', 'a1a2c3d4-0021-4b5b-9c3d-aa0000000021',
                       'a1a2c3d4-0028-4bcb-9c3d-aa0000000028') `
    -AllowedChanges @(
        'a1a2c3d4-0026-4bab-9c3d-aa0000000026:inputs,all_input_specs',
        'a1a2c3d4-0023-4b7b-9c3d-aa0000000023:all',
        'a1a2c3d4-0024-4b8b-9c3d-aa0000000024:all',
        'b1a2c3d4-0002-4a2b-9c3d-aa0000000002:inputs,all_input_specs,groups,active_groups')
```

| GUID | Component | Why |
|---|---|---|
| `a1a2c3d4-0023-4b7b-9c3d-aa0000000023` | Live View | `all` - the 4.0.0 rename (`name`), removed and added inputs AND outputs, `bed` promoted to core, and both the `beddef` and `log` groups gone: `inputs`, `all_input_specs`, `outputs`, `groups` and `active_groups` all move, in both records. See the 2026-09-09 note below |
| `a1a2c3d4-0024-4b8b-9c3d-aa0000000024` | Print Bed | already `all` from 4.0.0; now also the appended `shape` input |
| `b1a2c3d4-0002-4a2b-9c3d-aa0000000002` | Place | `bed` moved from the `beddef` group to the end of the input list, so the default record gains an input and the group lists shrink |
| `a1a2c3d4-0020-…0020`, `a1a2c3d4-0021-…0021` | Calib Points, Scara Fit | `-AllowedRemovals` - both components were removed; their indices are never reused |
| `a1a2c3d4-0028-…0028` | Version | `-AllowedRemovals` - removed 2026-09-21; its index is never reused |

Do not regenerate the baseline to absorb these either. Both documents (default and expanded) are
compared with the same allowances, which is why a change that only shows in the expanded record
still needs its entry.

## Duet Console output categories (2026-09-09)

The Duet Console's 27 fixed outputs became six categories with three pinned sockets
(`host`, `status`, `gcode`), so a component created by the harness now registers three outputs
instead of twenty-seven, and `gcode` is new. The console's existing 4.0.0 allowance already
covers exactly this and does **not** need widening:

```powershell
'a1a2c3d4-0029-4bdb-9c3d-aa0000000029:name,outputs','a1a2c3d4-0014-4aeb-9c3d-aa0000000014:inputs,all_input_specs'
```

`outputs` is the only key that moves, in the default AND the expanded document. It is worth
saying why the other two candidates do not:

| Key | Moves? | Why |
|---|---|---|
| `outputs` | yes | three sockets instead of twenty-seven, plus the new `gcode` |
| `groups` / `active_groups` | no | the dumper reads the INPUT-side `ToggleGroups` property and `Active` field; output categories live in their own `OutputToggleGroups` and `ActiveOutputs` and are deliberately not folded into them |
| `inputs` / `all_input_specs` | no | no input changed |

The harness creates each component fresh, so it sees the compact default; the archive
migration that keeps an old document's wired categories visible runs only on a saved
document and is covered by the manual rows in the changelog, not here. Do not regenerate
`component-contracts-v3.json` for this.

## Live View: no extrusion, no log, a `trail` output (2026-09-09)

Live View changed again and, this time, its OUTPUTS changed too. The `all` allowance it
already carries covers that - `all` means any contract key on that record, and `outputs` is one
of them - so **no new flag is needed**; the command block above is unchanged. What differs for
`a1a2c3d4-0023-4b7b-9c3d-aa0000000023` now:

- inputs `extWidth` and `layerHeight` are gone, and so are the six `log` group inputs together
  with the `log` toggle group itself, so `groups` and `active_groups` shrink as well;
- outputs `layers`, `log`, `path` and `logInfo` are gone and a geometry-tree `trail` output
  takes index 7.

The component's own archive normalization handles the input side (`InputContractVersion` 2
with eleven retired specs) and reports the removed outputs on load, but the harness compares
declared contracts, not archives, so it sees the whole set of differences. Do **not** recapture
the v3 baseline for it.
