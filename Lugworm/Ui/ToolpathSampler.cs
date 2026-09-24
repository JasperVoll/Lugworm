using System.Collections.Generic;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Lugworm.Convert;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;

namespace Lugworm.Ui
{
    /// <summary>Samples a moved-toolpath curve tree into TCP points + extrude flags
    /// (ext[k] = the segment ending at point k lays material). A join to the previous
    /// curve counts as continuous extrusion only when the ends meet on the SAME layer
    /// within the gap tolerance - the same-Z guard stops layers bridging between
    /// layers. Robot-independent: shared by the Robot Simulation component, the UR
    /// program generator and print-stats.</summary>
    public static class ToolpathSampler
    {
        public static (List<Vec3> pts, List<bool> ext) SampleMoves(IReadOnlyList<ParsedMove> moves,
                                                                    double? sharedArcTol = null)
        {
            var sampled = SampleMovesWithFeeds(moves, sharedArcTol);
            return (sampled.pts, sampled.ext);
        }

        /// <summary>The move sampler, now a thin call into <see cref="MoveSampler"/>.</summary>
        /// <remarks>The body moved into Core unchanged so the Live View renderer could also
        /// learn WHERE a file commands no motion at all (an unresolved G28 between two moves)
        /// without a second copy of the tessellation drifting away from this one. The
        /// returned triple is unchanged and stays that way: the Robot Simulation component,
        /// the RRF validation path and print-stats all index these lists in step with each
        /// other, so only the break list Core additionally returns is new.</remarks>
        public static (List<Vec3> pts, List<bool> ext, List<double> feeds)
            SampleMovesWithFeeds(IReadOnlyList<ParsedMove> moves, double? sharedArcTol = null)
        {
            var sampled = MoveSampler.SampleLegacy(moves, sharedArcTol);
            return (sampled.pts, sampled.ext, sampled.feeds);
        }

        public static (List<Vec3> pts, List<bool> ext) Sample(GH_Structure<GH_Curve> tree,
                                                              double tol = 0.5,
                                                              double scaleToMm = 1.0)
        {
            var sampled = SampleWithMetadata(tree, tol, scaleToMm);
            return (sampled.pts, sampled.ext);
        }

        /// <summary><paramref name="scaleToMm"/> converts document-unit curves into
        /// Core's mm frame while sampling (AUD-COR-001); tolerances stay mm.</summary>
        public static (List<Vec3> pts, List<bool> ext, List<int> layerIds)
            SampleWithMetadata(GH_Structure<GH_Curve> tree, double tol = 0.5,
                               double scaleToMm = 1.0)
        {
            var pts = new List<Vec3>();
            var ext = new List<bool>();
            var layerIds = new List<int>();
            for (int bi = 0; bi < tree.Branches.Count; bi++)
            {
                var branch = tree.Branches[bi];
                foreach (var goo in branch)
                {
                    if (goo?.Value == null) continue;
                    var cpts = GhConvert.CurveToPts(goo.Value, tol, scaleToMm);
                    if (cpts.Count == 0) continue;
                    for (int k = 0; k < cpts.Count; k++)
                    {
                        pts.Add(cpts[k]);
                        layerIds.Add(bi);
                        // Curves and branches are explicit topology boundaries;
                        // proximity must never create extruding bridges across an
                        // island, object, or contour handoff.
                        ext.Add(k > 0);
                    }
                }
            }
            return (pts, ext, layerIds);
        }

    }
}
