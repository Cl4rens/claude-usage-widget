using System.Globalization;

namespace ClaudeUsageWidget;

/// <summary>UI texts – German if the Windows display language is German, English otherwise.</summary>
static class L
{
    // "Language" in settings.json can force "de" / "en"; default follows the Windows display language
    static readonly bool De = TaskbarSettings.Load().Language switch
    {
        "de" => true,
        "en" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de",
    };

    /// <summary>Culture for dates/times so they match the UI language (e.g. "Sat" vs "Sa").</summary>
    public static CultureInfo Culture => De ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-US");
    static string T(string de, string en) => De ? de : en;

    // taskbar
    public static string Week => T("Woche", "Week");
    public static string WeekShort => T("W", "W");
    public static string Loading => T("lädt…", "loading…");
    public static string ErrorShort => T("⚠ Fehler", "⚠ Error");
    public static string OpenInBrowserMenu => T("Usage im Browser öffnen", "Open usage in browser");
    public static string AccountsHeader => T("Angezeigte Konten", "Accounts shown");
    public static string AddAccount => T("Konto hinzufügen…", "Add account…");
    public static string AddAccountHint => T(
        "Claude-Code-Ordner des Kontos wählen (dorthin zeigt CLAUDE_CONFIG_DIR, z. B. .claude-work) – oder den Benutzerordner, der .claude enthält.",
        "Select the account's Claude Code folder (where CLAUDE_CONFIG_DIR points, e.g. .claude-work) – or the user folder that contains .claude.");
    public static string NoLoginInFolder => T(
        "In diesem Ordner ist keine Claude-Code-Anmeldung (.credentials.json).",
        "This folder doesn't contain a Claude Code login (.credentials.json).");

    // widget card
    public static string SessionLong => T("Aktuelle Sitzung (5h)", "Current session (5h)");
    public static string SessionShort => T("5 Stunden", "5 hours");
    public static string WeekLong => T("Woche (alle Modelle)", "Week (all models)");
    public static string Reset => T("Reset", "Resets");
    public static string AsOf => T("Stand", "Updated");
    public static string Stale => T("⚠ veraltet", "⚠ outdated");
    public static string OpenInBrowser => T("Im Browser öffnen", "Open in browser");

    // customization card
    public static string TaskbarDisplay => T("Anzeige in der Taskleiste", "Taskbar display");
    public static string Rings => T("Kreise", "Rings");
    public static string Bars => T("Balken", "Bars");
    public static string ShowResets => T("Reset-Zeiten anzeigen", "Show reset times");
    public static string Compact => T("Kompakt", "Compact");
    public static string ShowLogo => T("Logo anzeigen", "Show logo");
    public static string Wide => T("Breit", "Wide");
    public static string Resets => T("Reset-Zeiten", "Reset times");
    public static string Logo => T("Logo", "Logo");
    public static string Style => T("Darstellung", "Style");
    public static string TextColor => T("Textfarbe", "Text colour");
    public static string TextAuto => T("Automatisch", "Automatic");
    public static string TextLight => T("Hell", "Light");
    public static string TextDark => T("Dunkel", "Dark");
    public static string Save => T("Speichern", "Save");
    public static string Cancel => T("Abbrechen", "Cancel");

    // hover tooltip
    public static string ResetAt(DateTimeOffset when)
    {
        var l = when.ToLocalTime().AddSeconds(30); // API sends e.g. 04:59:59 → 05:00
        var today = DateTime.Today;
        string day = l.Date == today ? T("heute", "today")
                   : l.Date == today.AddDays(1) ? T("morgen", "tomorrow")
                   : l.ToString(De ? "ddd, d. MMM" : "ddd, MMM d", Culture);
        return T($"Reset {day} um {l:HH:mm}", $"Resets {day} at {l:HH:mm}");
    }

    public static string In(TimeSpan t)
    {
        t += TimeSpan.FromSeconds(30); // same rounding as the reset time
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours < 1) return T($"in {Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} Min.", $"in {Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min");
        if (t.TotalDays < 1) return T($"in {(int)t.TotalHours} Std. {t.Minutes} Min.", $"in {(int)t.TotalHours} h {t.Minutes} min");
        int d = (int)t.TotalDays;
        return T($"in {d} {(d == 1 ? "Tag" : "Tagen")} {t.Hours} Std.", $"in {d} {(d == 1 ? "day" : "days")} {t.Hours} h");
    }

    public static string Used(double? pct) => pct is null ? "–" : T($"{pct:0} % verbraucht", $"{pct:0}% used");

    // errors
    public static string Timeout => T("Zeitüberschreitung", "Timed out");
    public static string TooManyRequests(DateTime next) => T("Zu viele Anfragen – nächster Versuch ", "Too many requests – next try ") + next.ToString("HH:mm");
    public static string NoLogin => T("Keine Claude-Code-Anmeldung gefunden (~/.claude/.credentials.json)", "No Claude Code login found (~/.claude/.credentials.json)");
    public static string TokenExpired => T("Anmeldung abgelaufen – Claude Code einmal öffnen bzw. neu anmelden", "Login expired – open Claude Code once or sign in again");
}
