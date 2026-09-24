using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Doe
{
    /// <summary>One candidate recipe on (or considered for) the trade-off front.
    /// <see cref="D"/> is the vector of INDIVIDUAL desirabilities d₁…d_k (all
    /// maximised, one per response goal that is not <see cref="GoalKind.None"/>),
    /// not the raw responses: a target= or range= goal has no monotone direction,
    /// so raw responses cannot be Pareto-ordered, while Goal.D() maps every goal
    /// kind onto "bigger is better" and makes the front directly comparable with
    /// the scalarised overall D.</summary>
    public sealed class ParetoPoint
    {
        public double[] X = Array.Empty<double>();   // factor settings, actual units
        public double[] D = Array.Empty<double>();   // individual desirabilities
        public double DOverall;                      // scalarised D (0 allowed here)
        public double Feasible = double.NaN;
        public double[] Predicted = Array.Empty<double>();
        public double[] PiHalf = Array.Empty<double>(), PiLo = Array.Empty<double>(), PiHi = Array.Empty<double>();
        public double Leverage = double.NaN;
        public bool ExtrapolationFlag;
        public bool[] Computed = Array.Empty<bool>();

        /// <summary>Legacy prefix followed by feasible, predictions, half widths, exact bounds, leverage and flag.</summary>
        public double[] ToBranch() => X.Concat(D).Concat(new[] { DOverall, Feasible })
            .Concat(Predicted).Concat(PiHalf).Concat(PiLo).Concat(PiHi)
            .Concat(new[] { Leverage, ExtrapolationFlag ? 1.0 : 0.0 })
            .Concat(Enumerable.Range(0, D.Length).Select(i => i < Computed.Length && Computed[i] ? 1.0 : 0.0)).ToArray();
    }

    /// <summary>Non-dominated sorting and thinning for the Desirability component's
    /// front output. Written from the definitions (dominance; crowding distance per
    /// Deb, Pratap, Agarwal &amp; Meyarivan, IEEE TEC 2002, §III-B) - no external
    /// implementation was consulted. A point with some dᵢ = 0 is deliberately a
    /// legal candidate: the scalarised geometric mean annihilates it, but it can
    /// still be the best available on another objective, and those points are
    /// exactly what the scalarised output hides.</summary>
    public static class ParetoFront
    {
        /// <summary>The non-dominated subset of <paramref name="candidates"/>.
        /// a dominates b when a.D[i] ≥ b.D[i] for all i with at least one strict.
        /// Near-duplicates (objective vectors equal within <paramref name="epsilon"/>,
        /// relative) collapse to the representative with the higher DOverall.
        /// Complexity: sort O(n log n) + archive insertion O(n×|F|), which stays
        /// fast because the surviving front is small next to a dense grid.</summary>
        public static List<ParetoPoint> NonDominated(IReadOnlyList<ParetoPoint> candidates,
                                                     double epsilon = 1e-9)
        {
            var front = new List<ParetoPoint>();
            if (candidates == null || candidates.Count == 0) return front;
            int k = candidates[0].D.Length;
            if (k == 0) return front;

            // Descending on d₀, ties broken by descending sum: a point can only be
            // dominated by one sorted before it, so a single forward pass against
            // the current archive is complete.
            var sorted = candidates.Where(c => c.D.Length == k && c.D.All(Num.Finite))
                                   .OrderByDescending(c => c.D[0])
                                   .ThenByDescending(c => c.D.Sum())
                                   .ToList();
            foreach (var cand in sorted)
            {
                bool dominated = false;
                for (int i = front.Count - 1; i >= 0; i--)
                {
                    int rel = Compare(front[i].D, cand.D, epsilon);
                    if (rel > 0) { dominated = true; break; }         // archived dominates cand
                    if (rel < 0) front.RemoveAt(i);                   // cand dominates archived
                    else if (rel == 0 && Equivalent(front[i].D, cand.D, epsilon))
                    {
                        // near-duplicate objective vector: keep one representative
                        if (cand.DOverall > front[i].DOverall) front[i] = cand;
                        dominated = true;
                        break;
                    }
                }
                if (!dominated) front.Add(cand);
            }
            return front;
        }

        /// <summary>+1: a dominates b - −1: b dominates a - 0: neither.</summary>
        internal static int Compare(double[] a, double[] b, double eps)
        {
            bool aBetter = false, bBetter = false;
            for (int i = 0; i < a.Length; i++)
            {
                double tol = eps * Math.Max(1.0, Math.Max(Math.Abs(a[i]), Math.Abs(b[i])));
                if (a[i] > b[i] + tol) aBetter = true;
                else if (b[i] > a[i] + tol) bBetter = true;
                if (aBetter && bBetter) return 0;
            }
            if (aBetter && !bBetter) return 1;
            if (bBetter && !aBetter) return -1;
            return 0;
        }

        static bool Equivalent(double[] a, double[] b, double eps)
        {
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > eps * Math.Max(1.0, Math.Max(Math.Abs(a[i]), Math.Abs(b[i]))))
                    return false;
            return true;
        }

        /// <summary>Thins a front to at most <paramref name="maxCount"/> points by
        /// crowding distance (Deb et al. 2002), with one hard rule: the argmax of
        /// EVERY individual objective survives. A front that silently drops its
        /// endpoints is worse than no front.</summary>
        public static List<ParetoPoint> Thin(IReadOnlyList<ParetoPoint> front, int maxCount)
        {
            if (front == null) return new List<ParetoPoint>();
            if (front.Count <= maxCount || maxCount <= 0) return front.ToList();
            int k = front[0].D.Length;

            var keep = new bool[front.Count];
            for (int obj = 0; obj < k; obj++)
            {
                int arg = 0;
                for (int i = 1; i < front.Count; i++)
                    if (front[i].D[obj] > front[arg].D[obj]) arg = i;
                keep[arg] = true;
            }

            // crowding distance: per objective, sort and accumulate the normalized
            // gap between each point's neighbours; extremes get +∞ (already kept).
            var crowd = new double[front.Count];
            var order = new int[front.Count];
            for (int obj = 0; obj < k; obj++)
            {
                for (int i = 0; i < front.Count; i++) order[i] = i;
                int o = obj;
                Array.Sort(order, (a, b) => front[a].D[o].CompareTo(front[b].D[o]));
                double span = front[order[front.Count - 1]].D[o] - front[order[0]].D[o];
                crowd[order[0]] = double.PositiveInfinity;
                crowd[order[front.Count - 1]] = double.PositiveInfinity;
                if (span <= 0) continue;
                for (int i = 1; i < front.Count - 1; i++)
                    crowd[order[i]] += (front[order[i + 1]].D[o] - front[order[i - 1]].D[o]) / span;
            }

            var chosen = Enumerable.Range(0, front.Count)
                .OrderByDescending(i => keep[i])                 // objective argmaxes first
                .ThenByDescending(i => crowd[i])
                .ThenByDescending(i => front[i].DOverall)
                .Take(Math.Max(maxCount, keep.Count(kv => kv)))
                .Select(i => front[i]);
            return chosen.ToList();
        }
    }
}
