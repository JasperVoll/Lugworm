using System;
using SkiaSharp;

namespace Lugworm.Ui.Console
{
    /// <summary>The console's monochrome icon family. Not the coloured 24x24 Grasshopper
    /// component icons: these are stroked vector glyphs on a normalised 24-unit grid,
    /// drawn at 18-20 px with ~1.75 px strokes, rounded caps and joins, in whatever
    /// colour the control state calls for.</summary>
    public enum ConsoleIcon
    {
        None, Dashboard, Control, Job, Terminal, Logs, Connection, Home, Position, Validate, Upload,
        Start, Pause, Resume, Cancel, Save, Folder, Retry, Sync, Warning, Reset, EStop, Disconnect,
        ArrowUp, ArrowDown, ArrowLeft, ArrowRight, Target, Export, Trash, Check,
    }

    public static class ConsoleIcons
    {
        /// <summary>Draws <paramref name="icon"/> into <paramref name="box"/>.</summary>
        public static void Draw(SKCanvas canvas, ConsoleIcon icon, SKRect box, SKColor color, float strokeWidth)
        {
            if (icon == ConsoleIcon.None) return;
            using (var paint = new SKPaint
            {
                IsAntialias = true,
                Color = color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            })
            using (var fill = new SKPaint { IsAntialias = true, Color = color, Style = SKPaintStyle.Fill })
            {
                canvas.Save();
                float s = Math.Min(box.Width, box.Height) / 24f;
                canvas.Translate(box.Left + (box.Width - 24f * s) / 2f, box.Top + (box.Height - 24f * s) / 2f);
                canvas.Scale(s);
                paint.StrokeWidth = strokeWidth / s;
                Glyph(canvas, icon, paint, fill);
                canvas.Restore();
            }
        }

