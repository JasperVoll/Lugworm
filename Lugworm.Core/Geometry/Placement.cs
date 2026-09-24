namespace Lugworm.Core.Geometry
{
    /// <summary>Pure placement math for the Mover. Computes the single translation that
    /// places a part at the global print centre.</summary>
    public static class Placement
    {
        /// <summary>Translation that lands the part's bottom-centre on
        /// <paramref name="printCenter"/>: the bbox centre moves onto printCenter.XY and the
        /// bbox base moves onto printCenter.Z. Then <paramref name="zOffset"/> (extra nozzle
        /// gap) and the <paramref name="move"/> vector (manual nudge) are added. Both default
        /// to zero, so printCenter alone fully positions the part.</summary>
        public static Vec3 Offset(Vec3 bboxCenter, double bboxMinZ, Vec3 printCenter,
                                  double zOffset, Vec3 move)
        {
            double dx = printCenter.X - bboxCenter.X + move.X;
            double dy = printCenter.Y - bboxCenter.Y + move.Y;
            double dz = printCenter.Z - bboxMinZ + zOffset + move.Z;
            return new Vec3(dx, dy, dz);
        }
    }
}
