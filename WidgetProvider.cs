using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.Widgets.Providers;

namespace ClaudeUsageWidget;

/// <summary>
/// Provider for the Windows 11 Widgets board. The Widgets host activates it via COM
/// (see Package.appxmanifest → windows.comServer / com.microsoft.windows.widgets).
/// </summary>
[ComVisible(true)]
[Guid(Clsid)]
public sealed partial class WidgetProvider : IWidgetProvider, IWidgetProvider2
{
    public const string Clsid = "7C2F6B1E-3A4D-4E8B-9C51-2D7E8A9F4B63";

    static readonly Dictionary<string, (string size, bool active)> Widgets = new();
    static readonly object Lock = new();
    // widgets currently showing the settings card → since when
    static readonly Dictionary<string, DateTime> Customizing = new();

    /// <summary>
    /// The host gives no callback when the customize dialog is closed via X. Opening the dialog itself
    /// fires Activate/Deactivate right away, so only treat those as "dialog gone" after a few seconds.
    /// Call inside lock(Lock). Returns true if the widget left settings mode.
    /// </summary>
    static bool EndStaleCustomizing(string id)
    {
        if (Customizing.TryGetValue(id, out var since) && DateTime.UtcNow - since > TimeSpan.FromSeconds(3))
            return Customizing.Remove(id);
        return false;
    }
    static System.Threading.Timer? _timer;
    /// <summary>Latest data of every account ticked in the taskbar's right-click menu.</summary>
    static List<(string Label, UsageSnapshot? Snap, string? Error)> _data = [];

