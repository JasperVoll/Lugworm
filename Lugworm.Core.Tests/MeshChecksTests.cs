using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class MeshChecksTests
{
    // ---- synthetic fixtures ----

    /// <summary>Closed tetrahedron, consistently outward-oriented.</summary>
    static (List<Vec3> V, List<(int, int, int)> F) Tetra(double ox = 0)
    {
        var V = new List<Vec3>
        {
            new Vec3(ox + 0, 0, 0), new Vec3(ox + 1, 0, 0),
            new Vec3(ox + 0.5, 1, 0), new Vec3(ox + 0.5, 0.4, 1),
        };
        var F = new List<(int, int, int)>
        {
            (0, 2, 1),   // bottom, seen from below
            (0, 1, 3), (1, 2, 3), (2, 0, 3),
        };
        return (V, F);
    }

    /// <summary>Closed octahedron (8 faces) - a crude "sphere".</summary>
    static (List<Vec3> V, List<(int, int, int)> F) Octa()
    {
        var V = new List<Vec3>
        {
            new Vec3(1, 0, 0), new Vec3(-1, 0, 0),
            new Vec3(0, 1, 0), new Vec3(0, -1, 0),
            new Vec3(0, 0, 1), new Vec3(0, 0, -1),
        };
        var F = new List<(int, int, int)>
        {
            (0, 2, 4), (2, 1, 4), (1, 3, 4), (3, 0, 4),
            (2, 0, 5), (1, 2, 5), (3, 1, 5), (0, 3, 5),
        };
        return (V, F);
    }

    /// <summary>Open box: cube without its top face (10 triangles).</summary>
    static (List<Vec3> V, List<(int, int, int)> F) OpenBox()
    {
        var V = new List<Vec3>
        {
            new Vec3(0,0,0), new Vec3(1,0,0), new Vec3(1,1,0), new Vec3(0,1,0),   // bottom
            new Vec3(0,0,1), new Vec3(1,0,1), new Vec3(1,1,1), new Vec3(0,1,1),   // top rim
        };
        var F = new List<(int, int, int)>
        {
            (0, 2, 1), (0, 3, 2),                 // bottom (outward = down)
            (0, 1, 5), (0, 5, 4),                 // front
            (1, 2, 6), (1, 6, 5),                 // right
            (2, 3, 7), (2, 7, 6),                 // back
            (3, 0, 4), (3, 4, 7),                 // left
        };
        return (V, F);
    }

    // ---- closed / clean ----

    [Fact]
    public void ClosedSphere_IsCleanClosedManifold()
    {
        var (V, F) = Octa();
        var r = MeshChecks.Analyze(V, F);
        Assert.True(r.IsClosed);
        Assert.True(r.IsEdgeManifold);
        Assert.True(r.IsOrientationConsistent);
        Assert.Equal(1, r.ComponentCount);
        Assert.Equal(0, r.BoundaryLoopCount);
        Assert.Empty(r.DegenerateFaces);
        Assert.Empty(r.UnreferencedVertices);
        Assert.False(r.HasErrors);
        Assert.Empty(r.Describe());
    }

    // ---- open box ----

    [Fact]
    public void OpenBox_ReportsNakedEdgesAndOneLoop()
    {
        var (V, F) = OpenBox();
        var r = MeshChecks.Analyze(V, F);
        Assert.False(r.IsClosed);
        Assert.Equal(4, r.NakedEdgeCount);         // the open top rim
        Assert.Equal(1, r.BoundaryLoopCount);
        Assert.True(r.IsEdgeManifold);
        Assert.False(r.HasErrors);                  // open is a warning, not a blocker
        Assert.Contains(r.Describe(), m => m.Contains("open"));
    }

    // ---- non-manifold ----

    [Fact]
    public void ThreeFacesOnOneEdge_IsNonManifold()
    {
        var V = new List<Vec3>
        {
            new Vec3(0,0,0), new Vec3(1,0,0),          // shared edge
            new Vec3(0.5,1,0), new Vec3(0.5,-1,0), new Vec3(0.5,0,1),
        };
        var F = new List<(int, int, int)> { (0, 1, 2), (0, 1, 3), (0, 1, 4) };
        var r = MeshChecks.Analyze(V, F);
        Assert.Equal(1, r.NonManifoldEdgeCount);
        Assert.True(r.HasErrors);
        Assert.Contains(r.Describe(), m => m.Contains("non-manifold"));
    }

    // ---- flipped normals ----

    [Fact]
    public void FlippedFace_DetectedAndUnified()
    {
        var (V, F) = Tetra();
        F[2] = (F[2].Item1, F[2].Item3, F[2].Item2);   // flip one face
        var before = MeshChecks.Analyze(V, F);
        Assert.True(before.OrientationConflicts > 0);

        var (fixedF, flipped) = MeshChecks.UnifyOrientation(F);
        Assert.Equal(1, flipped);
        var after = MeshChecks.Analyze(V, fixedF);
        Assert.Equal(0, after.OrientationConflicts);
        Assert.True(after.IsClosed);
    }

    [Fact]
    public void ConsistentMesh_UnifyIsNoOp()
    {
        var (V, F) = Octa();
        var (fixedF, flipped) = MeshChecks.UnifyOrientation(F);
        Assert.Equal(0, flipped);
        Assert.Equal(F, fixedF);
    }

    // ---- degenerate faces / unreferenced vertices ----

    [Fact]
    public void ZeroAreaFace_Reported()
    {
        var (V, F) = Tetra();
        V.Add(new Vec3(2, 2, 2));
        F.Add((4, 4, 4));   // fully collapsed triangle
        var r = MeshChecks.Analyze(V, F);
        Assert.Contains(4, r.DegenerateFaces);
    }

    [Fact]
    public void UnreferencedVertex_Reported()
    {
        var (V, F) = Tetra();
        V.Add(new Vec3(9, 9, 9));
        var r = MeshChecks.Analyze(V, F);
        Assert.Contains(4, r.UnreferencedVertices);
        Assert.Contains(r.Describe(), m => m.Contains("unreferenced"));
    }

    // ---- components ----

    [Fact]
    public void TwoDisjointTetras_TwoComponents_AndSplit()
    {
        var (V1, F1) = Tetra(0);
        var (V2, F2) = Tetra(10);
        var V = new List<Vec3>(V1); V.AddRange(V2);
        var F = new List<(int, int, int)>(F1);
        foreach (var f in F2) F.Add((f.Item1 + 4, f.Item2 + 4, f.Item3 + 4));

        var r = MeshChecks.Analyze(V, F);
        Assert.Equal(2, r.ComponentCount);
        Assert.Equal(new List<int> { 4, 4 }, r.ComponentSizes);

        var parts = MeshChecks.SplitComponents(V, F, r);
        Assert.Equal(2, parts.Count);
        foreach (var (cv, cf, toOrig) in parts)
        {
            Assert.Equal(4, cv.Count);
            Assert.Equal(4, cf.Count);
            var rr = MeshChecks.Analyze(cv, cf);
            Assert.True(rr.IsClosed);
            Assert.Equal(1, rr.ComponentCount);
        }
    }
}
