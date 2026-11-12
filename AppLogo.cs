using System.Drawing.Drawing2D;

namespace ClaudeUsageWidget;

/// <summary>
/// The app's own logo: an organic spark of tapered rays (own design, drawn as vectors so it stays
/// crisp at every size / DPI). Used in the taskbar, the widget header and the package icons.
/// </summary>
static class AppLogo
{
    /// <summary>Default logo colour (warm orange).</summary>
    public static readonly Color Orange = Color.FromArgb(0xE0, 0x7A, 0x52);

    const float Box = 24f, C = 12f; // design grid and centre

    static readonly Lazy<GraphicsPath> Shape = new(Build);

    // 12 rays with a hand-drawn irregularity – fixed values, so the logo is the same everywhere.
    static readonly double[] AngleJitter = { 0, 4, -3, 6, -2, 3, -5, 2, 5, -4, 1, -2 };          // degrees
    static readonly float[] Length = { 11.4f, 9.0f, 10.6f, 8.4f, 11.6f, 9.6f, 10.2f, 8.8f, 11.0f, 9.2f, 10.8f, 8.6f };
    static readonly float[] BaseWidth = { 2.5f, 2.0f, 2.3f, 1.9f, 2.6f, 2.1f, 2.3f, 1.9f, 2.4f, 2.0f, 2.3f, 1.9f };

    /// <summary>
    /// 12 slim wedges, widest near the centre and ending in a rounded tip, with slightly uneven
    /// angles, lengths and widths so the spark looks organic rather than like a plain asterisk.
    /// </summary>
    static GraphicsPath Build()
    {
        const float inner = 1.2f, tipR = 0.8f;
        var p = new GraphicsPath(FillMode.Winding);
        for (int i = 0; i < Length.Length; i++)
        {
            double a = (i * 30 - 75 + AngleJitter[i]) * Math.PI / 180;
            var dir = new PointF((float)Math.Cos(a), (float)Math.Sin(a));
            var nrm = new PointF(-dir.Y, dir.X);
            PointF At(float d, float side) => new(C + dir.X * d + nrm.X * side, C + dir.Y * d + nrm.Y * side);

            float tipD = Length[i] - tipR;
            p.AddPolygon(new[]
            {
                At(inner, -BaseWidth[i] / 2), At(tipD, -tipR), At(tipD, tipR), At(inner, BaseWidth[i] / 2),
            });
            var tip = At(tipD, 0);
            p.AddEllipse(tip.X - tipR, tip.Y - tipR, 2 * tipR, 2 * tipR);
        }
        p.AddEllipse(C - 1.7f, C - 1.7f, 3.4f, 3.4f); // small solid core
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
