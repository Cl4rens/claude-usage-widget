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
/// interval of 2 minutes – across all callers and all processes (shared file cache guarded by a
/// named semaphore). On HTTP 429 the wait doubles (4, 8 min) and never exceeds 8 minutes.
/// The Claude Code login file is only READ; tokens are never refreshed, stored or sent elsewhere.
/// </summary>
static class UsageClient
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(8);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly Semaphore FetchLock = new(1, 1, @"Local\ClaudeUsageWidget_Fetch");
    static string CredPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
    static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget", "cache.json");

    class Cache
    {
        public UsageSnapshot? Snapshot { get; set; }
        public string? Error { get; set; }
        public DateTime NextAllowedUtc { get; set; }
        public double BackoffMinutes { get; set; } // current wait after 429s, 0 = normal interval
    }

    public static Task<UsageResult> GetAsync() => Task.Run(() =>
    {
        if (!FetchLock.WaitOne(TimeSpan.FromSeconds(30))) return new UsageResult(ReadCache().Snapshot, L.Timeout);
        try
        {
            var cache = ReadCache();
            if (DateTime.UtcNow < cache.NextAllowedUtc)
                return new UsageResult(cache.Snapshot, cache.Error); // too early → serve cached data

            // Measured from the start of the request, minus a little slack – otherwise the 60 s timer
            // fires a few ms before the gate opens and a whole tick is skipped.
            var started = DateTime.UtcNow;
            try
            {
                cache.Snapshot = FetchAsync().GetAwaiter().GetResult();
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
            WriteCache(cache);
            return new UsageResult(cache.Snapshot, cache.Error);
        }
        finally { FetchLock.Release(); }
    });

    static Cache ReadCache()
    {
        try { return JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath)) ?? new(); }
        catch { return new(); }
    }

    static void WriteCache(Cache c)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(c));
        }
        catch { }
    }

    sealed class RateLimitedException(TimeSpan retryAfter) : Exception("429")
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }

    static async Task<UsageSnapshot> FetchAsync()
    {
        // Re-read every time: Claude Code refreshes the token and rewrites this file.
        if (!File.Exists(CredPath)) throw new Exception(L.NoLogin);
        using var cred = JsonDocument.Parse(await File.ReadAllTextAsync(CredPath));
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
        _taskbar = new TaskbarWidget();

        _taskbar.MenuItems.Add((L.OpenInBrowserMenu, () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true })));
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
            // served from the shared cache unless the last real request is ≥ 2 min old
            var r = await UsageClient.GetAsync();
            _taskbar.SetData(r.Snapshot, r.Error);
        }
        finally { _busy = false; }
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