        static void Glyph(SKCanvas c, ConsoleIcon icon, SKPaint p, SKPaint f)
        {
            switch (icon)
            {
                case ConsoleIcon.Dashboard:
                    c.DrawRoundRect(new SKRect(3, 3, 11, 11), 1.5f, 1.5f, p);
                    c.DrawRoundRect(new SKRect(13, 3, 21, 9), 1.5f, 1.5f, p);
                    c.DrawRoundRect(new SKRect(13, 11, 21, 21), 1.5f, 1.5f, p);
                    c.DrawRoundRect(new SKRect(3, 13, 11, 21), 1.5f, 1.5f, p);
                    break;
                case ConsoleIcon.Control:
                    c.DrawCircle(12, 12, 8.5f, p);
                    c.DrawLine(12, 3.5f, 12, 7.5f, p); c.DrawLine(12, 16.5f, 12, 20.5f, p);
                    c.DrawLine(3.5f, 12, 7.5f, 12, p); c.DrawLine(16.5f, 12, 20.5f, 12, p);
                    c.DrawCircle(12, 12, 1.6f, f);
                    break;
                case ConsoleIcon.Job:
                    Path(c, p, "M6 3 H15 L19 7 V21 H6 Z"); c.DrawLine(15, 3, 15, 7, p); c.DrawLine(19, 7, 15, 7, p);
                    c.DrawLine(9, 12, 16, 12, p); c.DrawLine(9, 16, 16, 16, p);
                    break;
                case ConsoleIcon.Terminal:
                    c.DrawRoundRect(new SKRect(3, 4, 21, 20), 2, 2, p);
                    Path(c, p, "M7 9 L10.5 12 L7 15"); c.DrawLine(12, 15.5f, 17, 15.5f, p);
                    break;
                case ConsoleIcon.Logs:
                    c.DrawLine(4, 6, 20, 6, p); c.DrawLine(4, 10.5f, 20, 10.5f, p);
                    c.DrawLine(4, 15, 14, 15, p); c.DrawLine(4, 19.5f, 11, 19.5f, p);
                    break;
                case ConsoleIcon.Connection:
                    Path(c, p, "M3 11 Q12 3 21 11"); Path(c, p, "M6.5 14.5 Q12 9.5 17.5 14.5"); c.DrawCircle(12, 19, 1.7f, f);
                    break;
                case ConsoleIcon.Disconnect:
                    Path(c, p, "M3 11 Q12 3 21 11"); Path(c, p, "M6.5 14.5 Q12 9.5 17.5 14.5"); c.DrawCircle(12, 19, 1.7f, f);
                    c.DrawLine(4, 21, 20, 4, p);
                    break;
                case ConsoleIcon.Home:
                    Path(c, p, "M4 11.5 L12 4.5 L20 11.5"); Path(c, p, "M6.5 10 V20 H17.5 V10"); c.DrawRect(new SKRect(10, 14, 14, 20), p);
                    break;
                case ConsoleIcon.Position:
                    c.DrawCircle(12, 12, 6.5f, p); c.DrawCircle(12, 12, 1.8f, f);
                    c.DrawLine(12, 2.5f, 12, 6, p); c.DrawLine(12, 18, 12, 21.5f, p);
                    c.DrawLine(2.5f, 12, 6, 12, p); c.DrawLine(18, 12, 21.5f, 12, p);
                    break;
                case ConsoleIcon.Validate:
                case ConsoleIcon.Check:
                    Path(c, p, "M4 12.5 L9.5 18 L20 6.5");
                    break;
                case ConsoleIcon.Upload:
                    Path(c, p, "M12 16 V4"); Path(c, p, "M7 9 L12 4 L17 9"); Path(c, p, "M4 15 V20 H20 V15");
                    break;
                case ConsoleIcon.Start:
                    Path(c, f, "M7 4 L20 12 L7 20 Z");
                    break;
                case ConsoleIcon.Pause:
                    c.DrawRoundRect(new SKRect(6, 4, 10, 20), 1, 1, f); c.DrawRoundRect(new SKRect(14, 4, 18, 20), 1, 1, f);
                    break;
                case ConsoleIcon.Resume:
                    c.DrawRoundRect(new SKRect(4, 4, 7, 20), 1, 1, f); Path(c, f, "M10 4 L21 12 L10 20 Z");
                    break;
                case ConsoleIcon.Cancel:
                    c.DrawRoundRect(new SKRect(5, 5, 19, 19), 2, 2, p);
                    break;
                case ConsoleIcon.Save:
                    Path(c, p, "M4 4 H17 L20 7 V20 H4 Z"); c.DrawRect(new SKRect(8, 4, 15, 9), p); c.DrawRect(new SKRect(7.5f, 13, 16.5f, 20), p);
                    break;
                case ConsoleIcon.Folder:
                    Path(c, p, "M3 6 H9.5 L11.5 8.5 H21 V19 H3 Z");
                    break;
                case ConsoleIcon.Retry:
                    Path(c, p, "M19 12 A7 7 0 1 1 15.5 6"); Path(c, p, "M15.5 2.5 V6.5 H19.5");
                    break;
                case ConsoleIcon.Sync:
                    Path(c, p, "M5 10 A7 7 0 0 1 17.5 7"); Path(c, p, "M17 3.5 L18 7.5 L14 8");
                    Path(c, p, "M19 14 A7 7 0 0 1 6.5 17"); Path(c, p, "M7 20.5 L6 16.5 L10 16");
                    break;
                case ConsoleIcon.Warning:
                    Path(c, p, "M12 3.5 L21.5 20 H2.5 Z"); c.DrawLine(12, 9.5f, 12, 14.5f, p); c.DrawCircle(12, 17.3f, 1.1f, f);
                    break;
                case ConsoleIcon.Reset:
                    Path(c, p, "M5.5 12 A6.5 6.5 0 1 0 8 6.9"); Path(c, p, "M4.5 3.5 V8 H9");
                    break;
                case ConsoleIcon.EStop:
                    c.DrawCircle(12, 12, 8.5f, p); c.DrawCircle(12, 12, 4.5f, f);
                    break;
                case ConsoleIcon.ArrowUp: Path(c, p, "M12 19 V5"); Path(c, p, "M6 11 L12 5 L18 11"); break;
                case ConsoleIcon.ArrowDown: Path(c, p, "M12 5 V19"); Path(c, p, "M6 13 L12 19 L18 13"); break;
                case ConsoleIcon.ArrowLeft: Path(c, p, "M19 12 H5"); Path(c, p, "M11 6 L5 12 L11 18"); break;
                case ConsoleIcon.ArrowRight: Path(c, p, "M5 12 H19"); Path(c, p, "M13 6 L19 12 L13 18"); break;
                case ConsoleIcon.Target:
                    c.DrawCircle(12, 12, 8, p); c.DrawCircle(12, 12, 3.5f, p); c.DrawCircle(12, 12, 1.2f, f);
                    break;
                case ConsoleIcon.Export:
                    Path(c, p, "M12 4 V15"); Path(c, p, "M7.5 10.5 L12 15 L16.5 10.5"); Path(c, p, "M4 17 V20 H20 V17");
                    break;
                case ConsoleIcon.Trash:
                    c.DrawLine(4, 7, 20, 7, p); Path(c, p, "M6 7 L7 20 H17 L18 7"); Path(c, p, "M9.5 7 V4.5 H14.5 V7");
                    c.DrawLine(10, 10.5f, 10, 17, p); c.DrawLine(14, 10.5f, 14, 17, p);
                    break;
            }
        }

        static void Path(SKCanvas c, SKPaint paint, string svg)
        {
            using (var path = SKPath.ParseSvgPathData(svg)) if (path != null) c.DrawPath(path, paint);
        }
    }
}
