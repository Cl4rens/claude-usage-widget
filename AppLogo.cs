using System.Drawing.Drawing2D;

namespace ClaudeUsageWidget;

/// <summary>
/// The app's own logo: a small gauge (arc + needle), drawn as vectors so it stays crisp
/// at every size / DPI. Used in the taskbar, the widget header and the package icons.
/// </summary>
static class AppLogo
{
    /// <summary>Default logo colour (warm orange).</summary>
    public static readonly Color Orange = Color.FromArgb(0xE0, 0x7A, 0x52);

    const float Box = 24f; // design grid

    /// <summary>Draws the logo centred in <paramref name="box"/>.</summary>
    public static void Draw(Graphics g, RectangleF box, Color color)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = Math.Min(box.Width, box.Height) / Box;
        g.TranslateTransform(box.X + (box.Width - Box * scale) / 2, box.Y + (box.Height - Box * scale) / 2);
        g.ScaleTransform(scale, scale);

        // gauge arc: 240° from lower left over the top to lower right
        using (var arc = new Pen(color, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, new RectangleF(2.5f, 3.5f, 19f, 19f), 150, 240);

        // needle pointing to roughly two thirds
        const float cx = 12f, cy = 13f;
        double a = -40 * Math.PI / 180;
        using (var needle = new Pen(color, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(needle, cx, cy, cx + (float)Math.Cos(a) * 7.2f, cy + (float)Math.Sin(a) * 7.2f);

        using (var hub = new SolidBrush(color))
            g.FillEllipse(hub, cx - 2.4f, cy - 2.4f, 4.8f, 4.8f);

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
