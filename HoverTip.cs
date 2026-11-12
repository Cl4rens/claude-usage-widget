using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ClaudeUsageWidget;

/// <summary>
/// Tooltip shown while hovering the taskbar display: usage and reset time of both limits.
/// A borderless, non-activating, click-through topmost window with Windows 11 rounded corners.
/// </summary>
sealed class HoverTip : Form
{
    /// <summary>Title + value on one line, detail below; optional account header above it.</summary>
    public record Block(string Title, string Value, string Detail, string? Header = null, bool IsError = false);

    Block[] _blocks = [];
    string? _footer;
    bool _dark;

    public HoverTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x8 /*TOPMOST*/ | 0x20 /*TRANSPARENT*/;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 3; // DWMWCP_ROUNDSMALL, like native tooltips
        DwmSetWindowAttribute(Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, sizeof(int));
    }

    float S => DeviceDpi / 96f;
    Font TitleFont() => new("Segoe UI", 12f * S, FontStyle.Regular, GraphicsUnit.Pixel);
    Font ValueFont() => new("Segoe UI Semibold", 12f * S, FontStyle.Regular, GraphicsUnit.Pixel);
    Font DetailFont() => new("Segoe UI", 12f * S, FontStyle.Regular, GraphicsUnit.Pixel);

    const float Pad = 10, BlockGap = 8, LineGap = 2;

    /// <summary>Shows the tip centred above <paramref name="anchor"/> (screen coordinates).</summary>
    public void ShowAbove(Rectangle anchor, Block[] blocks, string? footer, bool dark)
    {
        _blocks = blocks; _footer = footer; _dark = dark;
        BackColor = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);

        using (var g = CreateGraphics())
        {
            var size = Measure(g);
            var area = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.Left + (anchor.Width - size.Width) / 2;
            x = Math.Clamp(x, area.Left + 4, area.Right - size.Width - 4);
            int y = anchor.Top - size.Height - (int)(8 * S);
            if (y < area.Top) y = anchor.Bottom + (int)(8 * S); // taskbar at the top
            Bounds = new Rectangle(x, y, size.Width, size.Height);
        }
        Invalidate();
        if (!Visible) Show();
    }

    Size Measure(Graphics g)
    {
        using var tf = TitleFont(); using var vf = ValueFont(); using var df = DetailFont();
        float w = 0, h = Pad * S;
        foreach (var b in _blocks)
        {
            if (b.Header != null) { w = Math.Max(w, g.MeasureString(b.Header, vf).Width); h += vf.GetHeight(g) + LineGap * S; }
            if (b.Title.Length > 0)
            {
                w = Math.Max(w, g.MeasureString(b.Title, tf).Width + g.MeasureString("  " + b.Value, vf).Width);
                h += tf.GetHeight(g) + LineGap * S;
            }
            w = Math.Max(w, g.MeasureString(b.Detail, df).Width);
            h += df.GetHeight(g) + BlockGap * S;
        }
        if (_footer != null) { w = Math.Max(w, g.MeasureString(_footer, df).Width); h += df.GetHeight(g); }
        else h -= BlockGap * S;
        h += Pad * S;
        return new Size((int)Math.Ceiling(w + 2 * Pad * S), (int)Math.Ceiling(h));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var fg = _dark ? Color.White : Color.FromArgb(26, 26, 26);
        var muted = _dark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(96, 96, 96);
        using var tf = TitleFont(); using var vf = ValueFont(); using var df = DetailFont();
        using var fgB = new SolidBrush(fg); using var mutedB = new SolidBrush(muted);

        // thin border like native tooltips
        using (var pen = new Pen(_dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(214, 214, 214)))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        float x = Pad * S, y = Pad * S;
        using var accentB = new SolidBrush(AppLogo.Orange);
        foreach (var b in _blocks)
        {
            if (b.Header != null)
            {
                g.DrawString(b.Header, vf, accentB, x, y);
                y += vf.GetHeight(g) + LineGap * S;
            }
            if (b.Title.Length > 0)
            {
                g.DrawString(b.Title, tf, fgB, x, y);
                g.DrawString("  " + b.Value, vf, fgB, x + g.MeasureString(b.Title, tf).Width, y);
                y += tf.GetHeight(g) + LineGap * S;
            }
            g.DrawString(b.Detail, df, b.IsError ? Brushes.IndianRed : mutedB, x, y);
            y += df.GetHeight(g) + BlockGap * S;
        }
        if (_footer != null) g.DrawString(_footer, df, mutedB, x, y);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
