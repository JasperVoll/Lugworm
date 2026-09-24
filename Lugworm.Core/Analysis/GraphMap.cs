using System;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Analysis
{
    /// <summary>Graph families of the Graph Mapper - the stock Graph
    /// Mapper's set (minus Sine Summation, folded into Sine via its frequency
    /// handle, and Square Root, a Power preset) plus Conic, Exponential, Steps,
    /// and two fully custom point-based families: Polyline (linear interpolation)
    /// and Spline (shape-preserving monotone cubic), both with user-addable and
    /// removable points.</summary>
    public enum GraphMapKind
    {
        Linear,
        Bezier,
        Power,
        Sine,
        Parabola,
        Gaussian,
        Sigmoid,
        Perlin,
        Conic,
        Exponential,
        Steps,
        Polyline,
        Spline,
    }

    /// <summary>Which side of a Spline point a tangent handle belongs to: <c>In</c>
    /// shapes the segment arriving at the point, <c>Out</c> the segment leaving it.</summary>
    public enum GraphHandleSide { In = 0, Out = 1 }

    /// <summary>One editable mapping curve y = f(t) over the unit square, plus the
    /// domain remap around it. Pure and Rhino-free: the component evaluates and
    /// serializes through this class, the canvas editor only turns handle drags
    /// into parameter changes. Outputs are clamped to [0, 1] before the output
    /// remap, exactly like the stock Graph Mapper's graph area.</summary>
    public sealed class GraphMap
    {
        public GraphMapKind Kind;
        /// <summary>Parameters, meaning fixed per kind - see <see cref="Default"/>.</summary>
        public double[] P = Array.Empty<double>();

        /// <summary>Spline only: tangent handles parallel to <see cref="P"/> -
        /// <c>[in0, out0, in1, out1, …]</c> as unit-square slopes dy/dt, <c>NaN</c>
        /// meaning "automatic" (the Fritsch–Carlson tangent). A point whose two
        /// handles differ is a corner. Kept aligned with the point list by the
        /// add/remove/move methods; other kinds ignore it.</summary>
        public double[] H = Array.Empty<double>();

        /// <summary>Largest handle slope the editor and the setters accept - steeper
        /// than this the Hermite segment overshoots the whole graph area anyway.</summary>
        public const double MaxHandleSlope = 10.0;

        /// <summary>Parameter layout per kind (unit-square values unless noted):
        /// Linear      [y0, y1]                      - endpoint heights
        /// Bezier      [y0, p1x, p1y, p2x, p2y, y1]  - cubic, control x clamped to [0,1]
        /// Power       [e]                           - y = t^e, e ∈ [0.05, 20]
        /// Sine        [a, f, p]                     - y = 0.5 + a×sin(2π(f×t + p))
        /// Parabola    [vx, vy, a]                   - y = vy + a×(t − vx)²
        /// Gaussian    [mu, h, sigma]                - y = h×exp(−(t−mu)²/(2σ²))
        /// Sigmoid     [m, k]                        - y = 1/(1+exp(−k(t−m)))
        /// Perlin      [seed, f, a, base]            - base + a×noise(f×t), seed integer
        /// Conic       [a]                           - y = 1 − (1 − t^a)^(1/a), a ∈ [0.3, 8]
        /// Exponential [k]                           - y = (e^(k×t) − 1)/(e^k − 1); k→0 = identity, k&lt;0 = log-like
        /// Steps       [n]                           - n equal treads: y = floor(t×n)/(n−1), n ∈ [2, 20]
        /// Polyline    [t0,y0, t1,y1, …]             - VARIABLE length, sorted by t, linear interpolation
        /// Spline      [t0,y0, t1,y1, …]             - VARIABLE length, monotone cubic through the points
        ///                                             (Fritsch &amp; Carlson 1980 - shape-preserving, no overshoot)
        /// </summary>
        public static GraphMap Default(GraphMapKind kind) => new GraphMap
        {
            Kind = kind,
            P = kind switch
            {
                GraphMapKind.Linear => new[] { 0.0, 1.0 },
                GraphMapKind.Bezier => new[] { 0.0, 1.0 / 3, 1.0 / 3, 2.0 / 3, 2.0 / 3, 1.0 },
                GraphMapKind.Power => new[] { 1.0 },
                GraphMapKind.Sine => new[] { 0.4, 1.0, 0.75 },   // one full wave rising from 0.1
                GraphMapKind.Parabola => new[] { 0.0, 0.0, 1.0 },
                GraphMapKind.Gaussian => new[] { 0.5, 1.0, 0.18 },
                GraphMapKind.Sigmoid => new[] { 0.5, 10.0 },
                GraphMapKind.Perlin => new[] { 1.0, 3.0, 0.35, 0.5 },
                GraphMapKind.Conic => new[] { 2.0 },
                GraphMapKind.Exponential => new[] { 3.0 },
                GraphMapKind.Steps => new[] { 4.0 },
                GraphMapKind.Polyline => new[] { 0.0, 0.0, 0.5, 0.5, 1.0, 1.0 },
                GraphMapKind.Spline => new[] { 0.0, 0.0, 0.25, 0.1, 0.75, 0.9, 1.0, 1.0 },
                _ => new[] { 0.0, 1.0 },
            },
        };

        /// <summary>True for the point-based families whose parameter vector is a
        /// variable-length (t, y) list with addable/removable points.</summary>
        public bool IsCustom => Kind == GraphMapKind.Polyline || Kind == GraphMapKind.Spline;

        public int PointCount => IsCustom ? P.Length / 2 : 0;

        /// <summary>True for the family that carries tangent handles.</summary>
        public bool HasHandles => Kind == GraphMapKind.Spline;

        // ── tangent handles (Spline) ──

        /// <summary>Grows or shrinks <see cref="H"/> to <see cref="P"/>'s length; new
        /// slots are automatic. <see cref="P"/> is a public field the component may
        /// swap wholesale, so every handle accessor realigns lazily.</summary>
        void EnsureHandles()
        {
            if (H.Length == P.Length) return;
            var h = new double[P.Length];
            for (int i = 0; i < h.Length; i++) h[i] = i < H.Length ? H[i] : double.NaN;
            H = h;
        }

        /// <summary>True when the user set this handle (not automatic).</summary>
        public bool HasUserHandle(int index, GraphHandleSide side)
        {
            if (!HasHandles || index < 0 || index >= PointCount) return false;
            return !double.IsNaN(UserHandle(index, side));
        }

        /// <summary>The stored handle, NaN when automatic or not yet allocated. The
        /// handle array grows only when a handle is set, so reads (and therefore
        /// <see cref="Evaluate"/>) never mutate the map.</summary>
        double UserHandle(int index, GraphHandleSide side)
        {
            int i = 2 * index + (int)side;
            return i < H.Length ? H[i] : double.NaN;
        }

        /// <summary>The slope in force on one side of a point: the user's handle when
        /// set, else the automatic monotone tangent. What the editor draws and what
        /// <see cref="Evaluate"/> uses.</summary>
        public double HandleSlope(int index, GraphHandleSide side)
        {
            int n = PointCount;
            if (!HasHandles || index < 0 || index >= n) return 0.0;
            double user = UserHandle(index, side);
            return double.IsNaN(user) ? AutoTangent(index, n) : user;
        }

        /// <summary>Sets one handle; the slope is clamped to ±<see cref="MaxHandleSlope"/>.</summary>
        public void SetHandle(int index, GraphHandleSide side, double slope)
        {
            if (!HasHandles || index < 0 || index >= PointCount) return;
            EnsureHandles();
            if (double.IsNaN(slope) || double.IsInfinity(slope)) return;
            H[2 * index + (int)side] = Math.Max(-MaxHandleSlope, Math.Min(MaxHandleSlope, slope));
        }

        /// <summary>Sets both handles of a point to the same slope (a smooth point).</summary>
        public void SetHandlePair(int index, double slope)
        {
            SetHandle(index, GraphHandleSide.In, slope);
            SetHandle(index, GraphHandleSide.Out, slope);
        }

        /// <summary>Returns both handles of a point to automatic.</summary>
        public void ResetHandle(int index)
        {
            if (!HasHandles || index < 0 || index >= PointCount) return;
            EnsureHandles();
            H[2 * index] = double.NaN;
            H[2 * index + 1] = double.NaN;
        }

        public int UserHandleCount
        {
            get
            {
                if (!HasHandles) return 0;
                EnsureHandles();
                int c = 0;
                foreach (double v in H) if (!double.IsNaN(v)) c++;
                return c;
            }
        }

        /// <summary>Deep copy (kind, points, handles).</summary>
        public GraphMap Copy() => new GraphMap { Kind = Kind, P = (double[])P.Clone(), H = (double[])H.Clone() };

        /// <summary>f(t) over the unit square; t is clamped in, y is clamped out.</summary>
        public double Evaluate(double t)
        {
            t = Num.Clamp01(t);
            double y;
            switch (Kind)
            {
                case GraphMapKind.Linear:
                    y = P[0] + (P[1] - P[0]) * t;
                    break;
                case GraphMapKind.Power:
                    y = Math.Pow(t, P[0]);
                    break;
                case GraphMapKind.Sine:
                    y = 0.5 + P[0] * Math.Sin(2 * Math.PI * (P[1] * t + P[2]));
                    break;
                case GraphMapKind.Parabola:
                    y = P[1] + P[2] * (t - P[0]) * (t - P[0]);
                    break;
                case GraphMapKind.Gaussian:
                    {
                        double s = Math.Max(1e-4, P[2]);
                        y = P[1] * Math.Exp(-(t - P[0]) * (t - P[0]) / (2 * s * s));
                        break;
                    }
                case GraphMapKind.Sigmoid:
                    y = 1.0 / (1.0 + Math.Exp(-P[1] * (t - P[0])));
                    break;
                case GraphMapKind.Bezier:
                    y = BezierY(t);
                    break;
                case GraphMapKind.Perlin:
                    y = P[3] + P[2] * Noise1D((int)Math.Round(P[0]), P[1] * t);
                    break;
                case GraphMapKind.Conic:
                    {
                        // superellipse quadrant: a = 2 is the circle, a → large squares off
                        double a = Math.Max(0.3, Math.Min(8, P[0]));
                        y = 1.0 - Math.Pow(Math.Max(0, 1.0 - Math.Pow(t, a)), 1.0 / a);
                        break;
                    }
                case GraphMapKind.Exponential:
                    {
                        double k = Math.Max(-10, Math.Min(10, P[0]));
                        // k → 0 degenerates to 0/0; the limit is the identity
                        y = Math.Abs(k) < 1e-6 ? t : (Math.Exp(k * t) - 1.0) / (Math.Exp(k) - 1.0);
                        break;
                    }
                case GraphMapKind.Steps:
                    {
                        int n = (int)Math.Max(2, Math.Min(20, Math.Round(P[0])));
                        y = Math.Min(n - 1, Math.Floor(t * n)) / (n - 1.0);
                        break;
                    }
                case GraphMapKind.Polyline:
                    y = InterpolatePoints(t, smooth: false);
                    break;
                case GraphMapKind.Spline:
                    y = InterpolatePoints(t, smooth: true);
                    break;
                default:
                    y = t;
                    break;
            }
            return Num.Clamp01(y);
        }

        /// <summary>Piecewise interpolation through the custom point list. Outside
        /// the first/last point the curve holds that point's height (a graph area
        /// has hard edges; extrapolating would invent shape nobody drew). Smooth
        /// mode is monotone cubic Hermite with Fritsch–Carlson slope limiting
        /// (SIAM J. Numer. Anal. 17(2), 1980): shape-preserving, so the curve
        /// passes through every point and never overshoots between them - unless
        /// the user set a tangent handle on a point, which then replaces the
        /// automatic slope on that side (a differing in/out pair is a corner).</summary>
        double InterpolatePoints(double t, bool smooth)
        {
            int n = P.Length / 2;
            if (n == 0) return t;
            if (n == 1) return P[1];
            if (t <= P[0]) return P[1];
            if (t >= P[2 * (n - 1)]) return P[2 * n - 1];
            int i = 0;
            while (i < n - 2 && t > P[2 * (i + 1)]) i++;
            double x0 = P[2 * i], y0 = P[2 * i + 1];
            double x1 = P[2 * i + 2], y1 = P[2 * i + 3];
            double h = x1 - x0;
            if (h <= 1e-12) return y1;
            double u = (t - x0) / h;
            if (!smooth) return y0 + (y1 - y0) * u;

            double d0 = HandleSlope(i, GraphHandleSide.Out), d1 = HandleSlope(i + 1, GraphHandleSide.In);
            double u2 = u * u, u3 = u2 * u;
            return (2 * u3 - 3 * u2 + 1) * y0 + (u3 - 2 * u2 + u) * h * d0
                 + (-2 * u3 + 3 * u2) * y1 + (u3 - u2) * h * d1;
        }

        /// <summary>Fritsch–Carlson tangent at point k: harmonic-mean-limited so a
        /// monotone stretch of data yields a monotone curve segment.</summary>
        double AutoTangent(int k, int n)
        {
            double Slope(int i)
            {
                double h = P[2 * i + 2] - P[2 * i];
                return h > 1e-12 ? (P[2 * i + 3] - P[2 * i + 1]) / h : 0.0;
            }
            if (k == 0) return Slope(0);
            if (k == n - 1) return Slope(n - 2);
            double sL = Slope(k - 1), sR = Slope(k);
            if (sL * sR <= 0) return 0.0;   // local extremum - flat tangent, no overshoot
            return 2.0 * sL * sR / (sL + sR);
        }

        /// <summary>Inserts a point into a custom family, keeping the list sorted
        /// by t; returns its index. No-op (-1) on non-custom kinds.</summary>
        public int AddPoint(double t, double y)
        {
            if (!IsCustom) return -1;
            EnsureHandles();
            t = Num.Clamp01(t); y = Num.Clamp01(y);
            int n = P.Length / 2, at = 0;
            while (at < n && P[2 * at] < t) at++;
            var next = new double[P.Length + 2];
            Array.Copy(P, next, 2 * at);
            next[2 * at] = t; next[2 * at + 1] = y;
            Array.Copy(P, 2 * at, next, 2 * at + 2, P.Length - 2 * at);
            // the new point starts with automatic handles; the others keep theirs
            var h = new double[H.Length + 2];
            Array.Copy(H, h, 2 * at);
            h[2 * at] = double.NaN; h[2 * at + 1] = double.NaN;
            Array.Copy(H, 2 * at, h, 2 * at + 2, H.Length - 2 * at);
            P = next;
            H = h;
            return at;
        }

        /// <summary>Removes point <paramref name="index"/> from a custom family;
        /// refuses (false) below 2 remaining points or on non-custom kinds.</summary>
        public bool RemovePoint(int index)
        {
            int n = P.Length / 2;
            if (!IsCustom || n <= 2 || index < 0 || index >= n) return false;
            EnsureHandles();
            var next = new double[P.Length - 2];
            Array.Copy(P, next, 2 * index);
            Array.Copy(P, 2 * index + 2, next, 2 * index, P.Length - 2 * index - 2);
            var h = new double[H.Length - 2];
            Array.Copy(H, h, 2 * index);
            Array.Copy(H, 2 * index + 2, h, 2 * index, H.Length - 2 * index - 2);
            P = next;
            H = h;
            return true;
        }

        /// <summary>Moves point <paramref name="index"/> of a custom family; t is
        /// clamped strictly between its neighbours so the list stays sorted and no
        /// two points collapse.</summary>
        public void MovePoint(int index, double t, double y)
        {
            int n = P.Length / 2;
            if (!IsCustom || index < 0 || index >= n) return;
            const double gap = 0.005;
            double lo = index == 0 ? 0 : P[2 * (index - 1)] + gap;
            double hi = index == n - 1 ? 1 : P[2 * (index + 1)] - gap;
            P[2 * index] = Math.Max(lo, Math.Min(hi, Num.Clamp01(t)));
            P[2 * index + 1] = Num.Clamp01(y);
        }

        /// <summary>Samples this curve into a custom family - the bridge from any
        /// analytic graph to free point editing.</summary>
        public GraphMap ToCustom(GraphMapKind target, int samples = 9)
        {
            if (target != GraphMapKind.Polyline && target != GraphMapKind.Spline)
                throw new ArgumentException("target must be Polyline or Spline");
            samples = Math.Max(2, Math.Min(64, samples));
            var p = new double[2 * samples];
            for (int i = 0; i < samples; i++)
            {
                double t = i / (samples - 1.0);
                p[2 * i] = t;
                p[2 * i + 1] = Evaluate(t);
            }
            return new GraphMap { Kind = target, P = p };
        }

        /// <summary>Cubic Bézier as a FUNCTION of t: with P0x=0, P3x=1 and both
        /// control x clamped to [0, 1], x(u) is monotone non-decreasing (the CSS
        /// cubic-bezier guarantee), so u can be recovered by bisection and the
        /// curve never folds back over itself.</summary>
        double BezierY(double x)
        {
            double p1x = Num.Clamp01(P[1]), p2x = Num.Clamp01(P[3]);
            double X(double u)
            {
                double v = 1 - u;
                return 3 * v * v * u * p1x + 3 * v * u * u * p2x + u * u * u;
            }
            double lo = 0, hi = 1;
            for (int i = 0; i < 48; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (X(mid) < x) lo = mid; else hi = mid;
            }
            double t = 0.5 * (lo + hi), w = 1 - t;
            return w * w * w * P[0] + 3 * w * w * t * P[2] + 3 * w * t * t * P[4] + t * t * t * P[5];
        }

        /// <summary>Deterministic 1D gradient noise in [-1, 1]: hashed lattice
        /// gradients, smoothstep-interpolated. Textbook construction - written
        /// from the definition, no implementation consulted.</summary>
        internal static double Noise1D(int seed, double x)
        {
            int i0 = (int)Math.Floor(x);
            double f = x - i0;
            double g0 = Gradient(seed, i0), g1 = Gradient(seed, i0 + 1);
            double v0 = g0 * f, v1 = g1 * (f - 1);
            double u = f * f * (3 - 2 * f);
            return 2.0 * (v0 + (v1 - v0) * u);   // ×2: raw range is about ±0.5
        }

        static double Gradient(int seed, int i)
        {
            unchecked
            {
                uint h = (uint)(i * 374761393 + seed * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return h / (double)uint.MaxValue * 2.0 - 1.0;
            }
        }

        /// <summary>Full pipeline of one value: input domain → unit t → curve →
        /// output domain. A degenerate input domain maps everything through t = 0.5.
        /// <paramref name="clip"/> clamps the input into its domain first (off, the
        /// unit clamp still applies - the graph area has hard edges by design).</summary>
        public double Map(double v, double inMin, double inMax, double outMin, double outMax, bool clip = true)
        {
            double t = inMax > inMin ? (v - inMin) / (inMax - inMin) : 0.5;
            if (clip) t = Num.Clamp01(t);
            return outMin + Evaluate(t) * (outMax - outMin);
        }

        // ── persistence (component Write/Read + undo records) ──

        /// <summary><c>Kind;p0;p1;…</c>, byte-identical to the 2.5.0 format unless a
        /// Spline carries user handles - then one trailing <c>h=in0,out0,…</c> token
        /// follows (empty entry = automatic), so every pre-4.0.0 map and every map
        /// without handles round-trips through the old reader unchanged.</summary>
        public string Serialize()
        {
            var inv = CultureInfo.InvariantCulture;
            string s = Kind + ";" + string.Join(";", P.Select(v => v.ToString("R", inv)));
            if (HasHandles && UserHandleCount > 0)
                s += ";h=" + string.Join(",", H.Select(v => double.IsNaN(v) ? "" : v.ToString("R", inv)));
            return s;
        }

        public static GraphMap Deserialize(string text)
        {
            var parts = text.Split(';');
            if (parts.Length < 2 || !Enum.TryParse(parts[0], out GraphMapKind kind))
                throw new FormatException($"cannot parse graph map '{text}'");
            string? handles = null;
            int count = parts.Length - 1;
            if (count > 0 && parts[parts.Length - 1].StartsWith("h=", StringComparison.Ordinal))
            {
                handles = parts[parts.Length - 1].Substring(2);
                count--;
            }
            var map = Default(kind);
            var p = new double[count];
            for (int i = 0; i < p.Length; i++)
                if (!double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out p[i]))
                    throw new FormatException($"cannot parse graph map value '{parts[i + 1]}'");
            if (map.IsCustom)
            {
                // variable-length point list: adopt the stored vector whole when it
                // is structurally valid, otherwise keep the default shape
                if (p.Length >= 4 && p.Length % 2 == 0) map.P = p;
            }
            else
                // fixed-length family: tolerate a longer/shorter vector from a
                // newer/older build - keep the defaults where the vector runs short
                for (int i = 0; i < Math.Min(p.Length, map.P.Length); i++) map.P[i] = p[i];
            if (handles != null && map.HasHandles)
            {
                // a handle list that does not match the point list is a corrupt or
                // foreign record - keep the points, drop the handles, never throw
                var tokens = handles.Split(',');
                if (tokens.Length == map.P.Length)
                {
                    var h = new double[tokens.Length];
                    bool ok = true;
                    for (int i = 0; i < h.Length && ok; i++)
                    {
                        if (tokens[i].Length == 0) { h[i] = double.NaN; continue; }
                        ok = double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out h[i])
                             && !double.IsInfinity(h[i]);
                    }
                    if (ok) map.H = h;
                }
            }
            return map;
        }

        public string Describe()
        {
            var inv = CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("0.###", inv);
            return Kind switch
            {
                GraphMapKind.Linear => $"y = {N(P[0])} + ({N(P[1])} − {N(P[0])})×t",
                GraphMapKind.Power => $"y = t^{N(P[0])}",
                GraphMapKind.Sine => $"y = 0.5 + {N(P[0])}×sin(2π({N(P[1])}×t + {N(P[2])}))",
                GraphMapKind.Parabola => $"y = {N(P[1])} + {N(P[2])}×(t − {N(P[0])})²",
                GraphMapKind.Gaussian => $"y = {N(P[1])}×exp(−(t − {N(P[0])})²/(2×{N(P[2])}²))",
                GraphMapKind.Sigmoid => $"y = 1/(1 + e^(−{N(P[1])}×(t − {N(P[0])})))",
                GraphMapKind.Bezier => $"bezier y0={N(P[0])} P1=({N(P[1])},{N(P[2])}) P2=({N(P[3])},{N(P[4])}) y1={N(P[5])}",
                GraphMapKind.Perlin => $"y = {N(P[3])} + {N(P[2])}×noise({N(P[1])}×t), seed {N(P[0])}",
                GraphMapKind.Conic => $"y = 1 − (1 − t^{N(P[0])})^(1/{N(P[0])})",
                GraphMapKind.Exponential => $"y = (e^({N(P[0])}×t) − 1)/(e^{N(P[0])} − 1)",
                GraphMapKind.Steps => $"{N(Math.Round(P[0]))} steps",
                GraphMapKind.Polyline => $"polyline through {PointCount} points",
                GraphMapKind.Spline => UserHandleCount > 0
                    ? $"spline through {PointCount} points, {UserHandleCount} handle(s) set"
                    : $"monotone spline through {PointCount} points",
                _ => Kind.ToString(),
            };
        }
    }
}
