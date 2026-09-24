# Robot preview drawing and deposition/travel colouring

Moved verbatim out of `../ARCHI.md` sections 12.2 and 12.2b during the 4.4.0
documentation compaction. ARCHI keeps the rule; the code sketch and the reasoning
live here.

## Ghost in Robot Simulation (ARCHI section 12.2)

`Robot Simulation` draws the posed links as one translucent, unlit light-grey body with
its feature edges on top, via `Ui/RobotPreview`:

```csharp
// DrawViewportMeshes, after the opaque bed and layers
display.EnableColorWriting(false);                  // depth prepass: nearest skin into the depth buffer only
foreach (var mesh in meshes) display.DrawMeshShaded(mesh, ghost);
display.EnableColorWriting(true);
display.PushDepthWriting(false);                    // colour pass: only fragments at that depth survive
foreach (var mesh in meshes) display.DrawMeshShaded(mesh, ghost);   // emission 205,207,211 on black, @ 0.7, two-sided, unlit
display.PopDepthWriting();
// DrawViewportWires
display.DrawLines(edges, GhostEdgeColor, 1);        // black, depth-tested against the ghost's own skin
```

The prepass is what makes the transparency real: with depth writing simply off, every
internal part, bolt and back face of the ~100k-face assemblies stacked its alpha dozens
deep and the body read as solid even at 0.95. Now each pixel blends the tint once, the
coincident faces of the shipped assemblies cannot z-fight, and hidden edges are culled so
the visible ones read as the outline. Anything other components draw afterwards is
depth-tested against the ghost as if it were solid.

`Live View` draws the same ghost from its display conduit. Its links are the shared base
meshes under a transform each, so it calls the overload that composes the document-unit
transform with every link's own and runs both passes over the whole arm:

```csharp
RobotPreview.DrawGhost(display, frame.Links, scaled ? doc : Transform.Identity);
```

Nothing is duplicated or recoloured to pose the live arm, and the ghost is unconditional:
a stale pose looks exactly like a fresh one, because an appearance that changed with
staleness read as the arm flashing at telemetry cadence (2026-09-09).

## Flat and unlit, not shaded (the hidden `Robot`)

The remaining robot view draws posed meshes the old way, and it is not a style preference:

```csharp
mesh.VertexColors.CreateMonotoneMesh(RobotColor);   // 236,232,224
args.Display.DrawMeshFalseColors(mesh);             // flat + unlit
// plus, in DrawViewportWires:
foreach (var ln in _robotEdges) args.Display.DrawLine(ln, RobotEdgeColor, 1);  // 120,118,112
```

The shipped assemblies carry coincident faces. Under a lit `DisplayMaterial` those z-fight and shimmer as the scrub slider moves; under monotone false colours the competing faces share a colour, so the artefact is invisible. Form then comes from a feature-edge overlay (naked boundaries + creases sharper than `FeatureAngleRad = 0.55`, ~31°) rather than from shading.

Edges are extracted from the **base** meshes and transformed per frame; recomputing `TopologyEdges` on every posed duplicate is a per-tick hotspot on the ~100k-face assemblies. `MeshFeatureEdges` (`Lugworm/Robot/`) is the shared extractor; `RobotModel.PosedFeatureEdges` caches it keyed on the crease angle.

## Deposition vs. travel in the viewer (ARCHI section 12.2b)

**Robot Simulation** colours the two apart (since 2.1.0): deposition in RoyalBlue at width 2,
travel in ochre `(214,148,62)` at width 1. Both come from `Core/Paths/ExtrusionHistory` —
`VisibleRuns` for the layers, `TravelRuns` for their exact complement — so a run drawn as a
wire and a layer built by `ExtrusionPreview` cannot disagree about what extrudes. The two sets
partition the walked prefix exactly; `TravelRunsTests` pins that.

**Travel draws in both display modes.** It is the one part of the path a layer mesh cannot
represent, so suppressing it whenever `showExtrusion` is on would leave every jump invisible.
Deposition wires still yield to the layer mesh when that is showing.
