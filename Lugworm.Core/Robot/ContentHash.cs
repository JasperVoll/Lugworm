using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Robot
{
    public static class ContentHash
    {
        const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        public static ulong Fnv1a(string s)
        {
            ulong h = Offset;
            foreach (char c in s) { h ^= c; h *= Prime; }
            return h;
        }

        public static ulong Fnv1aLines(IReadOnlyList<string> lines)
        {
            ulong h = Offset;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i] ?? "";
                for (int k = 0; k < line.Length; k++) { h ^= line[k]; h *= Prime; }
                if (i + 1 < lines.Count) { h ^= '\n'; h *= Prime; }
            }
            return h;
        }

        public static ulong Fnv1aLinesWithSuffix(IReadOnlyList<string> lines, string suffix)
        {
            ulong h = Fnv1aLines(lines);
            string value = suffix ?? "";
            for (int i = 0; i < value.Length; i++) { h ^= value[i]; h *= Prime; }
            return h;
        }

        public static ulong Fnv1a(IReadOnlyList<Vec3> pts, IReadOnlyList<bool> extrude)
        {
            ulong h = Offset;
            void Mix(double v)
            {
                long bits = System.BitConverter.DoubleToInt64Bits(v);
                for (int b = 0; b < 8; b++) { h ^= (byte)(bits >> (8 * b)); h *= Prime; }
            }
            for (int i = 0; i < pts.Count; i++)
            { Mix(pts[i].X); Mix(pts[i].Y); Mix(pts[i].Z); h ^= extrude != null && i < extrude.Count && extrude[i] ? 1UL : 0UL; h *= Prime; }
            return h;
        }

        public static string LimitsKey(RobotLimits lim, int elbow, int firmware, bool arcSupport)
            => string.Join("|", lim.L1, lim.L2, lim.BaseH, lim.ReachMax, lim.ReachMin, lim.ZMax,
                lim.ZMin, lim.XMin, lim.XMax, lim.YMin, lim.YMax, lim.CheckEnvelope,
                lim.MaxFeed, lim.MaxJointSpeed, lim.JerkThresh, lim.J1Min, lim.J1Max,
                lim.J3Min, lim.J3Max, lim.J2Min, lim.J2Max, lim.MastClearance, lim.ArcChordTol,
                lim.CheckJointRanges, lim.RequireCalibratedHomePose,
                lim.CalibratedHomePose.HasValue ? lim.CalibratedHomePose.Value.X : double.NaN,
                lim.CalibratedHomePose.HasValue ? lim.CalibratedHomePose.Value.Y : double.NaN,
                lim.CalibratedHomePose.HasValue ? lim.CalibratedHomePose.Value.Z : double.NaN,
                elbow, firmware, arcSupport, lim.HomedArmMode);
    }
}
