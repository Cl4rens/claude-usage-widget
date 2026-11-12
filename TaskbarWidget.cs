using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

/// <summary>
/// A per-pixel-alpha child window that lives *inside* Shell_TrayWnd (like the weather button),
/// so it moves/hides together with the taskbar and needs no always-on-top hacks.
/// Layout: two concentric usage rings (outer = week, inner = 5h) + two text lines.
/// </summary>
sealed class TaskbarWidget : NativeWindow, IDisposable
{
    const float GapToNeighbour = 6;   // px between us and the weather button / tray
    const float PadLeft = 8, PadRight = 12, RingSize = 30, IconSize = 20, RingGap = 9;
    const float FontPx = 12.5f, LineHeight = 17;

    readonly System.Windows.Forms.Timer _layoutTimer = new() { Interval = 1000 };
    readonly MenuOwner _menuOwner = new();
    readonly HoverTip _tip = new();
    readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = 450 }; // hover delay like native tooltips
    UsageSnapshot? _snap;
    string? _error;
    bool _visible, _hover, _pressed;
    IntPtr _taskbar;
    Rectangle _bounds; // in taskbar client coordinates
    float _scale = 1;

    /// <summary>Right-click menu entries.</summary>
    public List<(string Text, Action Run)> MenuItems { get; } = new();

    public TaskbarWidget()
    {
        // Also handles explorer restarts and tray width changes.
        _layoutTimer.Tick += (_, _) => { if (_visible) { ReloadSettingsIfChanged(); EnsureAttached(); } };
        _layoutTimer.Start();
        _tipTimer.Tick += (_, _) => { if (_hover) ShowTip(); else _tipTimer.Stop(); };
        SystemEvents.UserPreferenceChanged += (_, _) => { ApplyMenuTheme(); EnsureAttached(); Render(); };
        ApplyMenuTheme();
    }

    public void Show() { _visible = true; EnsureAttached(); }

    public void SetData(UsageSnapshot? snap, string? error)
    {
        if (snap != null) _snap = snap;
        _error = error;
        EnsureAttached(); // width depends on the text
        Render();
        if (_tip.Visible) ShowTip(); // keep an open tooltip up to date
    }

    // ---------------------------------------------------------------- hover tooltip

    void ShowTip()
    {
        _tipTimer.Stop();
        if (Handle == IntPtr.Zero || !GetWindowRect(Handle, out var wr)) return;
        var blocks = new List<HoverTip.Block>();
        if (_snap != null)
        {
            HoverTip.Block B(string title, UsageWindow w) => new(title, L.Used(w.Percent),
                w.ResetsAt is { } r ? $"{L.ResetAt(r)} · {L.In(r - DateTimeOffset.Now)}" : "");
            blocks.Add(B(L.SessionLong, _snap.FiveHour));
            blocks.Add(B(L.WeekLong, _snap.SevenDay));
        }
        string footer = _snap != null ? $"{L.AsOf} {_snap.FetchedAt:HH:mm}" : "";
        if (_error != null) footer = (footer.Length > 0 ? footer + " · " : "") + _error;
        _tip.ShowAbove(new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top),
            blocks.ToArray(), footer.Length > 0 ? footer : null, dark: !Theme.IsLight());
    }

    void HideTip()
    {
        _tipTimer.Stop();
        if (_tip.Visible) _tip.Hide();
    }

    public static string FormatReset(DateTimeOffset r)
    {
        var local = r.ToLocalTime().AddSeconds(30); // API sends e.g. 04:59:59 → show 05:00
        return (local - DateTimeOffset.Now).TotalHours < 24 ? local.ToString("HH:mm") : local.ToString("ddd HH:mm");
    }

    // ---------------------------------------------------------------- settings / text content

    TaskbarSettings _cfg = TaskbarSettings.Load();
    DateTime _cfgStamp = TaskbarSettings.LastWrite;
    bool Bars => _cfg.Style == "bars";
    /// <summary>Text colour: user choice, or follow the Windows taskbar theme ("auto").</summary>
    bool DarkText => _cfg.TextColor switch { "dark" => true, "light" => false, _ => Theme.IsLight() };
    /// <summary>Width of the graphic left of the text incl. gap (0 for bars without logo).</summary>
    float IconColumn => Bars ? (_cfg.ShowLogo ? IconSize + RingGap : 0) : RingSize + RingGap;

    /// <summary>Settings are changed from the Widgets board ("Widget anpassen") – maybe in another process.</summary>
    void ReloadSettingsIfChanged()
    {
        var stamp = TaskbarSettings.LastWrite;
        if (stamp == _cfgStamp) return;
        _cfgStamp = stamp;
        _cfg = TaskbarSettings.Load();
        EnsureAttached();
        Render();
    }

    record Row(string Label, string Value, string Reset);

    Row[] Rows()
    {
        static string Pct(UsageWindow w) => w.Percent is null ? "–" : $"{w.Percent:0}%";
        string Rst(UsageWindow w) => _cfg.ShowResets && w.ResetsAt is { } r ? FormatReset(r) : "";
        if (_snap == null) return [];
        return [new("5h", Pct(_snap.FiveHour), Rst(_snap.FiveHour)), new(_cfg.Compact ? L.WeekShort : L.Week, Pct(_snap.SevenDay), Rst(_snap.SevenDay))];
    }

    static readonly StringFormat Typo = CreateTypo();
    static StringFormat CreateTypo()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        return f;
    }

    Font ValueFont() => new("Segoe UI Semibold", FontPx * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
    Font LabelFont() => new("Segoe UI", FontPx * _scale, FontStyle.Regular, GraphicsUnit.Pixel);

    const float BarWidth = 44, Gap = 6;

    /// <summary>Column widths of the text area for the current style (0 = column not shown).</summary>
    record Cols(float Label, float Bar, float Value, float Reset)
    {
        public float Total(float gap)
        {
            var c = new[] { Label, Bar, Value, Reset }.Where(v => v > 0).ToArray();
            return c.Sum() + gap * Math.Max(0, c.Length - 1);
        }
    }

    Cols Measure(Graphics g)
    {
        using var vf = ValueFont();
        using var lf = LabelFont();
        float W(string t, Font f) => t.Length == 0 ? 0 : g.MeasureString(t, f, 1000, Typo).Width;
        var rows = Rows();
        if (rows.Length == 0)
            return new Cols(0, 0, Math.Max(W("Claude", vf), W(_error ?? L.Loading, lf)), 0);
        float label = rows.Max(r => W(r.Label, lf));
        float value = rows.Max(r => W(r.Value, vf));
        float reset = rows.Max(r => W(r.Reset, lf));
        if (Bars) return new Cols(label, BarWidth * _scale, value, reset);              // 5h  ▬▬▬  11%  15:10
        float rest = rows.Max(r => W(r.Label + (r.Reset.Length > 0 ? " · " + r.Reset : ""), lf));
        return new Cols(0, 0, value, rest);                                              // 11%  5h · 15:10
    }

    int DesiredWidth()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        if (Wide) return WideWidth(g);
        float text = Measure(g).Total(Gap * _scale);
        float w = (PadLeft + IconColumn + PadRight) * _scale + text;
        return (int)Math.Ceiling(Math.Min(w, 320 * _scale));
    }

    // ---------------------------------------------------------------- wide mode (5h and week side by side)

    const float WideRing = 26, WideItemGap = 16, WideBarMin = 48;
    bool Wide => _cfg.Wide && _snap != null;
    float WideLogoColumn => _cfg.ShowLogo ? IconSize + RingGap : 0;

    /// <summary>Width of one wide item (5h or week).</summary>
    float WideItemWidth(Graphics g, Row r, Font vf, Font lf)
    {
        float W(string t, Font f) => t.Length == 0 ? 0 : g.MeasureString(t, f, 1000, Typo).Width;
        float gap = Gap * _scale;
        if (Bars)
        {
            // line 1: "5h  27%  15:10", line 2: bar across the item
            float line = W(r.Label, lf) + gap + W(r.Value, vf) + (r.Reset.Length > 0 ? gap + W(r.Reset, lf) : 0);
            return Math.Max(line, WideBarMin * _scale);
        }
        // ring + two lines: "27%" / "5h · 15:10"
        float text = Math.Max(W(r.Value, vf), W(r.Label + (r.Reset.Length > 0 ? " · " + r.Reset : ""), lf));
        return (WideRing + RingGap) * _scale + text;
    }

    int WideWidth(Graphics g)
    {
        using var vf = ValueFont();
        using var lf = LabelFont();
        var rows = Rows();
        float items = rows.Sum(r => WideItemWidth(g, r, vf, lf)) + WideItemGap * _scale * (rows.Length - 1);
        return (int)Math.Ceiling((PadLeft + WideLogoColumn + PadRight) * _scale + items);
    }

    void PaintWide(Graphics g, Size size, Color fgC, Color mutedC, Color trackC, Color sessionC, Color weekC, Color logoC)
    {
        float s = _scale, cy = size.Height / 2f, gap = Gap * s;
        using var vf = ValueFont();
        using var lf = LabelFont();
        using var fg = new SolidBrush(fgC);
        using var muted = new SolidBrush(mutedC);
        float top = cy - LineHeight * s;
        float TextY(int i) => top + i * LineHeight * s + (LineHeight * s - vf.GetHeight(g)) / 2;
        float W(string t, Font f) => t.Length == 0 ? 0 : g.MeasureString(t, f, 1000, Typo).Width;

        float x = PadLeft * s;
        if (_cfg.ShowLogo)
        {
            AppLogo.Draw(g, new RectangleF(x, cy - IconSize * s / 2, IconSize * s, IconSize * s), logoC);
            x += (IconSize + RingGap) * s;
        }
        if (_error != null) // stale data marker
            g.FillEllipse(Brushes.IndianRed, x - RingGap * s - 3 * s, cy - 13 * s, 5 * s, 5 * s);

        var rows = Rows();
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            var pct = i == 0 ? _snap!.FiveHour.Percent : _snap!.SevenDay.Percent;
            var color = i == 0 ? sessionC : weekC;
            float itemW = WideItemWidth(g, r, vf, lf);

            if (Bars)
            {
                float tx = x;
                g.DrawString(r.Label, lf, muted, tx, TextY(0), Typo); tx += W(r.Label, lf) + gap;
                g.DrawString(r.Value, vf, fg, tx, TextY(0), Typo); tx += W(r.Value, vf) + gap;
                if (r.Reset.Length > 0) g.DrawString(r.Reset, lf, muted, tx, TextY(0), Typo);

                float barH = 5 * s, by = top + LineHeight * s + (LineHeight * s - barH) / 2;
                using (var tb = new SolidBrush(trackC))
                using (var tp = TaskbarApp.RoundedRect(new Rectangle((int)x, (int)by, (int)itemW, (int)barH), (int)(barH / 2)))
                    g.FillPath(tb, tp);
                float frac = (float)Math.Clamp((pct ?? 0) / 100.0, 0, 1);
                if (frac > 0)
                {
                    using var fb = new SolidBrush(color);
                    using var fp = TaskbarApp.RoundedRect(new Rectangle((int)x, (int)by, (int)Math.Max(barH, itemW * frac), (int)barH), (int)(barH / 2));
                    g.FillPath(fb, fp);
                }
            }
            else
            {
                float rcx = x + WideRing * s / 2;
                DrawRing(g, rcx, cy, (WideRing / 2 - 1.8f) * s, 3.2f * s, pct, trackC, color);
                float tx = x + (WideRing + RingGap) * s;
                g.DrawString(r.Value, vf, fg, tx, TextY(0), Typo);
                g.DrawString(r.Label + (r.Reset.Length > 0 ? " · " + r.Reset : ""), lf, muted, tx, TextY(1), Typo);
            }
            x += itemW + WideItemGap * s;
        }
    }

    // ---------------------------------------------------------------- attach / layout

    /// <summary>Left edge (taskbar client coords) of the area we sit next to: the tray, or the
    /// Windows weather/widgets button if that sits directly left of the tray.</summary>
    int TrayLeft(RECT tbRect, RECT client)
    {
        int left = client.Right - (int)(280 * _scale);
        var tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var tr)) left = tr.Left - tbRect.Left;

        var wb = _widgetsButton; // screen coords, refreshed in the background
        if (wb is { } r && r.Width > 0 && r.Left - tbRect.Left > client.Right / 2)
            left = Math.Min(left, r.Left - tbRect.Left);
        return left;
    }

    // The weather button is XAML (no HWND) → find it via UI Automation, off the UI thread.
    Rectangle? _widgetsButton;
    DateTime _lastUiaLookup;
    bool _uiaBusy;

    void RefreshWidgetsButtonAsync()
    {
        if (_uiaBusy || DateTime.Now - _lastUiaLookup < TimeSpan.FromSeconds(10)) return;
        _uiaBusy = true;
        _lastUiaLookup = DateTime.Now;
        var tb = _taskbar;
        Task.Run(() =>
        {
            try
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(tb);
                var el = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.AutomationIdProperty, "WidgetsButton"));
                if (el == null || el.Current.IsOffscreen) return (Rectangle?)null;
                var b = el.Current.BoundingRectangle;
                return new Rectangle((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height);
            }
            catch { return null; }
        }).ContinueWith(t =>
        {
            _uiaBusy = false;
            if (t.Result != _widgetsButton) { _widgetsButton = t.Result; if (_visible) EnsureAttached(); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void EnsureAttached()
    {
        if (!_visible) return;
        var tb = FindWindow("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero) return; // explorer not running (yet)

        if (Handle != IntPtr.Zero && (tb != _taskbar || !IsWindow(_taskbar)))
            DestroyHandle(); // explorer restarted → re-attach to the new taskbar

        _taskbar = tb;
        _scale = GetDpiForWindow(tb) / 96f;
        RefreshWidgetsButtonAsync();

        GetClientRect(tb, out var client);
        GetWindowRect(tb, out var tbRect);
        int w = DesiredWidth(), h = client.Bottom;
        int x = TrayLeft(tbRect, client) - w - (int)(GapToNeighbour * _scale);
        x = Math.Clamp(x, 0, Math.Max(0, client.Right - w));
        var newBounds = new Rectangle(x, 0, w, h);

        if (Handle == IntPtr.Zero)
        {
            _bounds = newBounds;
            CreateHandle(new CreateParams
            {
                Caption = "ClaudeUsageWidget",
                Parent = tb,
                X = x, Y = 0, Width = w, Height = h,
                Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            });
            Render();
        }
        else if (newBounds != _bounds)
        {
            _bounds = newBounds;
            Render();
        }

        // stay above the XAML taskbar content (sibling z-order)
        if (Handle != IntPtr.Zero)
            SetWindowPos(Handle, HWND_TOP, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // ---------------------------------------------------------------- rendering

    void Render()
    {
        if (Handle == IntPtr.Zero || _bounds.Width <= 0 || _bounds.Height <= 0) return;
        using var bmp = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha 1 → invisible, but still receives mouse input
            Paint(g, bmp.Size);
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { x = _bounds.X, y = _bounds.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    static Color Lighten(Color c, float amount) => Color.FromArgb(c.A,
        (int)(c.R + (255 - c.R) * amount), (int)(c.G + (255 - c.G) * amount), (int)(c.B + (255 - c.B) * amount));

    void Paint(Graphics g, Size size)
    {
        float s = _scale;
        bool light = DarkText; // "light" = light taskbar → dark text
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var fgC = light ? Color.FromArgb(28, 28, 28) : Color.White;
        var mutedC = light ? Color.FromArgb(170, 0, 0, 0) : Color.FromArgb(200, 255, 255, 255);
        var trackC = light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(55, 255, 255, 255);

        // hover / pressed background like native taskbar buttons
        if (_hover || _pressed)
        {
            int a = _pressed ? 10 : 18;
            using var hb = new SolidBrush(light ? Color.FromArgb(a + 10, 0, 0, 0) : Color.FromArgb(a, 255, 255, 255));
            int pad = (int)(4 * s);
            using var path = TaskbarApp.RoundedRect(new Rectangle(0, pad, size.Width - 1, size.Height - 2 * pad - 1), (int)(5 * s));
            g.FillPath(hb, path);
        }

        // colours for the two limits
        Color weekC, sessionC;
        if (Theme.AccentTaskbar() && !light)
        {
            // coloured taskbar: orange has no contrast → white, warn colour only when it matters
            sessionC = (_snap?.FiveHour.Percent ?? 0) >= 90 ? Color.FromArgb(255, 210, 90) : Color.White;
            weekC = (_snap?.SevenDay.Percent ?? 0) >= 90 ? Color.FromArgb(255, 210, 90) : Color.FromArgb(185, 255, 255, 255);
        }
        else
        {
            sessionC = Theme.ForPercent(_snap?.FiveHour.Percent ?? 0);
            weekC = Lighten(Theme.ForPercent(_snap?.SevenDay.Percent ?? 0), 0.35f);
        }

        float cy = size.Height / 2f;
        float iconSize = Bars ? IconSize : RingSize;
        float cx = (PadLeft + iconSize / 2) * s;
        // logo orange, except on an accent-coloured taskbar where it would have no contrast
        var logoC = Theme.AccentTaskbar() && !light ? Color.White : AppLogo.Orange;
        if (Wide)
        {
            PaintWide(g, size, fgC, mutedC, trackC, sessionC, weekC, logoC);
            return;
        }
        if (Bars)
        {
            if (_cfg.ShowLogo)
                AppLogo.Draw(g, new RectangleF(cx - IconSize * s / 2, cy - IconSize * s / 2, IconSize * s, IconSize * s), logoC);
        }
        else
        {
            float thick = 3.2f * s;
            DrawRing(g, cx, cy, (RingSize / 2 - 1.8f) * s, thick, _snap?.SevenDay.Percent, trackC, weekC);
            DrawRing(g, cx, cy, (RingSize / 2 - 6.6f) * s, thick, _snap?.FiveHour.Percent, trackC, sessionC);
            if (_cfg.ShowLogo) // small logo in the middle of the rings
            {
                float ls = 8.5f * s;
                AppLogo.Draw(g, new RectangleF(cx - ls / 2, cy - ls / 2, ls, ls), logoC);
            }
        }
        if (_error != null && _snap != null) // stale data marker
            g.FillEllipse(Brushes.IndianRed, cx + 8 * s, cy - 13 * s, 5 * s, 5 * s);

        // ---- text
        using var vf = ValueFont();
        using var lf = LabelFont();
        using var fg = new SolidBrush(fgC);
        using var muted = new SolidBrush(mutedC);
        var cols = Measure(g);
        float x0 = (PadLeft + IconColumn) * s, gap = Gap * s;
        float top = cy - LineHeight * s;
        float TextY(int i) => top + i * LineHeight * s + (LineHeight * s - vf.GetHeight(g)) / 2;

        var rows = Rows();
        if (rows.Length == 0)
        {
            g.DrawString("Claude", vf, fg, x0, TextY(0), Typo);
            g.DrawString(_error ?? L.Loading, lf, _error != null ? Brushes.IndianRed : muted, x0, TextY(1), Typo);
            return;
        }

        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            float y = TextY(i), x = x0;
            if (Bars)
            {
                g.DrawString(r.Label, lf, muted, x, y, Typo);
                x += cols.Label + gap;
                float barH = 5 * s, by = top + i * LineHeight * s + (LineHeight * s - barH) / 2 + 0.5f * s;
                var pct = i == 0 ? _snap!.FiveHour.Percent : _snap!.SevenDay.Percent;
                using (var tb = new SolidBrush(trackC))
                using (var tp = TaskbarApp.RoundedRect(new Rectangle((int)x, (int)by, (int)cols.Bar, (int)barH), (int)(barH / 2)))
                    g.FillPath(tb, tp);
                float frac = (float)Math.Clamp((pct ?? 0) / 100.0, 0, 1);
                if (frac > 0)
                {
                    using var fb = new SolidBrush(i == 0 ? sessionC : weekC);
                    using var fp = TaskbarApp.RoundedRect(new Rectangle((int)x, (int)by, (int)Math.Max(barH, cols.Bar * frac), (int)barH), (int)(barH / 2));
                    g.FillPath(fb, fp);
                }
                x += cols.Bar + gap;
                g.DrawString(r.Value, vf, fg, x, y, Typo);
                x += cols.Value + gap;
                if (r.Reset.Length > 0) g.DrawString(r.Reset, lf, muted, x, y, Typo);
            }
            else
            {
                g.DrawString(r.Value, vf, fg, x, y, Typo);
                x += cols.Value + gap;
                g.DrawString(r.Label + (r.Reset.Length > 0 ? " · " + r.Reset : ""), lf, muted, x, y, Typo);
            }
        }
    }

    static void DrawRing(Graphics g, float cx, float cy, float r, float thick, double? pct, Color track, Color fill)
    {
        var rect = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
        using (var tp = new Pen(track, thick))
            g.DrawEllipse(tp, rect);
        float sweep = (float)Math.Clamp((pct ?? 0) / 100.0 * 360, 0, 360);
        if (sweep <= 0) return;
        using var fp = new Pen(fill, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(fp, rect, -90, Math.Max(sweep, 4));
    }

    // ---------------------------------------------------------------- input

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEMOVE:
                if (!_hover)
                {
                    _hover = true;
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    TrackMouseEvent(ref tme);
                    Render();
                    _tipTimer.Start();
                }
                break;

            case WM_MOUSELEAVE:
                _hover = false;
                HideTip();
                Render();
                break;

            case WM_LBUTTONDOWN:
                HideTip();
                _pressed = true;
                SetCapture(Handle);
                Render();
                break;

            case WM_LBUTTONUP:
                ReleaseCapture();
                bool inside = _pressed && GetWindowRect(Handle, out var wr) &&
                              new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top).Contains(Cursor.Position);
                _pressed = false;
                Render();
                if (inside) OpenWidgetsBoard(); // same behaviour as the weather button
                break;

            case WM_RBUTTONUP:
                HideTip();
                ShowMenu();
                break;

            case WM_MOUSEACTIVATE:
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Native popup menu, owned by a hidden top-level window that we bring to the foreground first –
    /// the same trick tray icons use, so a click anywhere else closes the menu.
    /// </summary>
    void ShowMenu()
    {
        if (MenuItems.Count == 0) return;
        IntPtr menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < MenuItems.Count; i++)
                AppendMenu(menu, MF_STRING, (UIntPtr)(i + 1), MenuItems[i].Text);

            var owner = _menuOwner.EnsureHandle();
            SetForegroundWindow(owner);
            var pt = Cursor.Position;
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, pt.X, pt.Y, owner, IntPtr.Zero);
            PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd > 0 && cmd <= MenuItems.Count) MenuItems[cmd - 1].Run();
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>Make native menus dark/light like the taskbar (undocumented uxtheme ordinals, Win10 1903+).</summary>
    static void ApplyMenuTheme()
    {
        try
        {
            SetPreferredAppMode(Theme.IsLight() ? 3 /*ForceLight*/ : 2 /*ForceDark*/);
            FlushMenuThemes();
        }
        catch { /* older Windows: default light menu */ }
    }

    sealed class MenuOwner : NativeWindow
    {
        public IntPtr EnsureHandle()
        {
            if (Handle == IntPtr.Zero)
                CreateHandle(new CreateParams { Caption = "ClaudeUsageMenuOwner", Style = unchecked((int)0x80000000) /*WS_POPUP*/, ExStyle = WS_EX_TOOLWINDOW });
            return Handle;
        }
    }

    /// <summary>Opens the Windows Widgets board (equivalent to Win+W).</summary>
    static void OpenWidgetsBoard()
    {
        const byte VK_LWIN = 0x5B, VK_W = 0x57;
        const uint KEYUP = 2;
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_W, 0, 0, UIntPtr.Zero);
        keybd_event(VK_W, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYUP, UIntPtr.Zero);
    }

    public void Dispose()
    {
        _layoutTimer.Dispose();
        _tipTimer.Dispose();
        _tip.Dispose();
        if (Handle != IntPtr.Zero) DestroyHandle();
        _menuOwner.DestroyHandle();
    }

    // ---------------------------------------------------------------- interop

    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
    const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int WM_NULL = 0, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205,
              WM_MOUSELEAVE = 0x2A3, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    const uint TME_LEAVE = 2, ULW_ALPHA = 2;
    const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    const uint MF_STRING = 0, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTALIGN = 0x8;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr m, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr m, uint flags, int x, int y, IntPtr owner, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
}
