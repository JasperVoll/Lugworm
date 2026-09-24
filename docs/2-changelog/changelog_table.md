# Changelog Table

Entries before 0.1.0-beta cite planning, review and test-round records under `docs/dev`
and `docs/superpowers` that were archived outside the repository on 2026-09-24; those
citations are kept as history and no longer resolve.

| Version | Week | Commit Message                  |
| ------- | ---- | ------------------------------- |
| `0.1.0-beta` | 39 | feat!: Lugworm - first public baseline. Rename from IAMLab Slicer (tab, package `lugworm`, namespaces, folders; no GUID, nickname, socket or serialised key moved), Duet Console name restored, 35 numbered legend palettes, console progress text, trail retention; stabilisation pass (console lease/reopen/disconnect state, export of saved logs, DPI-scaled window, IPv6 host keys, Slice firstLayer 0, Print Stats travelFeed and input validation, contract gate and icon manifest counts, versions aligned in both shipped assemblies). Worked on as 5.0.0 while private (2026-09-23) |
| `4.4.0` | 10   | fix/refactor: debugging and hygiene round - spiral and Fermat for hollow sections (nesting by containment), offset rings thinned to a bounded vertex count, bricklayer interlocks round parts, non-planar output thinned after modulation, glitch filter returns a view instead of mutating the poll, culture-invariant Core, typed and justified catches with four component failures made visible, SlicePipeline/ActionGatePolicy/SettingsPlan in Core, Num and LayerBuildOptions helpers, dead public API removed, analyzer policy with .editorconfig at 0 warnings, load-scaled perf budgets, 250 new tests, graphify cache untracked, stale docs removed, ARCHI compacted to 15.2k words with 17 verbatim reference notes (2026-09-15) |
| `4.3.0` | 10   | feat: stable infill seams (+X centroid ray from every wall's own centroid, exact split points in spiral/Fermat/chain/helix, silent ring fall-back reported), IAMLab Place orients open curves before seaming (wall-side gate, seam target, layer below, +X), non-planar wall waves with a quarter-turn phase per loop and a real per-loop gap guard after the bricklayer shift, zigzag/wave connector guard tolerance (one polyline per layer on round parts again) (2026-09-14) |
| `4.2.1` | 10   | fix: slicing review round - conical bricklayer adjacency measured in XY, bricklayer shift and non-planar amplitude bound to the actual adaptive layer gaps, null/degenerate infill rings skipped, G-code Restart's point documented in the Rhino frame, release script keeps its dotnet diagnostics and ships INSTALL/UPGRADE (2026-09-10) |
| `4.2.0` | 10   | feat: DIN EN / ASTM bending and compression test specimens plus a zig-zag extrusion test path in IAMLab Test Geometry, with a redrawn three-point-bending icon (2026-09-10) |
| `4.1.0` | 10   | feat!: LDM-only retrenchment - thermoplastic printing, the console Thermals page and the direct Klipper (Moonraker) connection removed, Klipper kept as a G-code dialect; Bricklayer interlocking walls, non-planar infill, sine/graph wave infill, Graph Mapper map output (2026-09-09) |
| `4.0.0` | 10   | feat!: thermoplastic printing and Klipper machine control (Moonraker), IAMLab Legend, settings files (IAMLab Load Settings), Graph Mapper spline handles, Interlayer Time layer scrolling and shading; UR support retired (2026-09-07) |
| `3.0.0` | 9    | feat: road to publication M2-M5a — calibration units, background DoE exports, console parity fixes, 60 fps live viewport with deviation overlay, job-log retention, console command shell, IAMLab Pareto Select, DoE report wording + power sheet, license/provenance payload (2026-09-07) |
| `3.0.0` | 9    | test: shipping-runtime readiness follow-up — focused net48/net7.0/net7.0-windows tests, source-export coverage gate and Rhino component-contract/archive baseline (2026-09-07) |
| `3.0.0` | 9    | feat!: print-console consolidation - one IAMLab Duet Console replaces Duet Connect/Status/Jog/Terminal/Print (hidden, not removed); Skia-rendered window with a native E-STOP/Reset footer, press-and-hold jogging, immediate post-command polling |
| `2.6.0` | 8    | fix/feat: release-readiness hardening - document units, invariant culture + CSV detection, Duet transport & identity trust, async file I/O, reproducible packaging, IAMLab Version component |
| `2.5.0` | 8    | feat: Graph Mapper customizability - custom point curves (add/remove/convert), Conic/Exponential/Steps, GH-capsule styling at half width |
| `2.4.0` | 8    | feat: IAMLab Graph Mapper - stock functionality, improved mechanics (undo, snapping, readout, data ticks, family memory) |
| `2.3.0` | 8    | feat: DoE inference pack, Pareto front, MC power; one plot system; goal-curve prototype; per-axis G28 by kinematics |
| `2.2.0` | 8    | feat: generic Duet control - profile-driven gates for Cartesian Duet machines |
| `2.1.0` | 8    | feat: Print Bed definition, robot preview fix, robot-aware print time |
| `2.0.0` | 8    | feat: robot-agnostic architecture - Robot Definition, unified Robot Simulation, slicer-only use |
| `1.13.0` | 7    | fix: Rhino design frame as ground truth, drawn-assembly corrections, homeall.g macro calls |
| `1.12.0` | 7    | fix: SCARA arm mode from the homing endstops + visible control-gate refusals |
| `1.11.0` | 7    | feat: firmware motion-limit checks at the hardware gate + G-code text output removal |
| `1.10.0` | 6    | feat: timestamped print path log with layer boundaries, speed telemetry and streaming CSV |
| `1.9.0` | 6    | feat: SCARA workspace envelope, RRF-faithful IK and verified homing + publish-readiness pass |
| `1.8.0` | 1    | feat: live print view component + lab-session fixes (button labels, extrusion preview to 100%, 10mm defaults, expert-unlock visibility, restart Compute gate, SCARA firmware-limit sentinels) |
| `1.7.1` | 1    | fix: retraction/Rdp/save hardening + release prep (license, attribution, metadata, doc refresh) |
| `1.7.0` | 1    | feat: heavy-use hardening — fail-closed machine safety, correctness, performance, UX polish |
| `1.6.1` | 1    | chore: initialize TRIP workflow |

# Changelog Summary

- **v4.3.0 (Stable Infill Seams, Place Orientation, Non-planar Walls - Week 10, 14-09-2026)**:
  - **Seams**: every closed wall re-started where the +X ray from its own area centroid crosses it (`SeamAnchor`, new); spiral, Fermat, chained rings and the spiralize helix use exact projected split points instead of nearest vertices; with one wall the seam was RDP's farthest-from-centroid vertex - a coin toss per layer on round parts, which is why spiral/Fermat infill differed on every layer
  - **Diagnostics**: spiral/Fermat fall-back to independent rings (islands) reported through `LayerPaths.Diagnostic` into Slice `info`, no longer silent
  - **Place**: open curves oriented before the wall seams in every mode except Keep - an end far from every wall never starts, the end nearer the seam target starts, near-ties follow the layer below, first layer +X; Nearest seams all walls next to the first open curve's start; `info` reports `open flipped`
  - **Zigzag/wave**: pre-existing connector-guard defect fixed - Clipper-rounded row ends counted as boundary crossings on any curved or oblique boundary (22 polylines for 23 rows on a circle); a crossing now needs a 1e-3 mm penetration on both connector ends
  - **Non-planar walls**: `npWallAmp`, `npLoopPhase` (quarter turn per loop), `npWallOuter` appended to the nonplanar group; clean-room from the GPL-3.0 BrickLayers family's behaviour, credited; wall guard uses the real per-loop gap after the bricklayer shift (`TaggedPath.ZShift` recorded by `Bricklayer.Apply`)
  - **Also**: `wallDistance`/`infillDistance`, `alternateInfill`, `Helix` extracted, per-component test sheet (previous round, committed here)
  - **Tests**: 1776/1776 passing (44 new), 0 warnings, all three TFMs; Codex review 2 rounds, APPROVED with observations; Rhino rows SLICE-28..32 / PLACE-01..04 and the v4.3 contract capture owed

- **v2.6.0 (Release-Readiness Hardening - Week 8, 01-09-2026)**:
  - **Units**: document-unit adapter at the Rhino/Core boundary - inch/metre documents slice, place and preview at correct physical size; mm documents byte-identical; Core stays mm-only
  - **Culture**: every machine-text site invariant, de-DE fixture pins Marlin/RRF/restart/Duet output; file-level English/German CSV detection refuses mixed/ambiguous tables with the offending line
  - **Duet**: streamed 4 MiB response cap under the request timeout, downloads fully time-bounded, non-finite JSON refused on every TFM, and per-connection machine-identity trust (persisted fingerprint, first-connection baseline, mismatch = diagnostic-only until explicitly trusted)
  - **Components**: file reads/writes moved off the solver thread (background workers, last-good replay, preserved rising-edge semantics); new IAMLab Version component (assembly-sourced version/build/runtime, strictly opt-in bounded update check)
  - **Release**: Clipper2 rebuilt from exact upstream source (NuGet binary embedded a foreign local path), .3dm revision-history usernames neutralized (UTF-16 leak invisible to ASCII scans), tools/release.ps1 candidate/public packaging with allowlisted staging + ASCII/UTF-16 PII scans, provenance and authority blockers closed
  - **Tests**: 947/947 passing (11 perf), 0 warnings, all three TFMs

- **v2.5.0 (Graph Mapper Customizability - Week 8, 31-08-2026)**:
  - **New**: custom point-based families - Polyline and Spline (Fritsch–Carlson monotone cubic: through every point, no overshoot) with variable-length point vectors; double-click adds a point, double-click a grip removes it (min 2), drags keep the list sorted; dropdown converts any analytic family into them by sampling
  - **New families**: Conic (superellipse, a=2 circle), Exponential (k→0 identity, k<0 log-like), Steps (2-20 treads); single-grip families place their grip by bisection-inverting the curve
  - **Design**: strip on a GH_Capsule face (native Grasshopper look, shared by all ICanvasStrip hosts), width halved to 190 px and centred under the component; theme plot-panel fill
  - **Fixed**: per-family memory now restores variable-length custom curves
  - **Tests**: 845/845 passing (11 perf), 0 warnings

- **v2.4.0 (Graph Mapper - Week 8, 31-08-2026)**:
  - **New**: "IAMLab Graph Mapper" (1 Slice, GUID index 0027) - remaps numbers through an on-canvas curve like the stock Graph Mapper, families Linear / Bézier / Power / Sine / Parabola / Gaussian / Sigmoid / Perlin, Core-tested curve math (`GraphMapTests`)
  - **Improved mechanics vs stock**: real ctrl+Z undo (also retrofitted to the 2.3.0 goal strip), Ctrl/Shift grid snapping, live numeric readout, hover-labelled grips, wired data as axis ticks, per-family parameter memory, wireable input/output domains, equation output
  - **Infra**: `ICanvasStrip` extracted so IamAttributes hosts any interactive strip; runtime-drawn icon + committed SVG for the icon pipeline
  - **Tests**: 837/837 passing (11 perf), 0 warnings

- **v2.3.0 (DoE Statistics & Visual System - Week 8, 31-08-2026)**:
  - **New**: inference pack - 95% prediction intervals (Desirability `interval` output; alternatives inside the PI marked `≈best`), Cook's D / leverage / VIF (RSM Evaluate `diag` output + warnings). Hand-derived fixtures; covariance serialized additively into model JSON
  - **New**: Pareto front on Desirability (`pareto` group + appended tree output, report section, 2-objective scatter with the D-optimum ringed; off by default). `alternatives` correctly relabelled as scalarised-D local optima
  - **New**: Monte-Carlo power on DoE Design (`power` group + output) - per-factor detection probability before printing, refit with the shipped fitter so DSD/Taguchi aliasing is honest
  - **New**: `IamPlot` - one palette/type/axis/legend system for all DoE panels, light + dark canvas aware; renderer rebuilt on it
  - **Prototype**: on-canvas goal-curve editor on Desirability - drag Low/High/Target/shape instead of typing; per-response overrides, save/reopen safe, ✕ resets
  - **Changed**: per-axis `G28 X/Y/Z` in job files now follows the kinematics via the machine profile - still refused for SCARA, allowed for Cartesian Duet profiles (maintainer decision); bare-letter G28 grammar accepted there
  - **Fixed/cosmetic**: dead jog `#if false` block and dead preview stores removed; M669 noise suppressed for Cartesian sessions; Duet Live neutral bed default for Cartesian; zOffset single-source design documented in ARCHI §11
  - **Tests**: 825/825 passing (11 perf), 0 warnings

- **v2.2.0 (Generic Duet Control - Week 8, 31-08-2026)**:
  - **New**: Duet Jog, Duet Print and Duet Live accept the Robot Definition (optional `robot` input, appended feature group). Unwired = Potterbot SCARA, bit-identical to 2.1.0; `generic-duet-cartesian` gates on the M208 envelope via new `CartesianChecks` (endpoints + sampled arc interiors + feed; no IK, no reach annulus, no arm configuration, no frame mirror)
  - **Envelope source**: unwired limit inputs defer to the firmware's own M208 report; jog/validate refuse with "machine envelope unknown" until it arrives - the Potterbot literals are never inherited by a generic machine
  - **Hardware gate**: Cartesian upload/start requires per-axis M208 verification and refuses a board whose `move.kinematics` parsed as SCARA; profiles without Duet control (`none`, generic-cartesian) block every planned action, E-STOP/Reset stay live
  - **Unchanged**: SCARA path (mechanism defaults to `Scara` everywhere), per-axis G28 job-file refusal, Calib Points / Scara Fit, all shipped GUIDs
  - **Tests**: 800/800 passing (13 new `CartesianDuetGateTests`), 0 warnings

- **v2.1.0 (Print Bed, Robot Preview & Robot-Aware Stats - Week 8, 31-08-2026)**:
  - **New**: "IAMLab Print Bed" component (2 Place) - one `iamlab-bed:v1:...` definition replaces bed size/position duplicated across Place, Robot Simulation and Duet Live. Plate centre and print centre stay separate quantities; reach stays the robot's, with the bed carrying optional overrides only
  - **Fixes**: robot preview in Robot Simulation - the 2.0.0 unification inherited the previous simulator's lit `DrawMeshShaded`, so the coincident faces of the shipped assemblies z-fought and no silhouette was drawn. Restored the flat/unlit `CreateMonotoneMesh` + `DrawMeshFalseColors` + feature-edge technique for both mechanisms, and gave it the cached base-mesh edge extraction it never had
  - **Fixes**: `make_icons.ps1` died on the first icon - Edge's stderr became a terminating error under PS 5.1 with `$ErrorActionPreference='Stop'`; relaxed around the native call only, PNG validation unchanged
  - **New**: robot-aware print time - Print Stats gains a `robot` input; with a kinematic profile each segment's feed is capped by inverting the `joint_speed = (Δjoint / Δcartesian) - feed` relation `RobotChecks` already enforces, so singularities and the reach boundary cost time instead of being invisible. Reported alongside the Cartesian numbers, not instead of them; `robot = none` is bit-identical to before
  - **Fixes**: G-code preview and output disagreed on height. `GcodeComponent` passed `ZOffset = 4.0` (Core's default is 0.0), double-counting a layer height the slicer had already applied, so a 4 mm layer printed at Z8; and `PrintPolys`/`TravelPolys` were collected at the raw geometry Z while every emitter writes `Z + zoff`, so preview could not match output at any non-zero offset. Component default is now 0.0 and the previews carry the commanded Z. **Lowers the nozzle 4 mm where `zOffset` is unwired - check Z before the next print**
  - **New**: Robot Simulation colours travel apart from deposition - beads RoyalBlue width 2, travel ochre width 1, both from `ExtrusionHistory` (`VisibleRuns` + new complementary `TravelRuns`) so wires and bead mesh cannot disagree. Travel now draws with the bead mesh on too, since no mesh can represent it
  - **Also**: `IamComponentBase.IsWired(name)`; shared `MeshFeatureEdges` extractor
  - **Fixed**: RobotDefinition/RobotSimulation had reused GUID index tails 18/19 already held by DuetTerminal/DuetPrintJob - distinct GUIDs but an ambiguous scheme; renumbered to 0025/0026 while both were still unreleased, so indices 1-26 are now gapless and unique
  - **Tests**: 778/778 passing, 0 warnings

- **v2.0.0 (Robot-Agnostic Architecture - Week 8, 31-08-2026)**:
  - **New**: "IAMLab Robot Definition" component (4 Robot) - one `iamlab-robot:v1:<id>` string selects the machine for every robot-dependent component; profiles `none`, `generic-cartesian`, `generic-duet-cartesian`, `potterbot-scara-v4`, plus three arm profiles retired in 4.0.0. "IAMLab Robot Simulation" (4 Robot) replaces the separate per-machine simulators; the old two are hidden but still deserialize
  - **Slicer-only use**: slicing, placement, extrusion, G-code, bead preview and print stats all run with no robot selected; Robot Simulation performs no IK and reports `reachOK` true for `none`
  - **Fixes**: continuous extrusion preview - the cache's incremental append welded a separately-capped delta mesh per scrub tick, so a continuous print rendered as many detached instances; the visible prefix is now always rebuilt as maximal runs via Rhino-free `ExtrusionHistory.VisibleRuns`
  - **Separation**: coordinate frame is data (`GcodeConfig.CoordinateFrame`, from the definition) rather than a constant; extrusion quantity extracted to `Core/Extrusion/RamExtrusion.cs`; Place no longer duplicates the SCARA reach check; generic tooltips de-SCARA'd
  - **Unchanged by design**: all shipped component GUIDs, every numerical solver (`ScaraKinematics`/`RobotChecks`, `UrKinematics`/`UrChecks`), and the whole Duet control/safety layer
  - **Known limitations**: direct Duet control (7 Print) still gates on the Potterbot SCARA limits and does not read the definition - non-SCARA Duet machines get G-code generation, not direct control (the Robot Definition component says so on the canvas); and an unwired `robot` input on the G-code generator keeps the legacy Potterbot frame, so pre-2.0.0 definitions are not silently mirrored
  - **Tests**: 732/732 passing, 0 warnings

- **v1.13.0 (Rhino Frame as Ground Truth - Week 7, 26-08-2026)**:
  - **The two frames are mirrored, not aligned**: the firmware reports the post-home pose as `X200 Y-500 Z400`; the operator sees that point at Rhino `X500 Y-200 Z400`. That is a reflection about `y = -x` - no rotation maps one to the other - and the plugin had treated the frames as identical. New `Core/Geometry/MachineFrame.cs` is the single definition; it is its own inverse and keeps zero positive
  - **Rhino is ground truth** (maintainer directive): anything that *validates or commands* the machine keeps the machine's own numbers so it can be audited against `config.g` - kinematics, joint ranges, the M208 envelope, `GcodeParser`, `JobGate`, `JogPlanner`, G-code text, telemetry. Everything a person draws or reads is Rhino, converted at the component boundary
  - **The map is a reflection**, so orientation flips with it: G-code emission swaps `G2 <-> G3` and `I/J`. `ScaraFit` converts *both* point lists, because a rigid grid model cannot absorb a reflection left in the residual. `Duet Live` still publishes the machine's own `J1`/`J3`/`J2` and draws the true configuration reflected
  - **BREAKING**: bed defaults are now Rhino - centre `(468, -441)`, width 1000 along Rhino X, depth 700 along Rhino Y. Saved definitions keep their old machine-frame values; re-enter them or re-capture `bedOrigin`
  - **The drawn assembly**: J1 swings the whole Z-rail about the pivot together with the proximal arm - only the Fundament is static (confirmed on the machine; an intermediate build had the rail static, which was wrong). And the viewport poses the assembly at *reflected joint values* rather than reflecting the meshes: same joint centres, but no part is drawn inside out, which is what turned the asymmetric Fundament through what looked like 90 degrees. The base placement moved out of the renderer into `robot.xml` as `<BasePlacement>`, confirmed on the machine at `-180`, `(3, 82)`
  - **`Unknown` arm configuration after every home**: the board's `homeall.g` now delegates with `M98 P"0:/sys/homez.g"`, and `HomeMacroReader` rejected the quoted path as an unsupported token - so `PrepareHomeConfigurationAsync` always threw and jog/validate/upload/start stayed refused with no way to satisfy them. The reader follows `M98` calls now, `DuetSession` downloads the include tree first, and modal `G90`/`G91` carries across a call. Still fail-closed on anything unrecognised
  - **The model is pinned**: `robot.xml` has been corrected against the machine four times, and none of those regressions was visible to the FK self-check (a vertical lift commutes with a vertical-axis rotation, so the nozzle lands in the same place whichever link J1 drives). `ShippedRobotModelTests` fails on ANY edit to the shipped definition
  - **Tests**: 693 passing (62 new), 0 warnings. Includes a Rhino -> G-code -> parser -> Rhino round trip, and the board's real home macros as tracked fixtures
  - **Motion, rail rotation and base orientation all confirmed on the machine** 2026-08-26 - see `w7_v1.13.0.md`

- **v1.12.0 (SCARA Arm Mode & Control-Gate Visibility - Week 7, 25-08-2026)**:
  - **Simulation matched the machine at last**: `M114` after a full `G28` measures joints -18.489 / -116.054, i.e. arm mode 1, while every component defaulted `elbow` to 0. Both modes are inside the firmware's A/B limits at that pose, so `InverseRrf` never fell back and nothing errored - the arm was simply drawn mirrored about the origin-to-nozzle line, which reads as a large rotation
  - **The homed arm mode is machine state now**: `<HomePose armMode>` in robot.xml flows to `RobotLimits.HomedArmMode` and seeds every `G28`-anchored walk; `elbow` survives only where nothing anchors the path, and defaults to down. A verified home no longer latches the operator's preference as machine truth
  - **Both seed sites changed, not one**: a conforming job homes before its first move, so the post-home pose is evaluated from the *initial* seed and never reaches the mid-program teleport branch - fixing only the latter would have changed nothing for a real job
  - **Duet Live** uses `InverseRrf` with the tracked configuration; it deliberately gained **no** homed-state check, so an unhomed board still renders the fully extended arm RRF actually reports after a reset. That render is correct, not a bug
  - **Refusals are visible**: a gated control component showed its reason only as a capsule tooltip, so a connected-but-unhomed Print component looked broken - every button dead except E-STOP, no text anywhere. Refusals now surface as runtime messages, severity declared where the reason is produced (safety refusals warn, connection/busy remark, routine prerequisites stay silent), and clear by themselves once the machine is homed
  - **Behaviour change**: `elbow` defaults 0 -> 1, and any `G28`-anchored job now ignores the input entirely. Definitions with a wired or internalised `elbow` keep their value
  - **Tests**: 631 passing (18 new), 0 warnings. No existing test broke - nothing pinned home-anchored arm mode before. The new joint assertions are the machine's measured values, not derived from the code

- **v1.11.0 (Firmware Motion Limits - Week 7, 25-08-2026)**:
  - **Safety**: a job can emit `M201`/`M566` into its header and silently undo a deliberate hardware decision - this lab's accel was cut to `M201 X800` from the vendor's `X3000` because the external drivers stall. Uploads and starts now compare the job text against `0:/sys/config.g`, read once per connection as the immutable baseline
  - **Why config.g, not a live probe**: jobs raise limits and never restore them, so a bare `M203`/`M201` query would read a previous job's raised values as the baseline and permanently disarm the warning
  - **Units**: normalized once at the parse boundary (`M203`/`M566` mm/min, `M201`/`M204` mm/s²). `M203 S1` switches to mm/sec and is honoured on both sides - always dividing by 60 would read `M203 S1 X100` as 1.67 mm/s and miss a 60× raise
  - **Severity**: raised accel/jerk/`M203` warn (refused only with `blockWarnings`); a feedrate over an intact `M203` is informational because RRF clips all axes together, leaving bead geometry unchanged; an unreadable `config.g` or failed probe is unverified and never blocks
  - **BREAKING**: the `gcode_text` output is gone (G-code 11→10 outputs, Restart 6→5), so **every wire after `gcode` shifts up one**. Grasshopper reconnects by index silently - re-check every definition. It was the cause of canvas hangs on large jobs
  - **Fixes**: `M203` allowed in the parser allowlist (the warning path was unreachable without it); `DuetSessionManager.HasOwner` closes a session-ownership hole where a post-disconnect reconnect left components unregistered; `LastActionWarned` stops motion-limit warnings being demoted to grey remarks by a prefix test that could never see them
  - **Tests**: 613 passing, 0 warnings

- **v1.10.0 (Print Path Log - Week 6, 21-08-2026)**:
  - **New**: Duet Live `log` feature group records what the machine actually did - every poll becomes a timestamped record of the real nozzle path with the firmware layer number, print speed and cumulative extrusion, emitted at layer start/end plus every Nth poll and streamed to CSV. Built for interlayer-adhesion analysis (knowing exactly when each layer was printed); that analysis is not part of this release
  - **Why the poll loop**: the old `_trail` sampled inside `Compute` behind coalesced change events, so polls were silently dropped whenever the canvas was busy - survivable for a decorative trail, disqualifying for a measurement. The recorder now rides the poll loop and its logic lives in Core where xUnit reaches it
  - **Telemetry recovered**: `speeds.requested`/`top`, `params.speedFactor`, `filePosition` and `extrRaw` were already in the legacy response and were being discarded; RRF3 adds `move.currentMove`/`move.speedFactor`. Status columns and speed factor are normalized so a CSV filter is not board-dependent (`extrRaw`, not the factor-affected `coords.extr`)
  - **Safety**: `PrintLogSink` moves all disk I/O to a background thread with a bounded queue - a file write has no timeout, and the poll loop keeps the session alive and gate snapshots fresh, so a log folder on a dead network share would otherwise have stalled machine control. Rows are dropped and counted rather than pushing back; the CSV is complete only while `rows dropped: 0`
  - **Testability**: `ObjectModelDiff` extracted from `DuetSession` into Core, so "motion telemetry must stay out of change detection" (or the 1.7.0 redraw storm returns) is pinned by a test instead of a convention
  - **Honest about limits**: on RRF < 3.2 - this lab's board - a cancelled run cannot be distinguished from a clean one by state alone (no cancelling state; use `fraction_printed < 1`); layer timestamps are +/-1 poll, not a hardware trigger; the Rhino smoke test was not run and has a 12-row on-machine protocol; new `COVERAGE-DEBT.md` records the sink queue-drop path and the plugin-assembly gaps
  - **Tests**: 568 passing (54 new), 0 warnings on Debug and Release

- **v1.9.0 (Workspace Envelope, RRF-Faithful IK & Publish Readiness - Week 6, 20-08-2026)**:
  - **Safety**: Cartesian workspace envelope (`XMin/XMax/YMin/YMax` + `CheckEnvelope`) enforced in `CheckPoint`, defaulting to the machine's real negative-Y half-plane - the validator previously knew only a 360deg reach annulus and passed poses the firmware then clipped; `FirmwareLimitCheck` rewritten to signed per-bound comparison with an M669 cross-check
  - **Kinematics**: `InverseRrf`/`TryResolveTheta` emulate RRF's own arm-mode and whole-turn selection; `RobotChecks` became one ordered stateful walk threading `(armMode, thetaTurns)` through every sample, raising `ConfigFlip` on real discontinuities (behaviour change: previously-clean jobs may now report errors the old model could not see)
  - **Homing**: verified against observed state and per-axis `Homed` flags, fail-closed; per-axis `Hm X/Y/Z` verifies only the requested axis so isolated bring-up works; an unreadable `homeall.g` no longer blocks homing, since homing is the recovery path; `/sys` and `/firmware` delete/move protected behind an explicit maintenance override
  - **Fixed in review** (7 Codex rounds, all verified in source): kinematics were never verified on this board and the gate **refused every upload and print start** - three compounding causes, all fixed; a verified home invalidated itself; per-axis homing could never succeed; headerless G-code always errored in the Robot preview; command authorization meant "a wrapper ran" rather than "we submitted motion", so an external DWC pause or cancel passed as ours
  - **Pre-existing bugs**: E-stop left `_busy` set and bricked every jog/Home button; Validate/Upload ran full IK on the GH UI thread and froze the canvas; a stale-snapshot latch disabled buttons
  - **Publish readiness**: `.claude/` git-ignored (2.9 MB of AI session state sat un-ignored in the repo root); serial `SCV4-2373` deliberately kept with the rationale recorded; two-press confirmations now read "double-click ... to confirm"; corrected user-facing text that was wrong (`PrintStats.timeMin` contradicted its own input, a tooltip cited a non-existent file, seven tooltips used a Unicode minus Grasshopper cannot parse); `elbow` was mislabelled "preview-only" though it seeds the home-macro replay
  - **Field diagnosis**: "Home All homes Z only" is a BOARD problem - stock RRF 3.5.1 running a 3.1.1-era config, external drivers stalling at the homing step rate. No slicer change applies; the slicer now surfaces it instead of hiding it
  - **Tests**: 512/512 passing, 0 warnings on Debug and Release. Gap: `DuetSession`/`DuetJogComponent` are in the plugin project, outside the Core suite

- **v1.8.0 (Live View + Lab-Session Fixes, 20-07-2026)**:
  - **New**: "IAMLab Duet Live" component (7 Print) — read-only live viewport: bed + SCARA robot posed at the real nozzle position via IK, travelled-path trail, optional G-code bead preview growing with the Duet's file progress
  - **Fixes**: on-canvas capsule rows size to their labels with refusal reasons moved to hover tooltips; extrusion preview covers the whole path via proportional decimation (no more ~75% cutoff, both scrub directions); bead-width defaults 10 mm (Robot/PrintStats) with 3.5 mm auto layer-height fallback; Terminal expert unlock shows an armed "Confirm send" state; G-code Restart is Heavy (manual Compute, no slider-scrub lag); Print/Status/Live show a live status×progress banner; SCARA M208 X/Y sentinel ranges no longer raise false reach warnings (check direction corrected, tests updated)

- **v1.7.1 (Release Preparation, 17-07-2026)**:
  - **Fixes**: NormalRetraction retracts at every seam (regression test added); `Rdp.Simplify` tolerates degenerate closed rings instead of throwing; dormant-param serialization can no longer fail a document save
  - **Release prep**: MIT LICENSE + THIRD-PARTY-NOTICES (visose/Robots MIT, Clipper2 Boost-1.0, Math.NET MIT, ArcWelder AGPL note); assembly icon + author/copyright metadata; README/INSTALL/UPGRADE/guide refresh (author fields, seven ribbon groups, de-pinned versions); lab-internal files untracked; `docs/dev/RELEASING.md` publish checklist

- **v1.7.0 (Heavy-Use Hardening - Week 1, 16-07-2026)**:
  - **Safety**: fail-closed G-code parser (strict-reject, logical/physical coordinate model, calibrated home-pose contract), segment-interior robot checks wired everywhere, mast clearance blocking, Duet gate-in-transaction (TOCTOU closed), idempotent-only 503 retries, remote content identity before M32, jerk dialect fix (RRF×Marlin ×60)
  - **Correctness**: infill span topology, open-section partition, ring closure, cyclic RDP, skirt/pause/coast builder state, restart offsets + modal snapshot + byte percent, seam lineage, session refcounting/disposal, ScaraFit quality gates, full-circle arcs
  - **Performance**: Z-bucketed slicing, spatial nesting, incremental arc fit, coalesced redraws, poll-loop allocation fixes, streaming transfers, release-gating scale tests
  - **UX**: truthful action messages, gate-derived buttons, accurate bubbles, password transport note
  - **Also**: adopts the Duet control (7 Print) feature work; removes the superseded legacy Python generator tree
  - **Tests**: 449/449 passing, 0 warnings on all target frameworks

- **v1.6.1 (TRIP Initialization - Week 1, 16-07-2026)**:
  - **Setup**: Initialized TRIP workflow docs structure (changelog + testing docs public; plans/review/memo records kept internal)
  - **Documentation**: Generated ARCHI.md documenting the hybrid Grasshopper-plugin + Core-library architecture (24 sections incl. slicing, G-code, SCARA robotics, Duet control, DoE calibration)
  - **Files Added**: docs/ARCHI.md, docs/ARCHI-rules.md, docs/2-changelog/changelog_table.md, docs/4-unit-tests/TESTING.md