    public WidgetProvider()
    {
        // Pick up widgets that were pinned before this process started.
        foreach (var info in WidgetManager.GetDefault().GetWidgetInfos() ?? [])
        {
            var ctx = info.WidgetContext;
            lock (Lock) Widgets[ctx.Id] = (ctx.Size.ToString().ToLowerInvariant(), ctx.IsActive);
        }
        _timer ??= new System.Threading.Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    public void CreateWidget(WidgetContext ctx)
    {
        lock (Lock) Widgets[ctx.Id] = (ctx.Size.ToString().ToLowerInvariant(), true);
        Push(ctx.Id);
        _ = RefreshAsync();
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        lock (Lock) { Widgets.Remove(widgetId); Customizing.Remove(widgetId); }
    }

    // "Widget anpassen" in the widget's … menu (requires IsCustomizable="true" in the manifest)
    public void OnCustomizationRequested(WidgetCustomizationRequestedArgs args)
    {
        var id = args.WidgetContext.Id;
        lock (Lock) Customizing[id] = DateTime.UtcNow;
        Push(id);
    }

    public void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        var id = args.WidgetContext.Id;
        if (args.Verb == "save")
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(args.Data) ? "{}" : args.Data);
                var cur = TaskbarSettings.Load();
                // input ids carry a per-render suffix (see BuildSettingsCard) → match by prefix
                var inputs = doc.RootElement.EnumerateObject()
                    .ToDictionary(p => p.Name.Split('_')[0], p => p.Value.GetString() ?? "");
                bool Flag(string key, bool fallback) => inputs.TryGetValue(key, out var v) ? v == "true" : fallback;
                (cur with
                {
                    Style = inputs.TryGetValue("style", out var st) && st.Length > 0 ? st : cur.Style,
                    ShowResets = Flag("resets", cur.ShowResets),
                    Compact = Flag("compact", cur.Compact),
                    ShowLogo = Flag("logo", cur.ShowLogo),
                    Wide = Flag("wide", cur.Wide),
                    TextColor = inputs.TryGetValue("text", out var tc) && tc.Length > 0 ? tc : cur.TextColor,
                }).Save(); // the taskbar display picks this up within a second
            }
            catch { }
        }
        if (args.Verb is "save" or "cancel")
        {
            lock (Lock) Customizing.Remove(id);
            Push(id);
        }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs args)
    {
        var ctx = args.WidgetContext;
        lock (Lock) Widgets[ctx.Id] = (ctx.Size.ToString().ToLowerInvariant(), ctx.IsActive);
        Push(ctx.Id);
    }

    public void Activate(WidgetContext ctx)
    {
        lock (Lock)
        {
            Widgets[ctx.Id] = (ctx.Size.ToString().ToLowerInvariant(), true);
            EndStaleCustomizing(ctx.Id); // dialog closed via X → back to the normal card
        }
        Push(ctx.Id);
        _ = RefreshAsync();
    }

    public void Deactivate(string widgetId)
    {
        bool ended;
        lock (Lock)
        {
            if (Widgets.TryGetValue(widgetId, out var w)) Widgets[widgetId] = (w.size, false);
            ended = EndStaleCustomizing(widgetId);
        }
        if (ended) Push(widgetId);
    }

    // ---------------------------------------------------------------- updates

    static int _refreshing;
    static async Task RefreshAsync()
    {
        bool anyActive;
        lock (Lock) anyActive = Widgets.Values.Any(w => w.active);
        if (!anyActive && _data.Count > 0) return; // board closed → don't poll
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            // shared cache: at most one real request every 2 minutes per account for the whole app
            var accounts = Accounts.Enabled(TaskbarSettings.Load());
            var results = await Task.WhenAll(accounts.Select(a => UsageClient.GetAsync(a.Dir)));
            _data = accounts.Zip(results, (a, r) => (a.Label, r.Snapshot, r.Error)).ToList();
        }
        finally { Interlocked.Exchange(ref _refreshing, 0); }

        string[] ids;
        lock (Lock) ids = Widgets.Keys.ToArray();
        foreach (var id in ids) Push(id);
    }

    static void Push(string id)
    {
        string size; bool customizing;
        lock (Lock) { if (!Widgets.TryGetValue(id, out var w)) return; size = w.size; customizing = Customizing.ContainsKey(id); }
        try
        {
            WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(id)
            {
                Template = customizing ? BuildSettingsCard(TaskbarSettings.Load()) : BuildCard(size, _data),
                Data = "{}",
                CustomState = "",
            });
        }
        catch { /* widget host went away – it will re-activate us */ }
    }

    // ---------------------------------------------------------------- Adaptive Card

    static string Style(double? p) => p switch { >= 90 => "attention", >= 70 => "warning", _ => "accent" };

    static object Bar(double? p)
    {
        int fill = (int)Math.Clamp(Math.Round(p ?? 0), 1, 99);
        return new
        {
            type = "ColumnSet",
            spacing = "small",
            columns = new object[]
            {
                new { type = "Column", width = fill, style = Style(p), minHeight = "6px", items = Array.Empty<object>() },
                new { type = "Column", width = 100 - fill, style = "emphasis", minHeight = "6px", items = Array.Empty<object>() },
            },
        };
    }

    static IEnumerable<object> Section(string title, UsageWindow w, bool compact)
    {
        string pct = w.Percent is null ? "–" : $"{w.Percent:0}%";
        string reset = w.ResetsAt is { } r ? L.Reset + " " + TaskbarWidget.FormatReset(r) : "";
        yield return new { type = "TextBlock", text = title, size = "small", isSubtle = true, spacing = compact ? "small" : "medium" };
        yield return new
        {
            type = "ColumnSet",
            spacing = "none",
            columns = new object[]
            {
                new { type = "Column", width = "stretch", items = new object[] {
                    new { type = "TextBlock", text = pct, size = compact ? "large" : "extraLarge", weight = "bolder" } } },
                new { type = "Column", width = "auto", verticalContentAlignment = "bottom", items = new object[] {
                    new { type = "TextBlock", text = reset, size = "small", isSubtle = true } } },
            },
        };
        yield return Bar(w.Percent);
    }

    static string BuildSettingsCard(TaskbarSettings cur)
    {
        // The Widgets host remembers input values per id across renders – fresh ids make it
        // show the saved settings instead of whatever was ticked in the last (cancelled) dialog.
        string n = "_" + DateTime.UtcNow.Ticks;
        var card = new Dictionary<string, object>
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["body"] = new object[]
            {
                new { type = "TextBlock", text = L.TaskbarDisplay, weight = "bolder", wrap = true },
                // two controls per row so everything fits into the medium widget
                Pair(
                    Choice("style" + n, L.Style, cur.Style, (L.Rings, "rings"), (L.Bars, "bars")),
                    Choice("text" + n, L.TextColor, cur.TextColor, (L.TextAuto, "auto"), (L.TextLight, "light"), (L.TextDark, "dark"))),
                Pair(Toggle("resets" + n, L.Resets, cur.ShowResets), Toggle("logo" + n, L.Logo, cur.ShowLogo)),
                Pair(Toggle("compact" + n, L.Compact, cur.Compact), Toggle("wide" + n, L.Wide, cur.Wide)),
            },
            ["actions"] = new object[]
            {
                new { type = "Action.Execute", title = L.Save, verb = "save" },
                new { type = "Action.Execute", title = L.Cancel, verb = "cancel" },
            },
        };
        return JsonSerializer.Serialize(card);
    }

    static object Toggle(string id, string title, bool on) =>
        new { type = "Input.Toggle", id, title, value = on ? "true" : "false", valueOn = "true", valueOff = "false" };

    static object Choice(string id, string label, string value, params (string Title, string Value)[] choices) => new
    {
        type = "Input.ChoiceSet", id, label, value, style = "compact",
        choices = choices.Select(c => new { title = c.Title, value = c.Value }).ToArray(),
    };

    static object Pair(object left, object right) => new
    {
        type = "ColumnSet",
        spacing = "small",
        columns = new object[]
        {
            new { type = "Column", width = "stretch", items = new[] { left } },
            new { type = "Column", width = "stretch", items = new[] { right } },
        },
    };

    static string BuildCard(string size, List<(string Label, UsageSnapshot? Snap, string? Error)> data)
    {
        if (data.Count <= 1)
            return BuildCard(size, data.FirstOrDefault().Snap, data.FirstOrDefault().Error);

        // several accounts: name, then 5h and week side by side per account
        bool compact = size == "small";
        var body = new List<object>();
        foreach (var (label, snap, error) in data)
        {
            body.Add(new { type = "TextBlock", text = label, size = "small", weight = "bolder", spacing = body.Count == 0 ? "none" : "medium", wrap = false });
            if (snap == null)
            {
                body.Add(new { type = "TextBlock", text = error ?? L.Loading, size = "small", wrap = true, color = error != null ? "attention" : "default", spacing = "none" });
                continue;
            }
            object Cell(string title, UsageWindow w) => new
            {
                type = "Column", width = "stretch",
                items = new object[]
                {
                    new { type = "TextBlock", text = $"{title}  {(w.Percent is null ? "–" : $"{w.Percent:0}%")}", weight = "bolder", size = compact ? "small" : "medium", spacing = "none" },
                    Bar(w.Percent),
                },
            };
            body.Add(new
            {
                type = "ColumnSet", spacing = "none",
                columns = new[] { Cell("5h", snap.FiveHour), Cell(compact ? L.WeekShort : L.Week, snap.SevenDay) },
            });
        }
        var newest = data.Where(d => d.Snap != null).Select(d => d.Snap!.FetchedAt).DefaultIfEmpty().Min();
        if (!compact && newest != default)
            body.Add(new { type = "TextBlock", text = $"{L.AsOf} {newest:HH:mm}", size = "small", isSubtle = true, horizontalAlignment = "right", spacing = "small" });

        var card = new Dictionary<string, object>
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["body"] = body,
        };
        if (!compact)
            card["actions"] = new object[] { new { type = "Action.OpenUrl", title = L.OpenInBrowser, url = "https://claude.ai/settings/usage" } };
        return JsonSerializer.Serialize(card);
    }

    static string BuildCard(string size, UsageSnapshot? snap, string? error)
    {
        bool compact = size == "small";
        var body = new List<object>();

        if (snap == null)
        {
            body.Add(new { type = "TextBlock", text = error ?? L.Loading, wrap = true, isSubtle = error == null, color = error != null ? "attention" : "default" });
        }
        else
        {
            body.AddRange(Section(compact ? L.SessionShort : L.SessionLong, snap.FiveHour, compact));
            body.AddRange(Section(compact ? L.Week : L.WeekLong, snap.SevenDay, compact));
            if (!compact)
                body.Add(new
                {
                    type = "TextBlock",
                    text = (error != null ? L.Stale + " · " : "") + $"{L.AsOf} {snap.FetchedAt:HH:mm}",
                    size = "small", isSubtle = true, horizontalAlignment = "right", spacing = "medium",
                });
        }

        var card = new Dictionary<string, object>
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["body"] = body,
        };
        if (!compact)
            card["actions"] = new object[] { new { type = "Action.OpenUrl", title = L.OpenInBrowser, url = "https://claude.ai/settings/usage" } };
        return JsonSerializer.Serialize(card);
    }
}

