using System;
using System.Linq;
using System.Reflection;
using Clipper2Lib;

namespace Lugworm.Core.Clipping
{
    /// <summary>Version-proof bridge to Clipper2. Grasshopper loads ONE Clipper2Lib
    /// per session by simple name - whichever plugin loads first wins (this machine:
    /// ovenbird ships v2.0, Nautilus v1.5.3, we compile against 1.4.0). Static calls
    /// then throw MissingMethodException when the loaded version changed a signature
    /// (InflatePaths gained/lost optional parameters across releases). This shim
    /// resolves the methods from the assembly that ACTUALLY loaded, fills optional
    /// parameters by type, and passes enum values by NAME (enum member order also
    /// changed between versions). MethodInfos are cached - reflection cost is per
    /// call, not per point.</summary>
    internal static class ClipperCompat
    {
        static volatile MethodInfo? _inflate;
        static object? _joinRound, _endPolygon;
        static readonly object Sync = new object();

        /// <summary>Clipper.InflatePaths for closed polygons (round joins).</summary>
        public static PathsD InflatePaths(PathsD subject, double delta, double miterLimit, int precision)
        {
            if (_inflate == null)
            {
                lock (Sync)
                {
                    if (_inflate == null)
                    {
                        var found = typeof(Clipper).GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .FirstOrDefault(m => m.Name == "InflatePaths"
                                && m.GetParameters().Length >= 4
                                && m.GetParameters()[0].ParameterType == typeof(PathsD)
                                && m.GetParameters()[1].ParameterType == typeof(double));
                        if (found == null)
                            throw new MissingMethodException(
                                "Clipper2Lib.Clipper.InflatePaths(PathsD, …) not found in the loaded Clipper2Lib v" +
                                typeof(Clipper).Assembly.GetName().Version +
                                " (another Grasshopper plugin loaded an incompatible fork)");
                        var ps = found.GetParameters();
                        _joinRound = Enum.Parse(ps[2].ParameterType, "Round");
                        _endPolygon = Enum.Parse(ps[3].ParameterType, "Polygon");
                        _inflate = found;
                    }
                }
            }
            var pars = _inflate.GetParameters();
            var args = new object[pars.Length];
            args[0] = subject; args[1] = delta; args[2] = _joinRound!; args[3] = _endPolygon!;
            for (int i = 4; i < pars.Length; i++)
            {
                var t = pars[i].ParameterType;
                if (t == typeof(double)) args[i] = miterLimit;
                else if (t == typeof(int)) args[i] = precision;
                else if (pars[i].HasDefaultValue && pars[i].DefaultValue != null) args[i] = pars[i].DefaultValue!;
                else args[i] = t.IsValueType ? Activator.CreateInstance(t)! : null!;
            }
            try
            {
                return (PathsD)_inflate.Invoke(null, args)!;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        /// <summary>Intersection of open subject lines with closed clip polygons -
        /// the ClipperD instance API is signature-stable, but construct defensively so
        /// a fork without the precision ctor still works.</summary>
        public static PathsD ClipOpenLines(PathsD openSubject, PathsD clip, int precision)
        {
            ClipperD c;
            try { c = new ClipperD(precision); }
            catch (MissingMethodException) { c = (ClipperD)Activator.CreateInstance(typeof(ClipperD))!; }
            c.AddClip(clip);
            c.AddOpenSubject(openSubject);
            var closed = new PathsD();
            var open = new PathsD();
            c.Execute(ClipType.Intersection, FillRule.NonZero, closed, open);
            return open;
        }
    }
}
