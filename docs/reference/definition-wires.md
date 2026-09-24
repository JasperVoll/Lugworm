# The definition wires and the flows that are not wires

Moved verbatim out of `../ARCHI.md` section 19 during the 4.4.0 documentation
compaction. ARCHI keeps the diagrams and names each wire format with its producers
and consumers; what each wire carries and why lives here.

**The two definition wires.** Both are plain strings, so they cross the canvas like any other value and need no shared object graph:

- **Robot Definition** (`iamlab-robot:v1:<profile>`) feeds G-code (coordinate frame), G-code Restart (the same coordinate frame for its `point` selector and its resume / printed / remaining previews, since 2026-09-16 - before that the Potterbot mirror was applied to every job), Robot Simulation (kinematics + posed model), and Print Stats (joint-speed ceilings, §11).
- **Print Bed** (`iamlab-bed:v1:w=…`, `v2` when the shape is an ellipse) feeds Place (default print centre), Robot Simulation and Live View (plate geometry - since 2026-09-09 the *only* source of it on both; neither draws a plate without the wire). Live View's own reach inputs feed the display IK and were deliberately left untouched. The wire's optional reach fields are no longer produced (§12.1).
- **Legend** (`iamlab-legend:v1:key=value;…`, since 4.0.0) runs the other way: **Overhang** (output `legend`, index 4) and **Interlayer Time** (output `legend`, index 7) *emit* it and **Legend** consumes it. Both build the wire from Core (`LegendSpec`, `OverhangColors`, `PlasmaColorScale`), which is the point — the viewport colours and the drawn legend read the same table and cannot drift apart. Unknown keys are ignored and every explicit Legend input overrides the field the wire carried, so the wire is a default, not a contract.

Two 4.0.0 flows do not travel on a wire at all:

- **The settings folder** (§7.6) is a document-scoped registration, not a value: `Load Settings` points one document at a folder, and from then on every component's `⇤ inputs` button builds its sliders/toggles/panels/value lists from the matching `*.iamset.json` and falls back to the built-in `ParamSpec` defaults where no file matches. It reaches components through `Ui/SettingsRegistry.cs`, lives in the session only, and is never written into the `.gh`.
- **The transport** is chosen by `MachineClientFactory` from the robot definition's `RobotControlKind` (12.1, 15). Only Duet remains after 4.1.0, but the choice stays in the factory rather than in a socket, so the Console still has nothing transport-shaped to get wrong.
- **The Graph Mapper feeds the slicer** (4.1.0): `Graph Mapper.map` (a serialized `GraphMap`) into `Slice.profile`, where it becomes the `GraphProfile` driving wave infill and the non-planar pass (10).

Every consumer reads an unwired robot definition as "keep my own default", so adding one to an existing document changes nothing until it is connected. The bed wire is stricter since 2026-09-09: Place keeps its historical print centre when unwired, but Live View and Robot Simulation draw no plate at all until a Print Bed is connected (§12.1), and there are no per-component plate inputs left to win over it.
