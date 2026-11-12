using System.Net.Http;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        ApplicationConfiguration.Initialize();

        if (args.Contains("-RegisterProcessAsComServer"))
        {
            // Launched by the Windows Widgets host: only serve the widget board.
            ComServer.Register();
            Application.Run();
            ComServer.Unregister();
            return;
        }

        using var mutex = new Mutex(true, "ClaudeUsageWidget_SingleInstance", out bool isNew);
        if (!isNew) return;
        Autostart.RemoveLegacyRunKey();
        ComServer.Register(); // this process can also serve the widget board
        Application.Run(new TaskbarApp());
        ComServer.Unregister();
    }
}

// ---------------------------------------------------------------- Data

record UsageWindow(double? Percent, DateTimeOffset? ResetsAt);
record UsageSnapshot(UsageWindow FiveHour, UsageWindow SevenDay, DateTime FetchedAt);
/// <summary>Latest known data (may be stale) plus the error of the last attempt, if any.</summary>
record UsageResult(UsageSnapshot? Snapshot, string? Error);

/// <summary>
/// Single source of usage data for the taskbar display and the Widgets board.
/// Fetches /api/oauth/usage (the endpoint Claude Code's own /usage command uses) at a fixed
/// interval of 2 minutes per account – across all callers and all processes (shared file cache
/// guarded by a named semaphore, one of each per Claude Code login). On HTTP 429 the wait doubles
/// (4, 8 min) and never exceeds 8 minutes; each account backs off on its own.
/// The Claude Code login files are only READ; tokens are never refreshed, stored or sent elsewhere.
/// </summary>
static class UsageClient
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(8);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Stable short id for an account folder (used for cache file and lock names).</summary>
    static string Key(string dir) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..12];

    static string CachePath(string dir) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget", $"cache-{Key(dir)}.json");

    class Cache
    {
        public UsageSnapshot? Snapshot { get; set; }
        public string? Error { get; set; }
        public DateTime NextAllowedUtc { get; set; }
        public double BackoffMinutes { get; set; } // current wait after 429s, 0 = normal interval
    }

    public static Task<UsageResult> GetAsync(string dir) => Task.Run(() =>
    {
        using var fetchLock = new Semaphore(1, 1, $@"Local\ClaudeUsageWidget_Fetch_{Key(dir)}");
        if (!fetchLock.WaitOne(TimeSpan.FromSeconds(30))) return new UsageResult(ReadCache(dir).Snapshot, L.Timeout);
        try
        {
            var cache = ReadCache(dir);
            if (DateTime.UtcNow < cache.NextAllowedUtc)
                return new UsageResult(cache.Snapshot, cache.Error); // too early → serve cached data

            // Measured from the start of the request, minus a little slack – otherwise the 60 s timer
            // fires a few ms before the gate opens and a whole tick is skipped.
            var started = DateTime.UtcNow;
            try
            {
                cache.Snapshot = FetchAsync(dir).GetAwaiter().GetResult();
                cache.Error = null;
                cache.NextAllowedUtc = started + Interval - TimeSpan.FromSeconds(5);
                cache.BackoffMinutes = 0;
            }
            catch (RateLimitedException rl)
            {
                // 2 → 4 → 8 → 8 … minutes; a longer Retry-After from the server is capped at 8 too.
                cache.BackoffMinutes = Math.Min(MaxBackoff.TotalMinutes,
                    (cache.BackoffMinutes <= 0 ? Interval.TotalMinutes : cache.BackoffMinutes) * 2);
                var wait = TimeSpan.FromMinutes(cache.BackoffMinutes);
                if (rl.RetryAfter > wait) wait = rl.RetryAfter < MaxBackoff ? rl.RetryAfter : MaxBackoff;
                cache.Error = L.TooManyRequests(DateTime.Now + wait);
                cache.NextAllowedUtc = DateTime.UtcNow + wait;
            }
            catch (Exception ex)
            {
                cache.Error = ex.Message;
                cache.NextAllowedUtc = DateTime.UtcNow + Interval;
            }
            WriteCache(dir, cache);
            return new UsageResult(cache.Snapshot, cache.Error);
        }
        finally { fetchLock.Release(); }
    });

    static Cache ReadCache(string dir)
    {
        try { return JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath(dir))) ?? new(); }
        catch { return new(); }
    }

    static void WriteCache(string dir, Cache c)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath(dir))!);
            File.WriteAllText(CachePath(dir), JsonSerializer.Serialize(c));
        }
        catch { }
    }

    sealed class RateLimitedException(TimeSpan retryAfter) : Exception("429")
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }

    static async Task<UsageSnapshot> FetchAsync(string dir)
    {
        // Re-read every time: Claude Code refreshes the token and rewrites this file.
        var credPath = Path.Combine(dir, ".credentials.json");
        if (!File.Exists(credPath)) throw new Exception(L.NoLogin);
        using var cred = JsonDocument.Parse(await File.ReadAllTextAsync(credPath));
        var token = cred.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString();

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.UserAgent.ParseAdd("claude-usage-widget/1.0");

        using var resp = await Http.SendAsync(req);
        if ((int)resp.StatusCode is 401 or 403)
            throw new Exception(L.TokenExpired);
        if ((int)resp.StatusCode == 429)
        {
            var ra = resp.Headers.RetryAfter;
            var wait = ra?.Delta ?? (ra?.Date is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.Zero);
            throw new RateLimitedException(wait);
        }
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return new UsageSnapshot(Parse(doc.RootElement, "five_hour"), Parse(doc.RootElement, "seven_day"), DateTime.Now);
    }

    static UsageWindow Parse(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return new(null, null);
        double? pct = w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null;
        DateTimeOffset? reset = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var d) ? d : null;
        return new(pct, reset);
    }
}

