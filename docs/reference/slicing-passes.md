# Slicing passes: schedules, offsets and the two stack passes

Moved verbatim out of `../ARCHI.md` section 10 during the 4.4.0 documentation
compaction. ARCHI keeps every rule and every gate constant; the algorithms, the
measurements and the reasoning behind each live here.

## Adaptive planar level schedule

2. **Adaptive planar** — `OverhangAdaptiveLevels.Build` chooses spacing from the
   worst facet inclination across the candidate layer's own interval. Each face
   contributes across its entire Z extent, so small faces cannot disappear in
   area averages or centroid bins. The height rule is
   `hMin + (hMax-hMin)*(1-abs(unitNormal.Z))`. A prefix-minimum tree removes
   expired faces and queries the faces reached by each candidate interval.
   Monotone bisection (40 iterations when hMax is infeasible) finds the largest
   feasible gap: O(F log F + 40 L log F) time and O(F + L) memory. Global horizontal base/top
   caps are excluded, internal horizontal ledges retained. Normal reversal does
   not affect the result; inward slopes are conservatively refined as well.
   Each object retains horizontal layers, the first-section convention and the
   existing optional `adaptive` controls. Only faces reached by the selected
   interval constrain its height; looking a full hMax ahead had incorrectly
   imposed a dome crown's minimum on lower layers. The 100,000-layer guard rejects instead of truncating a
   schedule. The older `Levels.Adaptive`/`SlopeFromMesh` APIs remain available
   but no longer drive the component. This is a geometry heuristic, not a
   calibrated overlap constraint, support generator, or printability proof.

## Offset ring thinning

**Offset rings are thinned, not just offset** (`Core/Clipping/Offset.cs`): `Offset.Polygons` re-runs RDP at `Offset.SimplifyTolerance(delta)` = `min(0.02, |delta|/200)` mm on every ring Clipper returns, because Clipper's round joins tessellate each convex corner at the working precision (4 == 0.1 um) and an iterated offset inherits the previous ring's vertices on top of the fresh arc steps, so a hole side compounds without bound (81 -> 481 -> 1117 -> 2090 vertices over three iterations of an r20 hole at spacing 3, against 81 flat after the thinning) and every O(n) pass downstream pays for it; `ClipperOffset.ArcTolerance` cannot replace this, because one arc step per convex corner is its floor (81 -> 161 -> 321 -> 641, identical at 0.015, 0.02 and 0.05 mm) and it removes no inherited vertex - `simplifyTol: 0` returns Clipper's raw ring, which is what `OffsetTests` measures the thinned rings against.

## Open-path orientation and Place seams

**Open-path orientation and Place seams:** Place measures each open endpoint against every closed wall in the placed coordinate frame, then orients each open path before seaming the walls. The rule first rejects a clearly inner endpoint, then prefers the layer seam target when it gives a clear choice, the previous layer's chosen start for the same object and open-path index, and finally the endpoint with the smaller polar angle from +X. Align and Stagger use their ray point as the target, Guide uses the guide's closest point, Track uses the previous outer seam, and Nearest uses the supplied point when present. In Nearest mode every closed wall follows the primary oriented open start; without open paths the supplied point remains the fallback. Endpoint selection is Core-tested; Rhino curve projection and tree wiring require in-app verification.

## Bricklayer

**Bricklayer** (`Core/Slicing/Bricklayer.cs`): within a layer, adjacent wall loops are two-coloured and every other colour rises by half a layer height, turning the vertical seam between two layer columns into a running bond. Two loops are adjacent only when **both** gates pass - XY distance within one layer width **and** more than 50% of the shorter loop running that close - because the distance gate alone bonds rings that merely touch at a tangent point, and the overlap gate alone bonds rings on opposite sides of the part. Since 4.4.0 (D10) the distance gate is tested at `Bricklayer.AdjacencyGate` = `layerWidth + max(1e-3 mm, 2% of layerWidth)` rather than at the layer width itself, because Clipper's 0.1 um grid and the ring thinning above land a round part's measured wall gap outside the nominal spacing (1.0e-4 mm on a 48-gon at spacing 3, 1.3e-2 mm on a 256-gon at spacing 6) and the old relative 1e-9 epsilon left cylinders, cones and vases reporting "0 wall loop(s) raised" on every eligible layer while the exact-offset rectangle fixtures passed. Components are two-coloured by BFS entered at the outermost loop, which stays flat unless `brickOuter`; a **non-bipartite component** (an odd cycle of three mutually adjacent loops) shifts nothing and is named in the report, because any two-colouring of it would put two shifted loops side by side. `RampLayers` leaves the first and last layers flat. A per-layer pair budget reports and skips rather than running quadratically on a part with hundreds of islands.

