using System.Drawing.Drawing2D;

namespace ClaudeUsageWidget;

/// <summary>
/// The app's own logo: a spark of tapered rays (own design, drawn as vectors so it stays
/// crisp at every size / DPI). Used in the taskbar, the widget header and the package icons.
/// </summary>
static class AppLogo
{
    /// <summary>Default logo colour (warm orange).</summary>
    public static readonly Color Orange = Color.FromArgb(0xE0, 0x7A, 0x52);

    const float Box = 24f, C = 12f; // design grid and centre
    const int Rays = 10;

    static readonly Lazy<GraphicsPath> Shape = new(Build);

    /// <summary>
    /// 10 rays alternating long / short, each a slim wedge that is widest near the centre and
    /// ends in a rounded tip; slightly rotated so it doesn't look like a plain asterisk.
    /// </summary>
    static GraphicsPath Build()
    {
        var p = new GraphicsPath(FillMode.Winding);
        for (int i = 0; i < Rays; i++)
        {
            double a = (i * 360.0 / Rays - 81) * Math.PI / 180;
            bool longRay = i % 2 == 0;
            float len = longRay ? 11.2f : 8.6f;          // tip distance from the centre
            float baseW = longRay ? 2.6f : 2.2f;          // width near the centre
            float tipR = longRay ? 0.95f : 0.85f;         // radius of the rounded tip
            const float inner = 1.6f;

            var dir = new PointF((float)Math.Cos(a), (float)Math.Sin(a));
            var nrm = new PointF(-dir.Y, dir.X);
            PointF At(float d, float side) => new(C + dir.X * d + nrm.X * side, C + dir.Y * d + nrm.Y * side);

            float tipD = len - tipR;
            p.AddPolygon(new[]
            {
                At(inner, -baseW / 2), At(tipD, -tipR), At(tipD, tipR), At(inner, baseW / 2),
            });
            var tip = At(tipD, 0);
            p.AddEllipse(tip.X - tipR, tip.Y - tipR, 2 * tipR, 2 * tipR);
        }
        p.AddEllipse(C - 2.1f, C - 2.1f, 4.2f, 4.2f); // small solid core
        return p;
    }

    /// <summary>Draws the logo centred in <paramref name="box"/>.</summary>
    public static void Draw(Graphics g, RectangleF box, Color color)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = Math.Min(box.Width, box.Height) / Box;
        g.TranslateTransform(box.X + (box.Width - Box * scale) / 2, box.Y + (box.Height - Box * scale) / 2);
        g.ScaleTransform(scale, scale);
        using (var brush = new SolidBrush(color))
            g.FillPath(brush, Shape.Value);
        g.Restore(state);
    }

    /// <summary>Renders the logo into a PNG (used for the package / widget icons).</summary>
    public static void SavePng(string file, int size, float padding = 0.1f)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float pad = size * padding;
            Draw(g, new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad), Orange);
        }
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }
}