// ---------------------------------------------------------------- Settings

/// <summary>
/// Appearance of the taskbar display. Edited via "Widget anpassen" in the Widgets board
/// (possibly from another process) → stored in a small JSON file that the taskbar re-reads on change.
/// </summary>
record TaskbarSettings
{
    public string Style { get; init; } = "rings"; // "rings" | "bars"
    public bool ShowResets { get; init; } = true;
    public bool Compact { get; init; } = false; // "W" instead of "Woche"/"Week"
    public bool ShowLogo { get; init; } = true;
    public bool Wide { get; init; } = false;      // 5h and week side by side
    public string TextColor { get; init; } = "auto"; // "auto" (follow Windows) | "light" | "dark"
    /// <summary>Account folders ticked in the right-click menu (null = just the default login).</summary>
    public List<string>? Accounts { get; init; }
    /// <summary>Account folders added via "Add account…" (others are found automatically).</summary>
    public List<string>? ExtraDirs { get; init; }
    /// <summary>UI language: "auto" (Windows display language) | "de" | "en". Not in the dialog (yet).</summary>
    public string Language { get; init; } = "auto";
    /// <summary>Show example.com addresses instead of real account emails (for screenshots / streaming).</summary>
    public bool DemoEmails { get; init; } = false;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget", "settings.json");

    public static DateTime LastWrite => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    public static TaskbarSettings Load()
    {
        try { return JsonSerializer.Deserialize<TaskbarSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

/// <summary>Autostart is the package's startup task (Task-Manager → Autostart).</summary>
static class Autostart
{
    /// <summary>The unpackaged version used the Run key – remove that leftover.</summary>
    public static void RemoveLegacyRunKey()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        k?.DeleteValue("ClaudeUsageWidget", false);
    }
}

static class Theme
{
    public static bool IsLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    /// <summary>"Show accent colour on Start and taskbar" – then orange would clash with the taskbar.</summary>
    public static bool AccentTaskbar()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("ColorPrevalence") is int v && v == 1;
    }

    public static Color ForPercent(double? p) => p switch
    {
        null => Color.Gray,
        >= 90 => Color.FromArgb(232, 72, 72),
        >= 70 => Color.FromArgb(240, 160, 50),
        _ => Color.FromArgb(217, 119, 87), // Claude-Orange
    };
}


// ---------------------------------------------------------------- App (taskbar display, no tray icon)

class TaskbarApp : ApplicationContext
{
    readonly TaskbarWidget _taskbar;
    readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 60_000 };