Conical layers need no separate path, and the reason is worth stating because the obvious design is wrong: the inverse conical map `z = level - tan(a)*r` subtracts a function of the radius only and leaves XY untouched, and `InverseContourRefined`'s chord refinement is radius-driven too, so raising a contour in the sheared frame and raising it after the map produce **identical points**. One uniform pass therefore serves both slice modes; `BricklayerTests.ConicalShift_EqualsShearedFrameShift` pins the equivalence rather than leaving it as a comment.

The pass emits geometry, not extrusion. A shifted loop's gap to the layer below is 1.5x the layer height on the ramp-in layer and 0.5x on the ramp-out layer, and the answer is the G-code generator's existing `varh` group (11) - the component emits a remark saying so whenever it actually shifts something. No flow factor is invented here.

## Non-planar path modulation

**Non-planar path modulation** (`Core/Slicing/NonPlanarInfill.cs`): `Infill` paths and selected closed `Wall` loops are resampled and displaced in Z by one periodic profile along a planar direction. Infill advances by layer phase. Walls also advance by `LoopPhaseTurns x WallIndex`, defaulting to a quarter turn per loop; wall index 0 stays flat unless `IncludeOuterWall` is enabled, and closure is restored exactly after resampling. Infill and wall amplitudes are checked against the same actual-gap rule, but the wall gap is the real one per loop after the Bricklayer pass (each shifted path records its `ZShift`), so at a Bricklayer ramp boundary, where a loop goes from flat to shifted, the wall limit is 0.4 x half a layer. The shared ramp keeps the first and last layers flat, and `OpenSection` paths are never touched. No per-segment extrusion correction is applied: `GcodeBuilder` measures every segment with the 3-D `PolylineOps.Dist`, so E already scales with the tilted length. Since 4.4.0 each modulated path is thinned afterwards - Douglas-Peucker in 3-D (`Rdp.SimplifyIndices`, whose `PolylineOps.PointSegDist` scoring is already 3-D) at `min(0.02 mm, amplitude/50)`, with closed loops thinned as an open polyline from their seam so the seam and the exact closure both survive - which keeps `Resolution` as the sampling step while dropping the vertices the profile never bent: on the two-loop square wall fixture a 200 mm wave falls from 130 points to 10, a 20 mm one only from 130 to 60.

## Wave infill

**Wave infill** (`Infill.WaveSpans`, infill modes 5 and 6): parallel rows that follow a periodic profile instead of running straight, **mirrored row to row** so one row's crests face the next row's troughs. `waveTangent` makes the mirrored peaks meet exactly (amplitude = spacing/2, common horizontal tangent at the contact point); otherwise they stop `waveClearance` short. Row pitch, boustrophedon connection, the hole/concavity connector guard and the wall anchor are `ZigzagSpans`' own. So is the boundary connector (both infills since 5.0.0): when the straight join would leave the region, row ends on one offset ring join along its shorter arc, one wall distance inside the last wall, provided the arc detours at most two row pitches beyond the straight join (`Infill.MaxConnectorDetourPitches`). Longer arcs are wraps around a hole; every row through the hole would wrap the same way and stack beads on its edge (three on a 20 mm square hole at a 3 mm pitch), so those rows start a new path instead. Amplitude is enforced **strictly below spacing/2** - not cosmetically, but because the single Clipper run recovers each clipped piece's row from its own y, which is unambiguous only while every point stays inside its row band; clipping row by row instead would cost one Clipper run per row for nothing.
