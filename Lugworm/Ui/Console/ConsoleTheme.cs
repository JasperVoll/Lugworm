using System;
using SkiaSharp;

namespace Lugworm.Ui.Console
{
    /// <summary>Colours and metrics of the Duet Console: the dark graphite workshop theme
    /// of the 2026-09-04 design (one theme; the console is a workshop surface, not a
    /// canvas element, so it does not follow the host's light/dark choice). Saturated
    /// colour means action or state, never decoration; solid red is E-STOP alone.</summary>
    public sealed class ConsoleTheme
    {
        /// <summary>Kept for the form's constructor contract; the console is always graphite.</summary>
        public bool Dark { get; }
        /// <summary>Device pixels per logical pixel. Every metric below is already scaled,
        /// so layout code never multiplies by the DPI itself.</summary>
        public float Scale { get; }

        // ---- palette (design spec tokens) ----
        public SKColor Background { get; } = new SKColor(0x11, 0x14, 0x17);
        public SKColor Navigation { get; } = new SKColor(0x17, 0x1B, 0x20);
        public SKColor Panel { get; } = new SKColor(0x1E, 0x24, 0x2B);
        public SKColor PanelElevated { get; } = new SKColor(0x25, 0x2C, 0x34);
        public SKColor Border { get; } = new SKColor(0x36, 0x41, 0x4C);
        public SKColor BorderStrong { get; } = new SKColor(0x4A, 0x58, 0x66);
        public SKColor Text { get; } = new SKColor(0xF2, 0xF5, 0xF7);
        public SKColor TextSecondary { get; } = new SKColor(0xAA, 0xB4, 0xBF);
        public SKColor TextMuted { get; } = new SKColor(0x74, 0x81, 0x8E);
        public SKColor Primary { get; } = new SKColor(0x43, 0x8C, 0xFF);
        public SKColor PrimaryHover { get; } = new SKColor(0x5A, 0x9B, 0xFF);
        public SKColor PrimaryPressed { get; } = new SKColor(0x32, 0x75, 0xD6);
        public SKColor Success { get; } = new SKColor(0x39, 0xB9, 0x80);
        public SKColor Warning { get; } = new SKColor(0xF0, 0xB4, 0x4D);
        public SKColor Danger { get; } = new SKColor(0xE5, 0x48, 0x4D);
        public SKColor Home { get; } = new SKColor(0xB9, 0x82, 0x4C);
        public SKColor Disabled { get; } = new SKColor(0x2A, 0x30, 0x37);

        // ---- compatibility names used by older call sites ----
        public SKColor Accent => Primary;
        public SKColor Secondary => PanelElevated;
        public SKColor Error => Danger;

        public ConsoleTheme(float scale)
        {
            Dark = true;
            Scale = scale <= 0 ? 1f : scale;
        }

        public float Px(float units) => units * Scale;

        // ---- typography (logical px per the spec) ----
        public float FontSmall => Px(12f);
        public float FontBody => Px(14f);
        public float FontButton => Px(15f);
        public float FontHeader => Px(17f);
        public float FontValue => Px(24f);

        // ---- metrics ----
        public float Unit => Px(4f);
        public float Gap => Px(8f);
        public float GapLarge => Px(12f);
        public float Pad => Px(16f);
        public float PadLarge => Px(24f);
        public float RowHeight => Px(30f);
        public float ButtonHeight => Px(38f);
        public float PrimaryButtonHeight => Px(42f);
        public float Corner => Px(6f);
        public float CardCorner => Px(8f);
        public float IconSize => Px(19f);
        public float RailWidth => Px(168f);
        public float HeaderHeight => Px(60f);

        /// <summary>Text colour on a filled role swatch.</summary>
        public SKColor TextOn(ConsoleRole role)
        {
            switch (role)
            {
                case ConsoleRole.Warning: return new SKColor(0x1A, 0x14, 0x06);
                case ConsoleRole.Destructive: return Danger;
                default: return Text;
            }
        }

        static SKTypeface? _regular, _semibold, _mono;
        public static SKTypeface Regular => _regular ??= SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
        public static SKTypeface Semibold => _semibold ??=
            SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            ?? SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        public static SKTypeface Mono => _mono ??= SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default;
    }

    public enum ConsoleRole { Primary, Home, Secondary, Warning, Error, Destructive }
}
