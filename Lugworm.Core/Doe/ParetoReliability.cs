using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Lugworm.Core.Doe
{
    /// <summary>Prediction and inference in response units. Exact bounds are retained
    /// because the displayed half-width cannot reconstruct an asymmetric interval.</summary>
    public sealed class ParetoReliability
    {
        public IReadOnlyList<double> Predicted { get; }
        public IReadOnlyList<double> PiHalf { get; }
        public IReadOnlyList<double> PiLo { get; }
        public IReadOnlyList<double> PiHi { get; }
        public double Leverage { get; }
        public bool ExtrapolationFlag { get; }
        public IReadOnlyList<bool> Computed { get; }
        readonly bool _includeComputed;

        public ParetoReliability(IEnumerable<double> predicted, IEnumerable<double> half,
            IEnumerable<double> lo, IEnumerable<double> hi, double leverage, bool extrapolation,
            IEnumerable<bool>? computed = null, bool includeComputed = true)
        {
            Predicted = Array.AsReadOnly(predicted.ToArray()); PiHalf = Array.AsReadOnly(half.ToArray());
            PiLo = Array.AsReadOnly(lo.ToArray()); PiHi = Array.AsReadOnly(hi.ToArray());
            Leverage = leverage; ExtrapolationFlag = extrapolation;
            Computed = Array.AsReadOnly(computed?.ToArray() ?? new bool[Predicted.Count]);
            _includeComputed = includeComputed;
        }

        public double[] ToColumns() => Predicted.Concat(PiHalf).Concat(PiLo).Concat(PiHi)
            .Concat(new[] { Leverage, ExtrapolationFlag ? 1.0 : 0.0 })
            .Concat(_includeComputed ? Computed.Select(c => c ? 1.0 : 0.0) : Enumerable.Empty<double>()).ToArray();

        internal static bool TryParse(IReadOnlyList<double> values, int k, out ParetoReliability? result)
        {
            result = null;
            if (values.Count != 4 * k + 2 && values.Count != 5 * k + 2) return false;
            var computed = new bool[k];
            if (values.Count == 5 * k + 2)
                for (int i = 0; i < k; i++)
                {
                    double flag = values[4 * k + 2 + i];
                    if (flag != 0 && flag != 1) return false;
                    computed[i] = flag == 1;
                }
            for (int i = 0; i < k; i++)
            {
                if (double.IsInfinity(values[i]) || double.IsNaN(values[i])) return false;
                double half = values[k + i], lo = values[2 * k + i], hi = values[3 * k + i];
                bool missing = double.IsNaN(half) && double.IsNaN(lo) && double.IsNaN(hi);
                if (computed[i] && !missing) return false;
                if (!missing && (double.IsNaN(half) || double.IsNaN(lo) || double.IsNaN(hi) || half < 0 || lo > hi)) return false;
            }
            if (values[4 * k] < 0 || double.IsInfinity(values[4 * k])) return false;
            if (values[4 * k + 1] != 0 && values[4 * k + 1] != 1) return false;
            result = new ParetoReliability(values.Take(k), values.Skip(k).Take(k), values.Skip(2 * k).Take(k),
                values.Skip(3 * k).Take(k), values[4 * k], values[4 * k + 1] == 1, computed, values.Count == 5 * k + 2);
            return true;
        }

        public string Status(IReadOnlyList<string> names)
        {
            var parts = new List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i], unit = "";
                // Explicit units only, e.g. layer_width [mm] or print_time (min).
                int at = name.LastIndexOf('[');
                if (at < 0) at = name.LastIndexOf('(');
                if (at >= 0 && (name.EndsWith("]", StringComparison.Ordinal) || name.EndsWith(")", StringComparison.Ordinal)))
                { unit = " " + name.Substring(at + 1, name.Length - at - 2); name = name.Substring(0, at).TrimEnd(); }
                string value = Predicted[i].ToString("0.0", CultureInfo.InvariantCulture);
                if (!double.IsNaN(PiHalf[i])) value += " ± " + PiHalf[i].ToString("0.0", CultureInfo.InvariantCulture);
                parts.Add(name + " " + value + unit);
            }
            return string.Join(", ", parts) + (ExtrapolationFlag ? ", outside tested region" : "");
        }
    }
}
