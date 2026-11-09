using System.Text.RegularExpressions;
using Remy.Core.Settings;

namespace Remy.Core.Context;

/// <summary>Maps Windows process names, window titles and browser hosts to a destination category
/// (cleanup.md §5.2, with every Mac bundle-id rule translated to Windows executable names).</summary>
public static partial class DictationContextClassifier
{
    // ---- tables (executable names lowercase, without ".exe")

    internal static readonly HashSet<string> TerminalApps = new(StringComparer.Ordinal)
    {
        "windowsterminal", "openconsole", "conhost", "cmd", "powershell", "pwsh", "alacritty", "wezterm", "wezterm-gui",
        "ghostty", "kitty", "warp", "hyper", "tabby", "rio", "mintty", "wsl",
    };

    // Cursor, Windsurf, Codex desktop. (VS Code is not in the Mac list and maps to Other.)
    internal static readonly HashSet<string> CodingAgentApps = new(StringComparer.Ordinal) { "cursor", "windsurf", "codex" };

    internal static readonly HashSet<string> AiChatApps = new(StringComparer.Ordinal) { "chatgpt", "claude" };

    internal static readonly HashSet<string> BrowserApps = new(StringComparer.Ordinal)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "zen", "arc",
    };

    internal static readonly string[] AiChatHosts = ["chatgpt.com", "chat.openai.com", "claude.ai", "gemini.google.com", "copilot.microsoft.com", "perplexity.ai"];
    internal static readonly string[] EmailHosts = ["gmail.com", "mail.google.com", "outlook.live.com", "outlook.office.com"];
    internal static readonly string[] WorkHosts = ["app.slack.com", "teams.microsoft.com", "discord.com"];
    internal static readonly string[] PersonalHosts = ["messages.google.com", "web.telegram.org", "web.whatsapp.com", "messenger.com"];

    internal static readonly string[] EmailSubstrings = ["mail", "outlook", "spark"];
    internal static readonly string[] EmailExecutables = ["olk", "thunderbird"];
    internal static readonly string[] WorkSubstrings = ["slack", "teams", "discord"];
    internal static readonly string[] PersonalSubstrings = ["messages", "mobilesms", "whatsapp", "telegram", "signal", "messenger"];

    internal static readonly string[] SensitiveExact = ["credentialuibroker", "remy"];
    internal static readonly string[] SensitiveFragments =
        ["1password", "bitwarden", "dashlane", "enpass", "keepass", "keeper", "lastpass", "nordpass", "password", "proton.pass", "protonpass"];

    // Browser fallback when the URL could not be read: window-title suffixes of well-known sites.
    private static readonly (string Suffix, DictationContextCategory Category)[] TitleSuffixes =
    [
        ("- gmail", DictationContextCategory.Email),
        ("- outlook", DictationContextCategory.Email),
        ("- chatgpt", DictationContextCategory.AiChat),
        ("- claude", DictationContextCategory.AiChat),
        ("- slack", DictationContextCategory.WorkMessaging),
        ("| slack", DictationContextCategory.WorkMessaging),
        ("| microsoft teams", DictationContextCategory.WorkMessaging),
        ("- discord", DictationContextCategory.WorkMessaging),
        ("- whatsapp", DictationContextCategory.PersonalMessaging),
        ("- telegram", DictationContextCategory.PersonalMessaging),
    ];

    [GeneratedRegex(@"(?<![\p{L}\p{N}_./-])(?:claude[ \t]+code|codex(?:[ \t]+cli)?|gemini[ \t]+cli|opencode|aider)(?![\p{L}\p{N}_./-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgentTitleRegex();

    /// <summary>Lowercase, trimmed, ".exe" removed.</summary>
    public static string NormalizeProcessName(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return n.EndsWith(".exe", StringComparison.Ordinal) ? n[..^4] : n;
    }

    public static bool IsTerminalAgentTitle(string? windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle)) return false;
        var t = windowTitle.Length > 300 ? windowTitle[..300] : windowTitle;
        return AgentTitleRegex().IsMatch(t);
    }

    public static bool IsSensitiveApplication(string? processName)
    {
        var n = NormalizeProcessName(processName);
        if (n.Length == 0) return false;
        if (SensitiveExact.Contains(n)) return true;
        return SensitiveFragments.Any(f => n.Contains(f, StringComparison.Ordinal));
    }

    /// <summary>Host of a URL or bare host string, lowercased; null when unparsable.</summary>
    public static string? HostOf(string? urlOrHost)
    {
        var s = (urlOrHost ?? "").Trim();
        if (s.Length == 0) return null;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host.ToLowerInvariant() : null;
    }

    private static bool HostMatches(string host, string[] domains) =>
        domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));

    /// <summary>Website category from a URL/host (any app), or null (cleanup.md §5.2 step 2).</summary>
    public static DictationContextCategory? CategoryForHost(string? urlOrHost)
    {
        var host = HostOf(urlOrHost);
        if (host is null) return null;
        if (HostMatches(host, AiChatHosts)) return DictationContextCategory.AiChat;
        if (HostMatches(host, EmailHosts)) return DictationContextCategory.Email;
        if (HostMatches(host, WorkHosts)) return DictationContextCategory.WorkMessaging;
        if (HostMatches(host, PersonalHosts)) return DictationContextCategory.PersonalMessaging;
        return null;
    }

    /// <summary>Auto category and whether field context may be read.</summary>
    /// <param name="urlOrHost">Focused document URL or host (browsers; any app that exposes one).</param>
    /// <param name="appName">Display name; only used for the Codex/ChatGPT disambiguation.</param>
    public static (DictationContextCategory Category, bool ContextEnabled) Classify(
        string? processName, string? windowTitle, string? urlOrHost, string? appName = null,
        IReadOnlyList<DictationAppRule>? rules = null)
    {
        var proc = NormalizeProcessName(processName);

        // 1. app rule
        DictationAppRule? rule = null;
        if (rules is not null && proc.Length > 0)
            rule = rules.FirstOrDefault(r => NormalizeProcessName(r.ProcessName) == proc);
        if (rule is { Category: { } rc }) return (rc, !rule.Excluded);

        var category = AutoCategory(proc, windowTitle, urlOrHost, appName);
        return (category, rule is null || !rule.Excluded);
    }

    /// <summary>Classification without app rules.</summary>
    public static DictationContextCategory AutoCategory(string? processName, string? windowTitle, string? urlOrHost, string? appName = null)
    {
        var proc = NormalizeProcessName(processName);

        // 2. website host
        if (CategoryForHost(urlOrHost) is { } web) return web;

        // 3. no process
        if (proc.Length == 0) return DictationContextCategory.Other;

        // 4. terminals
        if (TerminalApps.Contains(proc))
            return IsTerminalAgentTitle(windowTitle) ? DictationContextCategory.TerminalAgent : DictationContextCategory.Terminal;

        // 5. Codex builds that present as ChatGPT
        if (proc == "codex" && string.Equals((appName ?? "").Trim(), "ChatGPT", StringComparison.OrdinalIgnoreCase))
            return DictationContextCategory.AiChat;

        // 6/7. coding agents, AI chat
        if (CodingAgentApps.Contains(proc)) return DictationContextCategory.CodingAgent;
        if (AiChatApps.Contains(proc)) return DictationContextCategory.AiChat;

        // 8. browsers (with title fallback when the URL is unknown)
        if (BrowserApps.Contains(proc))
        {
            if (string.IsNullOrWhiteSpace(urlOrHost) && !string.IsNullOrEmpty(windowTitle))
            {
                var t = windowTitle.Trim().ToLowerInvariant();
                foreach (var (suffix, cat) in TitleSuffixes)
                    if (t.EndsWith(suffix, StringComparison.Ordinal)) return cat;
            }
            return DictationContextCategory.Browser;
        }

        // 9. substring rules
        if (EmailExecutables.Contains(proc) || EmailSubstrings.Any(s => proc.Contains(s, StringComparison.Ordinal)))
            return DictationContextCategory.Email;
        if (WorkSubstrings.Any(s => proc.Contains(s, StringComparison.Ordinal))) return DictationContextCategory.WorkMessaging;
        if (PersonalSubstrings.Any(s => proc.Contains(s, StringComparison.Ordinal))) return DictationContextCategory.PersonalMessaging;

        return DictationContextCategory.Other;
    }

    /// <summary>True when the process is a browser (parity set).</summary>
    public static bool IsBrowser(string? processName) => BrowserApps.Contains(NormalizeProcessName(processName));
}
