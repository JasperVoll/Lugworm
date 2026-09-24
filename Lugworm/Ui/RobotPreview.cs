using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace Lugworm.Ui
{
    /// <summary>Ghosted robot look for the Robot Simulation viewport: one translucent,
    /// unlit light-grey body with its feature edges on top, the way Rhino's Ghosted
    /// display mode presents a part. The links draw in two passes, depth first and
    /// colour second, so per pixel only the nearest skin blends once: the
    /// transparency is the material's alpha, not that alpha stacked over every
    /// internal part, bolt and back face the ~100k-face assemblies hold, which read
    /// as solid at any transparency. The prepass also settles coincident faces, so
    /// nothing flickers. The edges, drawn afterwards, are depth-tested against the
    /// ghost's own depth, so hidden ones are culled and the rest read as the outline.</summary>
    public static class RobotPreview
    {
        public static readonly Color GhostColor = Color.FromArgb(205, 207, 211);
        public static readonly Color GhostEdgeColor = Color.Black;
        /// <summary>The real per-pixel alpha since the depth prepass: 0.7 leaves
        /// 30 % of the body's tint over whatever is behind it.</summary>
        public const double GhostTransparency = 0.7;

        // Unlit: the colour comes entirely from emission, the diffuse and specular
        // terms are black, so no light in the viewport shades the body and every face
        // of the ghost carries the same flat grey. Form is the edge overlay's job.
        static readonly DisplayMaterial _ghost = new DisplayMaterial(Color.Black, GhostTransparency)
        {
            Emission = GhostColor,
            Specular = Color.Black,
            Shine = 0.0,
            IsTwoSided = true,
            BackDiffuse = Color.Black,
            BackEmission = GhostColor,
            BackTransparency = GhostTransparency,
            BackSpecular = Color.Black,
            BackShine = 0.0,
        };

        public static DisplayMaterial GhostMaterial() => _ghost;

        /// <summary>Draw posed link meshes as one ghost. Call from DrawViewportMeshes
        /// after the opaque scene so the body blends over it.</summary>
        public static void DrawGhost(DisplayPipeline display, IReadOnlyList<Mesh> meshes)
        {
            if (meshes.Count == 0) return;
            TwoPass(display, () => { foreach (var mesh in meshes) display.DrawMeshShaded(mesh, _ghost); });
        }

        /// <summary>The same ghost for shared base meshes under one transform each - the
        /// live viewport's per-frame path, which never duplicates a mesh to pose it.
        /// <paramref name="doc"/> is the document-unit transform applied on top of every
        /// link's own; pass <c>Transform.Identity</c> when the caller has already pushed
        /// it.</summary>
        public static void DrawGhost(DisplayPipeline display,
                                     IReadOnlyList<(Mesh mesh, Transform x)> links, Transform doc)
        {
            if (links.Count == 0) return;
            TwoPass(display, () =>
            {
                foreach (var (mesh, x) in links)
                {
                    display.PushModelTransform(doc * x);
                    try { display.DrawMeshShaded(mesh, _ghost); }
                    finally { display.PopModelTransform(); }
                }
            });
        }

        /// <summary>Depth prepass, then the colour pass. The prepass puts the nearest
        /// surface into the depth buffer and leaves the colour buffer untouched; in the
        /// colour pass, which writes no depth, only fragments at that nearest depth
        /// survive the test, so every pixel blends the tint exactly once. Both passes
        /// must cover the WHOLE body before the other begins, which is why the drawing
        /// is a callback rather than a loop here. Everything drawn later by other
        /// components is depth-tested against this skin as if the ghost were solid.</summary>
        static void TwoPass(DisplayPipeline display, Action draw)
        {
            display.EnableColorWriting(false);
            display.PushDepthWriting(true);
            try { draw(); }
            finally { display.PopDepthWriting(); display.EnableColorWriting(true); }
            display.PushDepthWriting(false);
            try { draw(); }
            finally { display.PopDepthWriting(); }
        }

        /// <summary>The feature-edge overlay (naked boundaries and creases) that gives
        /// the unlit body its form. Call from DrawViewportWires, after the ghost, so
        /// the edges it hides are culled against its own depth.</summary>
        public static void DrawEdges(DisplayPipeline display, IReadOnlyList<Line> edges)
        {
            if (edges.Count > 0) display.DrawLines(edges, GhostEdgeColor, 1);
        }
    }
}
