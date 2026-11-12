using System.Globalization;

namespace ClaudeUsageWidget;

/// <summary>UI texts – German if the Windows display language is German, English otherwise.</summary>
static class L
{
    static readonly bool De = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
    static string T(string de, string en) => De ? de : en;

    // taskbar
    public static string Week => T("Woche", "Week");
    public static string WeekShort => T("W", "W");
    public static string Loading => T("lädt…", "loading…");
    public static string OpenInBrowserMenu => T("Usage im Browser öffnen", "Open usage in browser");

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
    public static string Wide => T("Breit (nebeneinander)", "Wide (side by side)");
    public static string Save => T("Speichern", "Save");
    public static string Cancel => T("Abbrechen", "Cancel");

    // errors
    public static string Timeout => T("Zeitüberschreitung", "Timed out");
    public static string TooManyRequests(DateTime next) => T("Zu viele Anfragen – nächster Versuch ", "Too many requests – next try ") + next.ToString("HH:mm");
    public static string NoLogin => T("Keine Claude-Code-Anmeldung gefunden (~/.claude/.credentials.json)", "No Claude Code login found (~/.claude/.credentials.json)");
    public static string TokenExpired => T("Token abgelaufen – einmal Claude Code öffnen, dann klappt's wieder", "Token expired – open Claude Code once to refresh it");
}
