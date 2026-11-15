using System.Text;
using System.Text.RegularExpressions;

namespace OwShaderPrecache.Core;

/// <summary>Writes the environment the game needs into Steam's per-game
/// launch options (userdata/&lt;id&gt;/config/localconfig.vdf). Steam must be
/// closed, otherwise it overwrites the file on exit.</summary>
public static class LaunchOptions {
    static readonly string[] OurVars = {
        "__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME", "__GL_SHADER_DISK_CACHE_SIZE",
        "__GL_SHADER_DISK_CACHE_SKIP_CLEANUP", "MESA_SHADER_CACHE_MAX_SIZE",
    };

    public static string Assignments(CacheMode mode, int gib) => mode == CacheMode.NvidiaProprietary
        ? $"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME=\"{CacheSetup.GameReadOnlyList()}\" " +
          $"__GL_SHADER_DISK_CACHE_SIZE={CacheSetup.CacheBytes(gib)} __GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1"
        : $"MESA_SHADER_CACHE_MAX_SIZE={gib}G";

    /// <summary>Merge our variables into existing launch options.</summary>
    public static string Merge(string existing, string assignments) {
        var rest = existing;
        foreach (var v in OurVars)
            rest = Regex.Replace(rest, $@"(^|\s){Regex.Escape(v)}=(""[^""]*""|\S*)", " ");
        rest = Regex.Replace(rest, @"\s+", " ").Trim();
        if (rest.Contains("%command%")) return $"{assignments} {rest}";
        return rest.Length == 0 ? $"{assignments} %command%" : $"{assignments} %command% {rest}";
    }

    public static IEnumerable<string> ConfigFiles(string steamRoot) {
        var ud = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(ud)) yield break;
        foreach (var u in Directory.GetDirectories(ud)) {
            var f = Path.Combine(u, "config", "localconfig.vdf");
            if (File.Exists(f)) yield return f;
        }
    }

    public static string? Current(string steamRoot) {
        foreach (var f in ConfigFiles(steamRoot).OrderByDescending(File.GetLastWriteTimeUtc)) {
            var text = File.ReadAllText(f);
            var app = FindAppBlock(text);
            if (app == null) continue;
            var lo = FindKey(text, app.Value.open, app.Value.close, "LaunchOptions");
            return lo == null ? "" : Unescape(text[lo.Value.valStart..lo.Value.valEnd]);
        }
        return null;
    }

    /// <summary>Apply to every Steam user on this PC that has the game.</summary>
    public static List<string> Apply(string steamRoot, string assignments, bool skipSteamCheck = false) {
        if (!skipSteamCheck && Steam.IsRunning())
            throw new InvalidOperationException("Steam is running. Exit Steam completely (tray icon → Exit) and try again.");
        var changed = new List<string>();
        foreach (var f in ConfigFiles(steamRoot)) {
            var text = File.ReadAllText(f);
            var app = FindAppBlock(text);
            if (app == null) continue;
            var (open, close) = app.Value;
            var lo = FindKey(text, open, close, "LaunchOptions");
            string updated;
            string merged;
            if (lo != null) {
                merged = Merge(Unescape(text[lo.Value.valStart..lo.Value.valEnd]), assignments);
                updated = text[..lo.Value.valStart] + Escape(merged) + text[lo.Value.valEnd..];
            } else {
                merged = Merge("", assignments);
                // insert a new key on its own line before the block's closing brace
                int lineStart = text.LastIndexOf('\n', close) + 1;
                string indent = text[lineStart..close] + "\t";
                updated = text[..lineStart] + $"{indent}\"LaunchOptions\"\t\t\"{Escape(merged)}\"\n" + text[lineStart..];
            }
            File.Copy(f, f + $".bak-{DateTime.Now:yyyyMMdd-HHmmss}", false);
            File.WriteAllText(f, updated);
            changed.Add($"{f}: {merged}");
        }
        if (changed.Count == 0)
            throw new InvalidOperationException("Overwatch wasn't found in any Steam user's config. Launch the game once from Steam first.");
        return changed;
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    static string Unescape(string s) => Regex.Replace(s, @"\\(.)", "$1");

    // --- tiny VDF navigation (keeps the file's formatting untouched) ---

    readonly record struct Tok(bool IsString, string Text, int Start, int End); // End exclusive; for strings, inner span

    static IEnumerable<Tok> Tokens(string s, int from, int to) {
        int i = from;
        while (i < to) {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < to && s[i + 1] == '/') { while (i < to && s[i] != '\n') i++; continue; }
            if (c is '{' or '}') { yield return new Tok(false, c.ToString(), i, i + 1); i++; continue; }
            if (c == '"') {
                int j = i + 1;
                var sb = new StringBuilder();
                while (j < to && s[j] != '"') {
                    if (s[j] == '\\' && j + 1 < to) { sb.Append(s[j + 1]); j += 2; continue; }
                    sb.Append(s[j]); j++;
                }
                yield return new Tok(true, sb.ToString(), i + 1, j);
                i = j + 1;
                continue;
            }
            int k = i;
            while (k < to && !char.IsWhiteSpace(s[k]) && s[k] is not ('{' or '}' or '"')) k++;
            yield return new Tok(true, s[i..k], i, k);
            i = k;
        }
    }

    /// <summary>Find the child block named key within [from,to): returns the
    /// positions of its '{' and matching '}'.</summary>
    static (int open, int close)? FindBlock(string s, int from, int to, string key) {
        int depth = 0;
        Tok? prev = null;
        int openPos = -1;
        foreach (var t in Tokens(s, from, to)) {
            if (!t.IsString && t.Text == "{") {
                if (depth == 0 && prev is { IsString: true } p && p.Text.Equals(key, StringComparison.OrdinalIgnoreCase))
                    openPos = t.Start;
                depth++;
            } else if (!t.IsString && t.Text == "}") {
                depth--;
                if (depth == 0 && openPos >= 0) return (openPos, t.Start);
            }
            prev = t;
        }
        return null;
    }

    static (int open, int close)? FindAppBlock(string s) {
        (int open, int close)? cur = (-1, s.Length);
        foreach (var key in new[] { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", Steam.AppId }) {
            cur = FindBlock(s, cur!.Value.open + 1, cur.Value.close, key);
            if (cur == null) return null;
        }
        return cur;
    }

    static (int valStart, int valEnd)? FindKey(string s, int open, int close, string key) {
        int depth = 0;
        Tok? prev = null;
        foreach (var t in Tokens(s, open + 1, close)) {
            if (!t.IsString) { depth += t.Text == "{" ? 1 : -1; prev = null; continue; }
            if (depth == 0 && prev is { IsString: true } p && p.Text.Equals(key, StringComparison.OrdinalIgnoreCase)) {
                return (t.Start, t.End);
            }
            prev = depth == 0 && prev == null ? t : null;
        }
        return null;
    }
}
