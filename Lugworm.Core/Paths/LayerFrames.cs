using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Paths
{
    /// <summary>One placement of the layer cross-section along a swept run: the ring's
    /// centre, its lateral axis (unit length times the mitre widening, always in the
    /// layer plane) and the index of the path point it belongs to.</summary>
    public readonly struct LayerFrame
    {
        public readonly Vec3 Centre;
        public readonly Vec3 Side;
        public readonly int Source;

        public LayerFrame(Vec3 centre, Vec3 side, int source)
        {
            Centre = centre;
            Side = side;
            Source = source;
        }
    }

    /// <summary>Sweep frames for a layer piped along a polyline with ROUND joins. A
    /// vertex whose turn exceeds the join step gets a fan of rings rotating from the
    /// incoming to the outgoing lateral axis, so the outer side of a corner becomes an
    /// arc of half the layer width instead of one mitred crease; gentler turns keep a
    /// single mitred ring. The fan's inner side folds back into the tube it came
    /// from, which stays hidden inside the closed skin. Pure geometry, Rhino-free, so
    /// the sweep is testable and the mesh builder only has to place profile points.</summary>
    public static class LayerFrames
    {
        /// <summary>Angle one fan segment spans; 10 deg reads as round at every zoom
        /// the print itself is still legible at.</summary>
        public static readonly double DefaultJoinStepRad = Math.PI / 18;

        /// <summary>Coarser steps a budgeted caller falls back through before giving
        /// up on round joins altogether (<see cref="FitJoinStep"/>).</summary>
        public static readonly double[] JoinStepLadderRad =
            { Math.PI / 18, Math.PI / 12, Math.PI / 8, Math.PI / 6, Math.PI / 4 };

        // The single-ring mitre limits of the pre-fan sweep, still used for the turns a
        // fan does not cover: a bisector ring at a turn beyond ~110 deg would otherwise
        // reach far past the corner.
        const double MaxMiter = 2.0;
        const double MinMiterCos = 0.35;

        /// <summary>Frames for <paramref name="path"/>, in sweep order. With
        /// <paramref name="closed"/> the last point connects back to the first; with
        /// <paramref name="splitSeam"/> as well, the path repeats its first point at
        /// the end and both ends get their own ring, the head a plain one so the seam's
        /// turn is swept exactly once. <paramref name="joinStepRad"/> of +infinity
        /// means mitred rings only.</summary>
        public static List<LayerFrame> Build(IReadOnlyList<Vec3> path, bool closed, bool splitSeam, double joinStepRad)
        {
            int n = path.Count;
            var frames = new List<LayerFrame>(n);
            if (n < 2) return frames;
            if (n < 3) closed = false;
            for (int i = 0; i < n; i++)
            {
                var (sPrev, sNext) = Sides(path, i, closed, splitSeam);
                var centre = path[i];
                if (splitSeam && i == 0)
                {
                    frames.Add(new LayerFrame(centre, sNext, i));
                    continue;
                }
                double turn = SignedTurn(sPrev, sNext);
                int k = Segments(turn, joinStepRad);
                if (k <= 1)
                {
                    frames.Add(new LayerFrame(centre, MitredSide(sPrev, sNext, turn), i));
                    continue;
                }
                for (int j = 0; j <= k; j++)
                    frames.Add(new LayerFrame(centre, RotateZ(sPrev, turn * j / k), i));
            }
            return frames;
        }

        /// <summary>How many rings <see cref="Build"/> adds beyond one per point at
        /// this join step, the path taken as open or plainly closed.</summary>
        public static int ExtraJoinFrames(IReadOnlyList<Vec3> path, bool closed, double joinStepRad)
        {
            int n = path.Count;
            if (n < 2 || double.IsPositiveInfinity(joinStepRad)) return 0;
            if (n < 3) closed = false;
            int extra = 0;
            for (int i = 0; i < n; i++)
            {
                var (sPrev, sNext) = Sides(path, i, closed, false);
                int k = Segments(SignedTurn(sPrev, sNext), joinStepRad);
                if (k > 1) extra += k;
            }
            return extra;
        }

        /// <summary>The finest ladder step whose extra rings over all runs (taken as
        /// open) fit <paramref name="extraFrameBudget"/>, else +infinity: a print too
        /// large for round joins within the face budget falls back to mitred rings
        /// rather than blowing the budget.</summary>
        public static double FitJoinStep(IReadOnlyList<IReadOnlyList<Vec3>> runs, int extraFrameBudget)
        {
            if (extraFrameBudget <= 0) return double.PositiveInfinity;
            foreach (double step in JoinStepLadderRad)
            {
                long extra = 0;
                foreach (var run in runs)
                {
                    extra += ExtraJoinFrames(run, false, step);
                    if (extra > extraFrameBudget) break;
                }
                if (extra <= extraFrameBudget) return step;
            }
            return double.PositiveInfinity;
        }

        static (Vec3 prev, Vec3 next) Sides(IReadOnlyList<Vec3> path, int i, bool closed, bool splitSeam)
        {
            int n = path.Count;
            Vec3 tPrev, tNext;
            if (i > 0) tPrev = Dir(path[i - 1], path[i]);
            else if (closed) tPrev = Dir(path[splitSeam ? n - 2 : n - 1], path[0]);
            else tPrev = Dir(path[0], path[1]);
            if (i < n - 1) tNext = Dir(path[i], path[i + 1]);
            else if (closed) tNext = Dir(path[n - 1], path[splitSeam ? 1 : 0]);
            else tNext = Dir(path[n - 2], path[n - 1]);
            var sPrev = Lateral(tPrev);
            var sNext = Lateral(tNext);
            // a vertical segment has no lateral axis of its own: borrow the neighbour's,
            // and a wholly vertical run just picks one
            bool havePrev = sPrev.Length > 0.5, haveNext = sNext.Length > 0.5;
            if (!havePrev && !haveNext) { sPrev = new Vec3(1, 0, 0); sNext = sPrev; }
            else if (!havePrev) sPrev = sNext;
            else if (!haveNext) sNext = sPrev;
            return (sPrev, sNext);
        }

        static Vec3 Dir(Vec3 a, Vec3 b) => (b - a).Unit();

        /// <summary>Unit lateral axis of a travel direction, in the layer plane
        /// (travel x world Z); zero for a vertical direction.</summary>
        static Vec3 Lateral(Vec3 t)
        {
            var s = new Vec3(t.Y, -t.X, 0);
            return s.Length < 1e-9 ? new Vec3(0, 0, 0) : s.Unit();
        }

        /// <summary>Turn from one lateral axis to the next about world Z, in (-pi, pi].</summary>
        static double SignedTurn(Vec3 sPrev, Vec3 sNext)
        {
            double cross = sPrev.X * sNext.Y - sPrev.Y * sNext.X;
            double dot = sPrev.X * sNext.X + sPrev.Y * sNext.Y;
            return Math.Atan2(cross, dot);
        }

        static int Segments(double turn, double joinStepRad)
        {
            if (double.IsPositiveInfinity(joinStepRad) || double.IsNaN(joinStepRad) || joinStepRad <= 0) return 1;
            double a = Math.Abs(turn);
            if (a <= joinStepRad * (1 + 1e-9)) return 1;
            return (int)Math.Ceiling(a / joinStepRad - 1e-9);
        }

        static Vec3 MitredSide(Vec3 sPrev, Vec3 sNext, double turn)
        {
            var bis = sPrev + sNext;
            // a 180 deg turn has no bisector: the ring stands across the path, as before
            if (bis.Length < 1e-9) bis = RotateZ(sPrev, 0.5 * turn);
            double miter = Math.Min(MaxMiter, 1.0 / Math.Max(MinMiterCos, Math.Cos(0.5 * turn)));
            return bis.Unit() * miter;
        }

        static Vec3 RotateZ(Vec3 v, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return new Vec3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
        }
    }
}