    public TaskbarApp()
    {
        _taskbar = new TaskbarWidget { BuildMenu = BuildMenu };
        _taskbar.SettingsChanged += async () => await RefreshAsync();
        _taskbar.Show();

        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();
        _ = RefreshAsync();
    }

    bool _busy;
    async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // each account is served from its own cache unless its last real request is ≥ 2 min old
            var accounts = Accounts.Enabled(TaskbarSettings.Load());
            var results = await Task.WhenAll(accounts.Select(a => UsageClient.GetAsync(a.Dir)));
            _taskbar.SetData(accounts.Zip(results, (a, r) => new TaskbarWidget.AccountView(a.Label, r.Snapshot, r.Error)).ToList());
        }
        finally { _busy = false; }
    }

    // ---------------------------------------------------------------- right-click menu

    List<TaskbarWidget.MenuEntry> BuildMenu()
    {
        var cfg = TaskbarSettings.Load();
        var all = Accounts.Discover(cfg);
        var enabled = Accounts.Enabled(cfg).Select(a => a.Dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool sameLabels = all.GroupBy(a => a.Label).Any(g => g.Count() > 1);

        var menu = new List<TaskbarWidget.MenuEntry> { new(L.AccountsHeader, Disabled: true) };
        foreach (var acc in all)
        {
            string text = acc.Label == acc.Folder || (!sameLabels && acc.Folder == ".claude") ? acc.Label : $"{acc.Label}  ({acc.Folder})";
            menu.Add(new(text, () => ToggleAccount(acc.Dir), Checked: enabled.Contains(acc.Dir)));
        }
        menu.Add(new(L.AddAccount, AddAccount));
        menu.Add(new(null)); // separator
        menu.Add(new(L.OpenInBrowserMenu, () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true })));
        return menu;
    }

    async void ToggleAccount(string dir)
    {
        var cfg = TaskbarSettings.Load();
        var on = Accounts.Enabled(cfg).Select(a => a.Dir).ToList();
        if (on.Contains(dir, StringComparer.OrdinalIgnoreCase))
        {
            if (on.Count == 1) return; // keep at least one account
            on.RemoveAll(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase));
        }
        else on.Add(dir);
        // keep the menu order
        var order = Accounts.Discover(cfg).Select(a => a.Dir).ToList();
        on = on.OrderBy(d => order.FindIndex(o => o.Equals(d, StringComparison.OrdinalIgnoreCase))).ToList();
        (cfg with { Accounts = on }).Save();
        await RefreshAsync();
    }

    async void AddAccount()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = L.AddAccountHint,
            UseDescriptionForTitle = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ShowHiddenFiles = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var dir = Accounts.ResolvePicked(dlg.SelectedPath);
        if (dir == null)
        {
            MessageBox.Show(L.NoLoginInFolder, "Claude Usage", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var cfg = TaskbarSettings.Load();
        var extra = (cfg.ExtraDirs ?? []).Where(d => !d.Equals(dir, StringComparison.OrdinalIgnoreCase)).Append(dir).ToList();
        var on = Accounts.Enabled(cfg).Select(a => a.Dir).Where(d => !d.Equals(dir, StringComparison.OrdinalIgnoreCase)).Append(dir).ToList();
        (cfg with { ExtraDirs = extra, Accounts = on }).Save();
        await RefreshAsync();
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void ExitThreadCore()
    {
        _pollTimer.Stop();
        _taskbar.Dispose();
        base.ExitThreadCore();
    }
}
