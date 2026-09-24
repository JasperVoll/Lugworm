using System.Drawing;
using System.Drawing.Drawing2D;

namespace Lugworm.Ui
{
    /// <summary>Generates simple 24x24 component icons at runtime (a coloured disc with
    /// a letter) so each component is visually distinct without shipping asset files.</summary>
    public static class IconMaker
    {
        /// <summary>Load an embedded 24x24 PNG from Resources\ (logical name
        /// "Lugworm.Resources.&lt;fileName&gt;"). Returns null if not found.</summary>
        public static Bitmap? FromResource(string fileName)
        {
            var asm = typeof(IconMaker).Assembly;
            using var s = asm.GetManifestResourceStream("Lugworm.Resources." + fileName);
            if (s == null)
                return null;

            using var source = new Bitmap(s);
            return new Bitmap(source);
        }

        public static Bitmap Letter(string text, Color bg)
        {
            var bmp = new Bitmap(24, 24);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);
                using (var fill = new SolidBrush(bg))
                    g.FillEllipse(fill, 1, 1, 22, 22);
                using (var pen = new Pen(Color.FromArgb(90, 0, 0, 0)))
                    g.DrawEllipse(pen, 1, 1, 22, 22);
                using (var font = new Font("Arial", 10f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var tb = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(text, font, tb, new RectangleF(0, 1, 24, 24), sf);
                }
            }
            return bmp;
        }
    }
}
