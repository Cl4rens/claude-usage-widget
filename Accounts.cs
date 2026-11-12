using System.Text.Json;

namespace ClaudeUsageWidget;

/// <summary>One Claude Code login = one Claude Code config folder with a .credentials.json.</summary>
record Account(string Dir, string Label)
{
    /// <summary>Short folder name shown next to the email, e.g. ".claude-work".</summary>
    public string Folder => Path.GetFileName(Dir.TrimEnd('\\', '/'));
}

/// <summary>
/// Finds the Claude Code logins on this PC. Claude Code keeps one login per config folder:
/// %USERPROFILE%\.claude by default, or whatever CLAUDE_CONFIG_DIR points to
/// (e.g. %USERPROFILE%\.claude-work for a second account).
/// </summary>
static class Accounts
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The folder Claude Code uses when started plainly.</summary>
    public static string DefaultDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return Normalize(string.IsNullOrWhiteSpace(env) ? Path.Combine(Home, ".claude") : env);
        }
    }

    public static string Normalize(string dir) => Path.GetFullPath(dir).TrimEnd('\\', '/');

    public static bool HasLogin(string dir) => File.Exists(Path.Combine(dir, ".credentials.json"));

    /// <summary>All logins found: default folder, ~/.claude*, and folders the user added by hand.</summary>
    public static List<Account> Discover(TaskbarSettings cfg)
    {
        var dirs = new List<string> { DefaultDir, Path.Combine(Home, ".claude") };
        try { dirs.AddRange(Directory.GetDirectories(Home, ".claude*")); } catch { }
        if (cfg.ExtraDirs != null) dirs.AddRange(cfg.ExtraDirs);

        return dirs.Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(HasLogin)
            .Select(d => new Account(d, LabelFor(d)))
            .ToList();
    }

    /// <summary>The logins the user ticked in the right-click menu (default: the default login).</summary>
    public static List<Account> Enabled(TaskbarSettings cfg)
    {
        var all = Discover(cfg);
        if (cfg.Accounts is { Count: > 0 } chosen)
        {
            var picked = all.Where(a => chosen.Contains(a.Dir, StringComparer.OrdinalIgnoreCase)).ToList();
            if (picked.Count > 0) return picked;
        }
        var def = all.FirstOrDefault(a => a.Dir.Equals(DefaultDir, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        return def != null ? [def] : [new Account(DefaultDir, "Claude")]; // shows "no login" until there is one
    }

    /// <summary>
    /// Name for a login: the email Claude Code stores in .claude.json, otherwise the folder name.
    /// (.claude.json sits inside the folder for CLAUDE_CONFIG_DIR, next to it for the default ~/.claude.)
    /// </summary>
    static string LabelFor(string dir)
    {
        var candidates = new List<string> { Path.Combine(dir, ".claude.json") };
        if (Path.GetFileName(dir).Equals(".claude", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(dir) is { } parent)
            candidates.Add(Path.Combine(parent, ".claude.json"));

        foreach (var file in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("oauthAccount", out var acc) &&
                    acc.TryGetProperty("emailAddress", out var mail) && mail.GetString() is { Length: > 0 } email)
                    return email;
            }
            catch { }
        }
        return Path.GetFileName(dir);
    }

    /// <summary>Accepts the config folder itself or a user folder that contains .claude.</summary>
    public static string? ResolvePicked(string picked)
    {
        if (HasLogin(picked)) return Normalize(picked);
        var inner = Path.Combine(picked, ".claude");
        return HasLogin(inner) ? Normalize(inner) : null;
    }
}
