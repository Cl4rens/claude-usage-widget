using System.Diagnostics;

namespace ClaudeUsageWidget;

/// <summary>
/// Lets Claude Code renew its own login. Running <c>claude auth status</c> makes Claude Code
/// refresh an access token that is about to expire (it writes the login file itself, with its
/// own locking) – no model request is sent. The widget never refreshes or writes tokens.
/// </summary>
static class ClaudeCli
{
    static readonly Dictionary<string, DateTime> LastRun = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Lock = new();

    /// <summary>Path to the Claude Code executable, or null if it isn't installed / found.</summary>
    static string? FindExecutable()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"), // npm install
            Path.Combine(home, ".local", "bin", "claude.exe"),                                                // native installer
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
            candidates.Add(Path.Combine(dir.Trim(), "claude.cmd"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Runs <c>claude auth status</c> for the login in <paramref name="configDir"/> (hidden, max 30 s).
    /// At most once per minute per account. Returns false if Claude Code wasn't found or failed.
    /// </summary>
    public static bool RenewLogin(string configDir)
    {
        lock (Lock)
        {
            if (LastRun.TryGetValue(configDir, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return false;
            LastRun[configDir] = DateTime.UtcNow;
        }

        var exe = FindExecutable();
        if (exe == null) return false;

        var psi = new ProcessStartInfo
        {
            FileName = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? "cmd.exe" : exe,
            Arguments = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? $"/c \"\"{exe}\" auth status --json\"" : "auth status --json",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        // The default login (~/.claude without CLAUDE_CONFIG_DIR) must run without the variable,
        // because Claude Code then keeps .claude.json next to the folder instead of inside it.
        bool isPlainDefault = configDir.Equals(
            Accounts.Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")),
            StringComparison.OrdinalIgnoreCase);
        if (isPlainDefault) psi.Environment.Remove("CLAUDE_CONFIG_DIR");
        else psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEndAsync(); // drain so the process never blocks on a full pipe
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
