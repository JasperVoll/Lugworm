using System;

namespace Lugworm.Core.Geometry
{
    /// <summary>Plain (x,y,z) point/vector in mm - the engine's only coordinate type.</summary>
    public readonly struct Vec3
    {
        public readonly double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public override string ToString() => FormattableString.Invariant($"({X:0.######},{Y:0.######},{Z:0.######})");

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 v, double s) => new Vec3(v.X * s, v.Y * s, v.Z * s);
        public static Vec3 operator *(double s, Vec3 v) => v * s;

        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double DistanceTo(Vec3 b) => (this - b).Length;
        /// <summary>Unit vector; the zero vector for inputs shorter than 1e-12.
        /// (Call sites that need a specific fallback direction keep their own.)</summary>
        public Vec3 Unit()
        {
            double n = Length;
            return n < 1e-12 ? new Vec3(0, 0, 0) : new Vec3(X / n, Y / n, Z / n);
        }
    }
}
