using System;
using SkiaSharp;

namespace Lugworm.Ui.Console
{
    /// <summary>Stable shell geometry, computed without drawing: the left navigation
    /// rail, the status header across the workspace and the page area. The native
    /// safety footer is outside the drawn surface and is not part of this rectangle
    /// (the form docks it below). Everything scales with the theme.</summary>
    public readonly struct ConsoleLayout
    {
        public SKRect Rail { get; }
        public SKRect Header { get; }
        public SKRect Page { get; }
        /// <summary>Content inside the page after padding.</summary>
        public SKRect Content { get; }
        /// <summary>True when the surface is narrower than the 820 px design minimum;
        /// pages then stack their columns.</summary>
        public bool Narrow { get; }

        public ConsoleLayout(ConsoleTheme t, int width, int height)
        {
            Rail = new SKRect(0, 0, t.RailWidth, height);
            Header = new SKRect(t.RailWidth, 0, width, t.HeaderHeight);
            Page = new SKRect(t.RailWidth, t.HeaderHeight, width, height);
            Content = new SKRect(Page.Left + t.Pad, Page.Top + t.Pad, Page.Right - t.Pad, Page.Bottom - t.Pad);
            Narrow = width < t.Px(820f) - 1;
        }

        /// <summary>Splits a rectangle into <paramref name="columns"/> equal columns with a gap.</summary>
        public static SKRect[] Columns(SKRect area, int columns, float gap)
        {
            var result = new SKRect[Math.Max(1, columns)];
            float w = (area.Width - gap * (result.Length - 1)) / result.Length;
            for (int i = 0; i < result.Length; i++)
                result[i] = new SKRect(area.Left + i * (w + gap), area.Top, area.Left + i * (w + gap) + w, area.Bottom);
            return result;
        }

        /// <summary>A row of the given height at the top of an area; the area shrinks.</summary>
        public static SKRect Take(ref SKRect area, float height, float gap)
        {
            var row = new SKRect(area.Left, area.Top, area.Right, Math.Min(area.Bottom, area.Top + height));
            area = new SKRect(area.Left, row.Bottom + gap, area.Right, area.Bottom);
            return row;
        }
    }
}
