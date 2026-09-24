using Lugworm.Core.Geometry;

namespace Lugworm.Core.Arcs
{
    public enum MoveKind { Line, Arc }

    /// <summary>A toolpath move: a straight line P0->P1, or a circular arc P0->P1
    /// about (Cx,Cy) with radius R, clockwise when Cw (G2) else CCW (G3).</summary>
    public readonly struct Move
    {
        public readonly MoveKind Kind;
        public readonly Vec3 P0, P1;
        public readonly double Cx, Cy, R;
        public readonly bool Cw;

        private Move(MoveKind kind, Vec3 p0, Vec3 p1, double cx, double cy, bool cw, double r)
        { Kind = kind; P0 = p0; P1 = p1; Cx = cx; Cy = cy; Cw = cw; R = r; }

        public static Move Line(Vec3 p0, Vec3 p1)
            => new Move(MoveKind.Line, p0, p1, 0, 0, false, 0);

        public static Move Arc(Vec3 p0, Vec3 p1, double cx, double cy, bool cw, double r)
            => new Move(MoveKind.Arc, p0, p1, cx, cy, cw, r);

        /// <summary>Signed sweep angle of an arc move (0 for lines): negative when CW.
        /// Coincident XY endpoints follow the firmware convention - a FULL circle
        /// (±2π), never a zero-sweep no-op. Single source of truth for every sweep
        /// consumer (PointAt, ArcFit.ArcLengthOf, ArcTessellation).</summary>
        public double SweepAngle()
        {
            if (Kind == MoveKind.Line) return 0.0;
            double ex = P1.X - P0.X, ey = P1.Y - P0.Y;
            if (ex * ex + ey * ey < 1e-12) return Cw ? -2 * System.Math.PI : 2 * System.Math.PI;
            double a0 = System.Math.Atan2(P0.Y - Cy, P0.X - Cx);
            double a1 = System.Math.Atan2(P1.Y - Cy, P1.X - Cx);
            double da = a1 - a0;
            if (Cw) { while (da > 0) da -= 2 * System.Math.PI; while (da < -2 * System.Math.PI) da += 2 * System.Math.PI; }
            else { while (da < 0) da += 2 * System.Math.PI; while (da > 2 * System.Math.PI) da -= 2 * System.Math.PI; }
            return da;
        }

        public Vec3 PointAt(double t)
        {
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            if (Kind == MoveKind.Line)
                return new Vec3(P0.X + (P1.X - P0.X) * t, P0.Y + (P1.Y - P0.Y) * t,
                                P0.Z + (P1.Z - P0.Z) * t);
            double a0 = System.Math.Atan2(P0.Y - Cy, P0.X - Cx);
            double a = a0 + SweepAngle() * t;
            return new Vec3(Cx + R * System.Math.Cos(a), Cy + R * System.Math.Sin(a),
                            P0.Z + (P1.Z - P0.Z) * t);
        }

        public Move Portion(double t0, double t1)
            => Kind == MoveKind.Line ? Line(PointAt(t0), PointAt(t1))
                                      : Arc(PointAt(t0), PointAt(t1), Cx, Cy, Cw, R);
    }
}