// ---------------------------------------------------------------- COM plumbing

static class ComServer
{
    static uint _cookie;

    public static void Register()
    {
        var clsid = Guid.Parse(WidgetProvider.Clsid);
        int hr = CoRegisterClassObject(ref clsid, new WidgetProviderFactory(), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out _cookie);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static void Unregister()
    {
        if (_cookie != 0) CoRevokeClassObject(_cookie);
    }

    const uint CLSCTX_LOCAL_SERVER = 0x4, REGCLS_MULTIPLEUSE = 1;
    [DllImport("ole32.dll")] static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object obj, uint ctx, uint flags, out uint cookie);
    [DllImport("ole32.dll")] static extern int CoRevokeClassObject(uint cookie);
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr obj);
    [PreserveSig] int LockServer(bool fLock);
}

[ComVisible(true)]
sealed class WidgetProviderFactory : IClassFactory
{
    static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110), E_NOINTERFACE = unchecked((int)0x80004002);

    public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr obj)
    {
        obj = IntPtr.Zero;
        if (outer != IntPtr.Zero) return CLASS_E_NOAGGREGATION;
        if (riid != typeof(IWidgetProvider).GUID && riid != IUnknown) return E_NOINTERFACE;
        obj = WinRT.MarshalInspectable<IWidgetProvider>.FromManaged(new WidgetProvider());
        return 0;
    }

    public int LockServer(bool fLock) => 0;
}
