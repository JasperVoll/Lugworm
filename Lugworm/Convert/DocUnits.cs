using System;
using Rhino;
using Rhino.Geometry;

namespace Lugworm.Convert
{
    /// <summary>Document-unit adapter (AUD-COR-001). Core works exclusively in
    /// millimetres; this is the single authority for the document→mm factor and the
    /// scaled copies exchanged at the Rhino/Core boundary. Contract (spec §4.1):
    /// geometry and document tolerances convert to mm on entry, geometry/preview
    /// outputs convert back to document units on exit, inputs explicitly labelled
    /// `mm` are physical millimetres and never rescale, and G-code stays mm.
    /// A millimetre document has scale 1 and skips every copy/transform, so its
    /// behaviour is byte-identical to the pre-adapter pipeline.</summary>
    public static class DocUnits
    {
        /// <summary>Factor that converts the active document's model units to
        /// millimetres (25.4 for inches, 1000 for metres). 1.0 for millimetre
        /// documents and when no document is available (headless).</summary>
        public static double ScaleToMm()
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return 1.0;
            double s = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);
            return s > 0 && !double.IsInfinity(s) && !double.IsNaN(s) ? s : 1.0;
        }

        public static bool IsUnity(double s) => Math.Abs(s - 1.0) < 1e-12;

        /// <summary>Transform mapping document units to mm.</summary>
        public static Transform ToMmTransform(double s) => Transform.Scale(Point3d.Origin, s);

        /// <summary>Transform mapping mm back to document units (for previews and
        /// geometry outputs).</summary>
        public static Transform FromMmTransform(double s) => Transform.Scale(Point3d.Origin, 1.0 / s);

        /// <summary>Duplicated, mm-scaled copy of any geometry; the caller's object is
        /// never touched. Returns the original reference unchanged for mm documents.</summary>
        public static T ToMmCopy<T>(T geo, double s) where T : GeometryBase
        {
            if (IsUnity(s)) return geo;
            var dup = (T)geo.Duplicate();
            dup.Transform(ToMmTransform(s));
            return dup;
        }

        /// <summary>Scale a caller-owned (already duplicated) geometry to mm in place.</summary>
        public static void ToMmInPlace(GeometryBase geo, double s)
        {
            if (!IsUnity(s)) geo.Transform(ToMmTransform(s));
        }

        /// <summary>Scale a freshly built output back to document units in place.</summary>
        public static void FromMmInPlace(GeometryBase geo, double s)
        {
            if (!IsUnity(s)) geo.Transform(FromMmTransform(s));
        }

        public static Point3d FromMm(Point3d mm, double s)
            => IsUnity(s) ? mm : new Point3d(mm.X / s, mm.Y / s, mm.Z / s);
    }
}
