# Lugworm — Test Feedback

For a fresh, component-by-component test round, use the
[component test sheet](COMPONENT-TEST-SHEET.md); its Appendix B maps the ids of the
earlier rounds. It separates offline canvas checks from physical SCARA trials. The older
results and counts below are historical evidence, not results for a new build; the dated
round records up to 4.2.1 were archived outside the repository on 2026-09-24.

**Package:** `lugworm` - **Version:** _fill in the version you tested_ - **Target:** Rhino 8 (Windows) + Grasshopper

> How to use this file: copy it, fill in the sections below as you test, and send it back
> (or edit in place and share). Leave anything you didn't get to blank. Rough notes are fine —
> a screenshot + "this looked wrong" is more useful than nothing.

---

## 1. Setup

### Automated readiness gates (2026-09-07)

From the repository root, run `dotnet build Lugworm.sln -c Release`, `dotnet test`,
and `python -m unittest discover -s tests/tooling` (25 tests — pure comparator,
source-allowlist, console-parity, DoE-export and public-link guards; no Rhino, no network).
The solution includes the focused FrameworkSmoke tests on net48/net7.0/net7.0-windows as well
as the full net10 Core suite. At 4.0.0 that is **1348 Core**, **358 per shipping TFM** and
**25 tooling**. Run `dotnet test -c Release --no-build` for Release runtime evidence too.
`dotnet test --filter "Category!=Perf"` is the default gate (wall-clock-budget tests
excluded so a busy machine can't fail it); run `--filter "Category=Perf"` explicitly
to check those (see docs/4-unit-tests/TESTING.md).

The Rhino compatibility launcher verifies the captured unchanged-source baseline:
`powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-rhino-contracts.ps1 -Framework net48`.
The baseline already exists; do not regenerate it to hide a compatibility change.
Repeat with
`-Framework net7.0` and `-Framework net7.0-windows`. The ExecutionPolicy override is
limited to that PowerShell process. No definitions are solved and no robot is connected.
Run `-InjectFailure` without `-Capture` to prove a real host assertion returns a nonzero
launcher result. Inspect the run-specific JSON under `tests/grasshopper/bin/`; a process
launch, timeout, old report or script-start marker is **not** a pass. In the managed
environment, host probes needed an out-of-sandbox launch. See
`tests/grasshopper/README.md` for baseline provenance and scope limits.

| ID | Host acceptance | Result |
|---|---|---|
| READY-01 | Exact Release assembly path/TFM and CLR reported; unique GUID/default and expanded socket/group baseline for every component | OK 2026-09-07: 29 components, all three payloads, Rhino 8.34 |
| READY-02 | Save/reload wired default and expanded documents through genuine GH archive; all sockets and wire endpoints preserved, no solve/window/connect | OK 2026-09-07: 375 default/589 expanded wires, all three payloads; historical files remain pending |
| READY-03 | Deliberately injected host assertion produces fresh failed report and nonzero launcher result; no other Rhino process closed | OK 2026-09-07: fresh net48 report with injected AssertionError, launcher exit 1 |

### Manual setup

- **Fresh machine:** follow [INSTALL.md](../../INSTALL.md).
- **Already have an older version installed:** follow [UPGRADE.md](../../UPGRADE.md) — do not skip
  the uninstall step, mixed old/new files are the #1 source of ghost bugs.

The components appear in Grasshopper under the **Lugworm** tab:
**1 Slice - 2 Place - 3 G-code - 4 Robot - 5 Analyze - 6 Calibrate - 7 Print**.

**Renamed in 4.0.0, the console again in 5.0.0 — the older sections below still use the old names.** No GUID and no socket
moved, so a saved file finds the same component under the new label:

| Called this before 4.0.0 | Called this now |
|---|---|
| Duet Console (Print Console in 2.6.0, Machine Console in 4.x) | **Duet Console** (`…0029`) |
| Duet Files | **Machine Files** (`…0022`) |
| Duet Live | **Live View** (`…0023`) |

The five hidden compatibility components (Duet Connect / Status / Jog / Terminal / Print) keep
their `Lugworm Duet …` names deliberately. `UR Program` and `UR Robot` no longer
exist — rows below that mention them are historical.

| | |
|---|---|
| Tester | |
| Date | |
| Rhino version (`Help → About`) | |
| OS | |
| Upgrade or fresh install? | |
| Install worked? (y/n) | |

---

## 2. What's new since 1.5.0 (test focus)

| Area | What changed |
|------|--------------|
| **Slice** | **Heat mode is back** (mode 2, non-planar): layers follow the surface but are slope-capped for the vertical nozzle (`maxStagger` input, default 30°). Component bubble shows `mode - layers`. ⚠ Old files with mode=2 no longer degrade to planar! Heat emits perimeter rings only — enable **varh** on G-code. One bad object in a list no longer kills the others. **Superseded: heat mode was retired again in 4.0.0 — a saved `mode = 2` slices planar with a warning. The three rows below are no longer runnable.** |
| **Place** | Two new seam modes: **4 guide** (seam follows a drawn world-space curve) and **5 track** (each layer seams at the closest point to the previous layer's seam — no more seam wander). Early reach heads-up when the placed part leaves the SCARA annulus. |
| **G-code** | **ArcWelder post-process actually welds now** (it silently never did — expect G2/G3 in output when enabled). New **ecomp** group: slope-compensated flow for conical/non-planar prints (needs a measured multiplier — warning explains). Implausible `ramDiameter` warns. |
| **Robots** | G2/G3 arcs are now checked along the swept path, not just endpoints. **Self-collision warnings**: UR link-pair clearances + below-base; SCARA link-2-vs-mast sweep (both default on, toggleable). UR10e poses shift ≤1 mm (DH corrected against UR's tables); UR elbow speed warnings less eager (cap 120→180°/s per datasheet). Files using `G92 X/Y/Z` validate at correct coordinates now. Robot warns if `Robot_Assembly.3dm` changed since base calibration. |
| **Print Stats** | New `accel` input (feeds group): trapezoidal acceleration model with corner slowdown — `timeMin` becomes the realistic estimate instead of the lower bound. |
| **Calibrate (DoE)** | Malformed tables/goals give readable errors instead of crashes; >12-factor desirability no longer hangs; near-singular fits are flagged instead of showing bare NaN dashes. |
| **Performance** | Overhang analysis 12× faster, variable-layer-height 10× faster; SCARA viewport scrubbing noticeably smoother (bed + edge caching). |
| **Defaults** | All defaults now match the lab reference definition (2026-07-14): layer 3 mm, width 10, pressure 1.2, print 43 / travel 30 mm/s, retract 1 mm/s, offset/prime 0.2, coast 2, zHop 5 @ 10 mm/s, gcode zOffset 4, seam Align, print centre (339, −193), bed 700×1000 @ (441, −468). A fresh `⇤ inputs` click reproduces a working print setup with no slider tuning. |

### Extra test rows for 1.6.0

| Area | Status | Notes |
|------|:------:|-------|
| Place — seam mode 4 follows a drawn line; mode 5 stops seam wander | | |
| G-code — ArcWelder toggle produces G2/G3 in the file | | |
| Robot — near-base target (r≈100) → mast-sweep warning | | |
| UR — self-collision warning on tight fold; off when disabled | | |
| Print Stats — accel 500 raises timeMin vs accel 0 | | |

### Extra test rows for 1.10.0 — print path log (Duet Live)

Needs a real machine: the xUnit suite reaches Core only, so the session wiring, the
component surface and the viewport trail have no automated coverage. Arm the log via
Live View (Duet Live before 4.0.0) right-click -> **Print path log**, wire `logEnable` +
`logFolder`.

| Check | Status | Notes |
|------|:------:|-------|
| Short real print: layer count in `layers` matches the sliced layer count | | |
| CSV rows keep arriving while the canvas is busy (drag a slider during the print) | | |
| Cancel mid-print: file still flushed and closed, run-end rows present with the observed `terminal_status` (`finish_reason` stays empty — it is for log-side stops). On the legacy Potterbot expect `Idle`, not `Cancelling` — RRF < 3.2 has no cancelling state; check `fraction_printed` < 1 instead | | |
| `logInfo` reports `rows dropped: 0` after a normal print | | |
| Unwritable `logFolder` (e.g. `C:\Windows\printlog`): warning on the component, memory log continues, connection undisturbed | | |
| Pull the network past 60 s: final layer span closed with `finish_reason = ConnectionLost` | | |
| Very short job that ends before a name arrives: an `unknown-job` CSV still appears | | |
| Machine paused for a minute: `layers` / `log` outputs keep refreshing (PrintLog change kind) | | |
| Two Duet Live components on one host: second reports `Secondary`, only one CSV is written | | |
| Log group ON but `logEnable` OFF: the live blue trail still draws as before | | |
| Old 1.9.0 `.gh` opens with unchanged inputs and existing output wires intact | | |
| `logExport` button (rising edge) writes one file per press, not one per solve | | |
| Reviewer check (code, not runtime): `SnapshotDelta` -> `SessionChangeKind` mapping is per flag, never a cast | | |

### Extra test rows for 1.11.0 — firmware motion limits

The Core scanner and comparison are covered by the requester's automated testing
gate. These two rows exercise the plugin/session wiring on a real or scripted Duet:

| Check | Status | Notes |
|------|:------:|-------|
| Connect downloads `0:/sys/config.g`, reports the real `M201 X800` baseline, and shows an unverified warning without blocking when the file is absent or malformed | | |
| A job emitting `M201 X3000` warns at upload and still uploads with `blockWarnings` off; turning `blockWarnings` on refuses it, while an M203 feedrate note remains informational | | |
| **Print start still succeeds on a slow link.** The motion probe makes three command round-trips (2 s timeout each) while `StateGate` refuses snapshots older than 3 s. It is ordered after the gate for that reason — confirm a normal start is not refused as "state is stale", especially over WiFi or with the board busy | | |
| **BREAKING — rewire after the `gcode_text` removal.** Open every existing `.gh`: the G-code component lost output index 1 (11→10 outputs) and Restart lost its own (6→5), so **every wire after `gcode` shifts up one**. Grasshopper reconnects by position and reports nothing, so this is silent. Confirm `preview`/`travels`/`length`/`info` still feed what they used to and re-wire where they do not. Files in this repo to check: `docs/Debug_script.gh`, `docs/Example_script.gh` | | |
| A large job no longer hangs the canvas when the G-code or Restart component recomputes (the removed single-string output was the cause) | | |

### Extra test rows for 1.12.0 — arm mode and control-gate visibility

The arm-mode seeding is covered by the automated gate. These rows exercise the two
plugin-layer paths the xUnit suite cannot reach.

| Check | Status | Notes |
|------|:------:|-------|
| **Unhomed Print component speaks up.** Connect without homing: the Print component shows a warning naming the arm-configuration refusal (it used to grey every button silently and leave only E-STOP live). Then `Home all` on Jog: the warning disappears by itself and Validate/Upload/Start become clickable — no manual recompute | | |
| **Live view draws the real branch.** After a full `G28`, the Duet Live arm's elbow points toward $+X$ (matching J1 $\approx -18.5^\circ$), not mirrored across the line from the origin to the nozzle. Cross-check against `M114`'s joint readout | | |
| **Live view still shows an unhomed board honestly.** After `M112` then `M999` and a reconnect, the arm renders fully extended because RRF reports $\theta=\psi=0$ after the motor counts reset. That is correct, not a bug — the pose must NOT be suppressed. `info` reports `axes homed: no`. Homing restores it | | |
| **Idle canvas stays quiet.** Connected and homed: no gate warnings on any Duet component. Disconnected: at most one grey remark per component, never orange | | |
| Old `.gh` with an unwired `elbow` input: opens and now validates in arm mode *down*. A definition that deliberately internalised `0` keeps its value — confirm it still opens unchanged | | |

## 2b. What was new in 1.5.0 (kept for upgraders)

| Area | What changed |
|------|--------------|
| **Icons / UI** | Frameless glyph icons, one colour per tab group. Every component has a **`⇤ inputs` button** that wires sliders (correct ranges), toggles, value lists and panels to all visible inputs in one click. |
| **Slice** | **Heat mode removed** (old files degrade to planar with a warning). Conical + mesh sections now use tolerance-driven quality meshing (contours sit on the surface). **Fermat infill rewritten** — true connected Fermat spiral, path start/end together at the boundary. Infill no longer crashes when other plugins (ovenbird, Nautilus) ship their own Clipper2. |
| **Gcode** | Ram Ø defaults to 94 mm, retraction rescaled to the ram screw (~0.1 mm), plain `BEGIN/END HEADER` + `G28` homing in the header. Saving to a **folder** path now auto-names the file instead of "access denied". |
| **URScript** (3 Gcode) | New: G-code/toolpath → UR `.script` (movel, tool-down poses, extruder via digital out, safe blend radii). |
| **Robot (SCARA)** | Arctic look: white layers (flat-top, filleted, pipes **around corners**), near-white bed with the true printable area outlined, bed follows the `bedCenter` point. DANGER banner faces the camera (and is no longer mirrored). |
| **UR Robot** (4 Robot) | New: UR10/UR10e/UR5 simulator — analytic IK, joint/speed/reach checks, danger banner, optional real UR10 mesh model from the STEP assembly (`mesh` group). |
| **Print Stats** (5 Analyze) | New: lengths, extruded volume (exact from E values + 94 mm ram), time estimate, per-layer breakdown. |
| **6 Calibrate** | New DoE workflow: **DoE Design** (DSD/BBD/Taguchi/FullFact2/FracFact2 → Excel/CSV plan) → **RSM Evaluate** (fits models from the filled table, on-canvas effect bars + contours) → **Desirability** (optimal recipe + sweep). **Test Geometry** with shape dropdown (vase, cylinder, wall…). Example data: `docs/reference/example_doe_measurements.xlsx`. |

---

## 3. Testing results

Mark each: ✅ works - ⚠️ works with issues - ❌ broken - — not tested.

### Core pipeline
| Area | Status | Notes |
|------|:------:|-------|
| Install / components load (all 7 groups) | | |
| `⇤ inputs` button wires sliders/toggles/lists with sane ranges | | |
| Slice — planar (contours ON the surface, incl. curved vases) | | |
| Slice — conical (smooth contours, no faceting) | | |
| Infill — zigzag / concentric / spiral (with ovenbird/Nautilus also installed!) | | |
| Infill — fermat (single path, starts AND ends at the boundary seam) | | |
| Place — print-center moves toolpath | | |
| G-code — header/footer format, `G28` present, runs on the printer | | |
| G-code — save to a FOLDER path (should auto-name, not error) | | |

### Robots
| Area | Status | Notes |
|------|:------:|-------|
| Robot Simulation / Potterbot — scrub, pose, reach/dead-zone, bed follows Print Bed | | |
| Robot Simulation / Potterbot — extrusion preview (blue deposition, ochre travel, corners piped, flat tops) | | |
| Robot Simulation / UR — schematic sim + checks on a reachable path | | |
| Robot Simulation / UR — mesh arm renders from the embedded model (schematic fallback warns if it fails) | | |
| UR Program — script structure looks right / runs in URSim | | |
| Print Stats — volume matches the G-code component's volume | | |

### Calibration (DoE)
| Area | Status | Notes |
|------|:------:|-------|
| DoE Design — BBD plan generates + exports .xlsx/.csv | | |
| DoE Design — FracFact2 / DSD / Taguchi run counts sensible | | |
| RSM Evaluate — reads `docs/reference/example_doe_measurements.xlsx`, R² ≈ 1, graphs render | | |
| Desirability — recipe + on-canvas profile (try `target=7 in 6..8 w=2`, `min`, `min`) | | |
| Test Geometry — dropdown shapes slice + print end-to-end | | |

---

## 4. Bugs

Copy the row per bug. Severity: **blocker / major / minor / cosmetic**.

| # | Component | Severity | Steps to reproduce | Expected | Actual | Screenshot? |
|---|-----------|----------|--------------------|----------|--------|-------------|
| 1 | | | | | | |
| 2 | | | | | | |
| 3 | | | | | | |

---

## 5. Missing features / requests

| # | What | Why it matters | Priority |
|---|------|----------------|----------|
| 1 | | | |
| 2 | | | |

---

## 6. General feedback

UX, naming, defaults, documentation, anything else:

-
-

## Extra test rows for 2.0.0 / 2.1.0 — definitions, compatibility, and previews

These are plugin-layer checks that the Rhino-free xUnit suite cannot perform.

| Check | Status | Notes |
|------|:------:|-------|
| Open a pre-2.0 `.gh`: Place, Duet Live, Print Stats, UR Program, and G-code wires remain on their original named sockets; the new Print Bed / Robot Definition inputs appear only when their optional groups are enabled | | |
| Robot Definition: each preset emits stable text; invalid text fails loudly; save/reopen preserves the selection; Generic Duet's remark points at Duet Jog / Print / Live | | |
| Print Bed: definition round-trips through Place, Robot Simulation, and Duet Live; explicit component inputs still win; plate centre and print centre remain distinct | | |
| Robot Simulation (SCARA): a G28-containing program uses the calibrated homed arm branch, resets after later G28, and reports the same problems as the hardware gate | | |
| Robot Simulation (SCARA and UR): matte/unlit meshes and feature edges remain stable while scrubbing, without z-fighting | | |
| Robot Simulation: deposition is blue, travel is ochre, an all-travel path has no blue deposition, and travel remains visible with extrusion mesh both on and off | | |
| Print Stats: no robot, loose SCARA, near-singularity SCARA, and acceleration-plus-robot cases produce monotonic timing; report says `robot-limited` only when at least one segment was capped | | |
| Print Stats G-code: the first segment and arc interiors participate in robot timing; volume and layer totals remain identical to the non-robot result | | |
| UR Program: legacy model selector remains usable when Robot Definition is hidden; enabling the group and wiring a UR preset selects the same model without shifting old sockets | | |
| Icons and registration: Robot Definition, Print Bed, and Robot Simulation load in all target frameworks and appear once in the intended ribbon groups | | |
| Yak package: inspect contents after a dirty Debug build; no `Desktop`, `True`, `False`, test output, or local-path artifact is present | | |

## Extra test rows for 2.2.0 — generic Duet control

The Cartesian gate logic (JogPlanner / JobGate `mechanism`, `CartesianChecks`) is unit-tested
in `CartesianDuetGateTests`, but everything session- and hardware-facing below is not
verifiable without a machine. **The Potterbot rows above must be re-run too — the SCARA path
is designed to be bit-identical with the robot input unwired, and that claim needs the
hardware to confirm it.**

| Check | Status | Notes |
|------|:------:|-------|
| Potterbot regression: with the new `robot` input unwired, Duet Jog / Print / Live behave exactly as 2.1.0 (same gates, same refusal texts, same mirrored coordinates, arm configuration still required) | | |
| Duet Jog + generic-duet-cartesian on a Cartesian Duet: jog works with no verified-home arm-configuration requirement; refuses beyond the M208 envelope; envelope values come from the firmware report when the limit sliders are unwired; jog is refused with "envelope unknown" before the first M208 report arrives | | |
| Duet Jog + generic-duet-cartesian: displayed position equals the machine's own coordinates (no y = -x mirror); Set bed origin captures unmirrored values | | |
| Duet Jog + a `none` / UR / generic-cartesian (non-Duet) definition: every action capsule is disabled with the profile error; E-STOP and Reset stay live | | |
| Duet Print + generic-duet-cartesian: Validate/Upload/Start work against the M208 envelope; a SCARA-only refusal (dead zone, arm configuration) never appears; per-axis `G28 X/Y/Z` in the job validates (2.3.0); the same job through the unwired SCARA path is still refused at parse | | |
| Duet Print + generic-duet-cartesian against the lab's SCARA board: upload/start refused with "firmware reports SCARA (M669) kinematics" | | |
| Duet Live + generic-duet-cartesian: no arm drawn, no reach annulus, whole plate printable, trail and layer preview in the machine's own frame; switching the profile mid-session rebuilds trail/path/preview in the new frame | | |
| Homing on a Cartesian Duet: Home all / per-axis homes verify axis flags without touching homeall.g or reporting an arm-configuration caveat | | |

## Extra test rows for 2.3.0 — DoE inference, front, power, plots, curve editor

Core math is unit-tested with hand-derived fixtures (`RsmInferenceTests`, `ParetoTests`,
`PowerAnalysisTests`); everything below is canvas surface the Rhino-free suite cannot see.

| Check | Status | Notes |
|------|:------:|-------|
| Pre-2.3.0 `.gh` with DoE Design / RSM Evaluate / Desirability opens with every wire on its original socket; the new inputs appear only when their groups are enabled | | |
| RSM Evaluate: `diag` output lists leverage/Cook's D/VIF per response; a deliberately corrupted run raises the Cook's D warning on the component | | |
| Desirability: `interval` output branches match responses; alternatives whose predictions sit inside the PI show `≈best` in the report | | |
| Desirability + pareto group: 2 goals draw the front scatter with the D-optimum ring; 3+ goals draw a text note and no scatter; `paretoOn` off leaves solve time unchanged | | |
| DoE Design + power group: sigma + effect produce the per-factor power block; power < 0.8 warns; missing sigma/effect remarks instead of failing | | |
| All DoE panels legible on BOTH light and dark canvas (rebuild via Compute after switching the theme); no white flash behind the panel on dark | | |
| Goal-curve strip (prototype): appears after first solve; dragging Low/High/Target/shape updates the curve live without re-solving; releasing marks Compute stale; ✕ resets; the override survives save/reopen and is announced as a Remark | | |
| Per-axis `G28 X` job on Robot Simulation / Print Stats / Duet Live: parses under a Cartesian definition, refused under SCARA/none | | |

## Extra test rows for 2.4.0 — Graph Mapper

Curve math is unit-tested (`GraphMapTests`); the strip's mechanics are canvas surface.

| Check | Status | Notes |
|------|:------:|-------|
| Graph Mapper drops with the strip live immediately; every graph type draws and its grips drag (Linear, Bézier incl. control polygon, Power, Sine, Parabola, Gaussian, Sigmoid, Perlin incl. ⚄ reseed) | | |
| Ctrl snaps grips to 0.05, Shift to 0.01; the readout shows exact grip t/y while dragging; hover highlights a grip with its label | | |
| ctrl+Z after a drag restores the pre-drag curve; ctrl+Z after a type switch restores the previous type; ↺ resets only the current type | | |
| Switching graph type and back restores the previous parameters (per-family memory); save/reopen preserves curve, type, and memory | | |
| Wired numbers appear as ticks on both axes and the `y` output matches the curve; domain group remaps `[inMin,inMax] → [outMin,outMax]`; `clip` off lets out-of-domain inputs pin to the graph edges | | |
| Runtime-drawn icon renders on the ribbon at both UI scales; once `IamGraphMapper.svg` goes through `make_icons.ps1`, swap the component to the PNG and re-check | | |
| Desirability goal strip: ctrl+Z now restores a pre-drag goal (undo hook added in 2.4.0) | | |

## Extra test rows for 2.5.0 — Graph Mapper customizability

Curve math incl. the custom families is unit-tested (`GraphMapTests`, 20); the gestures
and the capsule styling are canvas surface.

| Check | Status | Notes |
|------|:------:|-------|
| Polyline/Spline: double-click empty plot adds a point at the cursor, double-click a grip removes it, removal refuses at 2 points; every point drags with t clamped between neighbours; ctrl+Z undoes add/remove/move | | |
| Spline passes visibly through every point with no overshoot between monotone points | | |
| Convert → Polyline/Spline samples the current curve (e.g. Power e=2 converts to a curve through (0.5, 0.25)); afterwards the dropdown hides the convert entries; switching back to the analytic family restores its parameters | | |
| Conic / Exponential / Steps: single grip lands where dropped (bisection inverse); Steps quantizes into the shown tread count; Exponential k<0 bends log-like | | |
| Strip renders on a GH capsule (rounded, shadowed, skin-aware) at ~190 px, centred under the component; Desirability's goal strip keeps its 372 px card alignment and also sits on a capsule | | |
| 2.4.0-saved Graph Mapper `.gh` reopens with curve, type and memory intact; a custom curve with added points survives save/reopen | | |
| Dark canvas: capsule face + plot fill + grips legible after a Compute on the new theme | | |

Maintainer contact: Jasper Vollmert — jasper.vollmert@tuhh.de (IDAC @ TUHH).

## Extra test rows for 2.6.0 — units, culture/CSV, async I/O, Duet trust

| # | Check | OK? | Notes |
|---|---|---|---|
| U1 | Millimetre document: slice → place → G-code on a known definition; output text byte-identical to 2.5.0 (same geometry, same settings) | | |
| U2 | INCH document: slice a 4-inch cylinder (layer height 3 mm) — layer count matches the mm equivalent (~33 layers for 101.6 mm), previews overlay the doc geometry, G-code coordinates are mm | | |
| U3 | METRE document: same check at 0.1016 m; Place lands the part at the bed's print centre; Robot Simulation draws bed/arm at sensible size | | |
| U4 | Change document units mid-session (mm→inch): Heavy components show "no cached result - press Compute" instead of replaying the stale-scale result | | |
| C1 | Windows set to German (de-DE): generate Marlin + RRF G-code — decimal POINTS everywhere, firmware preview parses | | |
| C2 | RSM Evaluate: read an English CSV (`1.5,2`) and a German CSV (`1,5;2`) — identical numbers; a mixed file (`1,5;2` + `0.25;3`) errors naming the line | | |
| C3 | Pump group: German semicolon calibration CSV loads; `1,5,2,5`-style comma file is refused, not misread | | |
| A1 | G-code Save to a valid folder: remark "writing …", then written=true on the follow-up solve; file content correct | | |
| A2 | G-code Save to an unreachable path (e.g. dead share): canvas stays responsive, error appears when the write fails, last good outputs kept | | |
| A3 | Restart with gcodePath: first solve shows "loading …", then the plan; edit the file externally → re-solve reloads once | | |
| A4 | Close the document while a save is pending: no crash, no zombie write afterwards | | |
| V3 | Grasshopper's own plug-in info dialog and the .gha file properties both show 2.6.0 | | |
| T1 **needs hardware** | Duet Connect to the Potterbot: first connection logs "machine identity recorded: …"; save, reopen, reconnect — identity trusted, jog/print gates behave as before | | |
| T2 **needs hardware** | Simulate a mismatch (edit the saved fingerprint string in the .gh, or point at a different board): every action button shows the identity refusal, E-STOP stays live, "Trust connected machine" on the Connect menu recovers | | |
| T3 **needs hardware** | Verified home + one reduced-speed jog after trusting — unchanged from 2.5.0 behaviour | | |
| T4 **needs hardware** | Two Connect components (e.g. two open documents) with different saved fingerprints on the same host: both show "MACHINE IDENTITY CONFLICT", state-changing actions refuse, E-STOP stays live; "Trust connected machine" clears it and both documents adopt the accepted identity on their next solve | | |

## Extra test rows for 3.0.0 readiness (2026-09-07: units, exports, console shell, live viewport, logs, Pareto Select)

Nothing below has run in Rhino yet. Run the contract gate with the selector allowed:
`tools/verify-rhino-contracts.ps1 -Framework net48 -AllowedAdditions a1a2c3d4-0030-4beb-9c3d-aa0000000030`
(repeat for `net7.0` and `net7.0-windows`), then the offline probe
`-Probe pareto-selection`. The probe does not connect to a machine.

| # | Check | OK? | Notes |
|---|---|---|---|
| CU2 | Console go-to targets in an inch document are millimetre machine targets (log line `console point N: rhino ... -> machine ...`) | | |
| DX1 | DoE Design xlsx + csv export: `exporting...` then `saved:` on the next solve; unreachable share keeps the canvas responsive and reports once; power group on → `Power` sheet present, `DoE_Plan` identical to an export without it | | |
| DX2 | Desirability xlsx export the same way; deleting the component mid-export leaves no error after | | |
| CP1 | Console **Validate** works with no session (disconnected) and with an untrusted machine; **Upload** stays refused with its reason beside it | | |
| CP2 | `dryRun` on → Start reads *Start (dry M37)* on Job and Dashboard; pressing it sends M37 (DWC shows simulating) | | |
| CP3 | Wire > 6 calibration points: paging reaches the last point; *selected:* stays visible when paged away; replacing the list clears the selection | | |
| CP4 | Hold a jog, then: switch page / Alt-Tab away / hide the window / open the per-axis home dialog - the repeat stops after at most one further step each time; a fresh press is needed to continue | | |
| CP5 | OpenGL opt-in on a GL-less host: window falls back to CPU, footer tooltip names the reason, everything clickable | | |
| CP6 | Terminal `M999` while idle: refused with *not halted or off*, both before and (E-STOP then type) at the transaction | | |
| SH1 | Window opens on **Dashboard**; readiness/position/job/logging cards agree with DWC; *Next* proposes Connect → Home all → Validate → Upload → Start in that order as the state advances, and never fires by itself | | |
| SH2 | Every page at 820×620, 960×700 and maximised, at 100/125/150 % scaling: no clipped labels, every drawn control clickable, hand cursor over enabled controls only, refusal text readable | | |
| SH3 | Hover/press/navigation animations stop within a second of the last interaction (no CPU use at rest); **Reduce console motion** removes them; pending spinner while an action runs | | |
| SH4 | Terminal: armed strip names the exact command and counts down; editing the line disarms | | |
| SH5 | Reopen the window: bounds restored, page is Dashboard again | | |
| LV1 | Duet Live during a print with the GH editor open, minimised and closed: the arm moves smoothly, no canvas solve per frame (watch the component: no flicker, no downstream re-solves with automatic outputs off) | | record fps/degraded state from the component's `info` |
| LV2 | Label reads *smoothed - delay N ms, sample age M ms* while moving, *observed* at rest, *STALE* after pulling the network; a reconnect shows the arm only after fresh samples; homing hides the arm | | |
| LV3 | Old (pre-3.0.0) definition: automatic outputs on, at most one re-solve per second; new component: outputs update only on explicit solves until the menu item is switched on | | |
| LV4 | Deviation overlay with the job G-code wired: trail colours purple→yellow with the chosen legend, grey where no comparison exists, dimmer on crossings; replacing the G-code resets the colours | | |
| LV5 | Two Duet Live components on one host, one hidden/locked: only the visible one animates; deleting a component stops its display; closing the document leaves no timer running | | |
| LG1 | First print after install: Logs page shows *Temporary recording* with the job name; after the job, *Temporary complete*; restart Rhino cleanly - the record is gone | | |
| LG2 | **Save this job** during a print (choose a folder first): destination gets `print.csv.partial` growing, then `print.csv` + `events.log` + `manifest.json` at job end; staging folder removed | | |
| LG3 | **Keep all job logs** with a network folder; pull the network mid-print: *Waiting to sync*, machine control unaffected; restore and **Retry**: no duplicated rows in the destination CSV (compare line counts with the staged copy) | | |
| LG4 | **Change location** mid-sync: the whole buffer appears at the new place; the old place keeps only its `.partial` | | |
| LG5 | Kill Rhino during a print; restart: a *Recovered temporary log* is listed; save it; it becomes Saved | | |
| LG6 | Corrupt `manifest.json` by hand; restart: the job folder is moved under `quarantine/` and a note is shown, nothing deleted | | |
| LG7 | Fill the local disk (or make the staging folder read-only): a red *Logging fault*, the job never shows Saved, E-STOP/Reset/jogging still work | | |
| P50-1 | Console header, Dashboard and Job page during a Lugworm job read `NN%  layer N/M` with M the file's layer count; a job started from DWC shows `layer N` (or `N/M` when the firmware reports a count); after the job the total is gone. Header text fits beside the bar at 820×620 | | 5.0.0 |
| P50-2 | Live View through a print longer than 30 min with three pauses: the start of the print stays drawn, `info` names the retention bound (≤ 5 mm), no lift/park spike at any pause | | 5.0.0 |
| P50-3 | Slice a part with a hole and a concave outline, zigzag infill: rows join along the offset boundary at concave edges and near the hole's top and bottom; no infill line crosses the hole; no edge carries a doubled bead in the preview | | 5.0.0 |
| P50-4 | Legend: `⇤ inputs` gives the 35-entry grouped list; a Panel with `14` draws coolwarm; `npg` warns and draws blues; open `pump_calibration_placeholder.xlsx` in Excel: chart, formulas and Check column work, no brand text | | 5.0.0 |
| PS1 | Desirability with `pareto` on, two goals, its `objectives` output wired to `objectiveNames` → Pareto Select strip shows the scatter with the optimum marked; click a point: outputs match the branch; **Pick…** window walks with arrow keys | | |
| PS2 | Three goals → parallel coordinates; hover names the recipe; the zero-D extremes are selectable | | |
| PS3 | Save, close, reopen with Desirability disabled: same `settings/di/D`, plot labelled archived, `current` false, no window | | probe `pareto-selection` covers the archive path offline |
| PS4 | Change a goal weight and recompute: warning *front changed*, stored numbers unchanged; click the dashed candidate: current again | | |
| PS5 | Wire `key` back into `selectionKey`: current; wire a key saved from another front: refused, no fallback; `selectionIndex` unchanged across a new front: not re-applied | | |
| PS6 | Wire `weights` = `1, 1` on a two-goal front: a green square on the recipe with the greatest geometric mean of its two d's, legend *best for w=1,1 (…, response goals only)*; change to `1, 4`: the square walks toward the second goal's extreme, nothing upstream recomputes, the stored selection and `D` output are unchanged; three weights on a two-goal front, or a negative one: warning *Weights not applied*, no square; `0, 1`: the square sits on the second objective's argmax even if that recipe has `d1 = 0`; disconnect: square gone, no warning | | |
| PS6 | Ctrl+Z after a click restores the previous recipe; copy/paste gives an independent copy; two documents do not share a picker window | | |
| PS7 | Hover and select a recipe in the picker window: a fixed line below the plot shows predictions and PI half-widths in response units; no reliability text overlays the plot; save/reopen retains it | written, not executed | probe extended |
| PS8 | With search bounds beyond tested recipes, points beyond maximum training leverage are hollow; supported points are filled; the status adds outside tested region | written, not executed | needs Rhino |
| PK1 | `tools/release.ps1` candidate mode: `licenses/` holds the Skia/OpenTK upstream files + `PROVENANCE.txt`, `BUILD-INFO.txt` names the commit and SDK, `dist/HASHES-<version>.txt` written; root `THIRD-PARTY-NOTICES.md` SHA-256 unchanged (`25CFE048...`) | | |

## Extra test rows for 4.0.0 (thermoplastic + Klipper, Legend, settings files, spline handles)

**The 4.0.0 protocol was its own file (`FINAL-TEST-ROUND_v4.0.0.md`, archived outside the repository on 2026-09-24).**
It is not a summary of this one — it is the release gate, with the candidate package, the
ribbon, saved-file compatibility, the Potterbot Duet regression (section 8) and the first
Klipper/Moonraker bring-up (section 9) as numbered rows. Run it there rather than adding rows
here.

What is new and therefore worth knowing before you start:

- **Since 4.1.0, Klipper is a file-only G-code dialect and the console has no thermals
  surface** - direct Klipper connection, `MoonrakerClient` and the Thermals tab were all
  removed; see `docs/2-changelog/w10_v4.1.0.md`.
- **The Duet path is claimed byte-identical** through the `IMachineClient` extraction. That
  claim is backed by green Core tests and an unmodified fake board, which is exactly the
  evidence that was green in 1.9.0 and 1.12.0 while the plugin layer was wrong. Section 8 is
  the only thing that turns it into a fact.
- The contract gate now needs allowed additions, removals **and** changes; the exact
  invocation is in round section 10 and in `ARCHI.md` §21. A **v4 baseline is owed** and must
  be captured only after the round passes.

The two offline Rhino probes (`-Probe pareto-selection`, `-Probe interlayer-time`)
are still unexecuted on a host. They connect to nothing and are
cheap; run them with the contract gate.
