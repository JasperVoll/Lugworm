using System;
using System.Globalization;

namespace Lugworm.Core.Gcode
{
    /// <summary>The exact layer-marker format emitted by the G-code builder.</summary>
    public static class LayerMarker
    {
        public static string Format(int layer, int total)
        {
            if (layer < 1) throw new ArgumentOutOfRangeException(nameof(layer));
            if (total < layer) throw new ArgumentOutOfRangeException(nameof(total));
            return "; layer " + layer.ToString(CultureInfo.InvariantCulture) + "/"
                 + total.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Reads only the whole-line form written by <see cref="Format"/>.</summary>
        public static bool TryParseExact(string? line, out int layer, out int total)
        {
            layer = 0;
            total = 0;
            if (line == null || !line.StartsWith("; layer ", StringComparison.Ordinal)) return false;
            int slash = line.IndexOf('/', "; layer ".Length);
            if (slash < 0 || slash == "; layer ".Length || slash == line.Length - 1) return false;
            if (!int.TryParse(line.Substring("; layer ".Length, slash - "; layer ".Length),
                              NumberStyles.None, CultureInfo.InvariantCulture, out layer)
                || !int.TryParse(line.Substring(slash + 1), NumberStyles.None,
                                 CultureInfo.InvariantCulture, out total))
            {
                layer = 0;
                total = 0;
                return false;
            }
            if (layer < 1 || total < layer) { layer = 0; total = 0; return false; }
            string canonical = Format(layer, total);
            if (!string.Equals(line, canonical, StringComparison.Ordinal))
            {
                layer = 0;
                total = 0;
                return false;
            }
            return true;
        }
    }
}
