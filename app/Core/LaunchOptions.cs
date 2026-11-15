using System.Text;
using System.Text.RegularExpressions;

namespace OwShaderPrecache.Core;

/// <summary>Writes the environment the game needs into Steam's per-game
/// launch options (userdata/&lt;id&gt;/config/localconfig.vdf). Steam must be
/// closed, otherwise it overwrites the file on exit.</summary>
public static class LaunchOptions {
    static readonly string[] OurVars = {
        "__GL_SHADER_DISK_CACHE", "__GL_SHADER_DISK_CACHE_PATH",
        "__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME", "__GL_SHADER_DISK_CACHE_SIZE",
        "__GL_SHADER_DISK_CACHE_SKIP_CLEANUP", "MESA_SHADER_CACHE_DIR", "MESA_SHADER_CACHE_MAX_SIZE",
    };

    /// <summary>The cache folder is set explicitly, so the game finds the
    /// precompiled cache even with Steam's Shader Pre-Caching turned off
    /// (Steam only points the driver at its shadercache folder while it's on).</summary>
    public static string Assignments(GameInstall g, CacheMode mode, int gib) => mode == CacheMode.NvidiaProprietary
        ? $"__GL_SHADER_DISK_CACHE=1 __GL_SHADER_DISK_CACHE_PATH={Quote(g.NvidiaCacheDir)} " +
          $"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME={Quote(CacheSetup.GameReadOnlyList())} " +
          $"__GL_SHADER_DISK_CACHE_SIZE={CacheSetup.CacheBytes(gib)} __GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1"
        : $"MESA_SHADER_CACHE_DIR={Quote(g.ShaderCacheDir)} MESA_SHADER_CACHE_MAX_SIZE={gib}G";

    /// <summary>True when the launch options hold what the current version
    /// writes (older versions didn't set the cache folder).</summary>
    public static bool IsApplied(string? launchOptions, GameInstall g, CacheMode mode) {
        if (launchOptions == null) return false;
        var need = mode == CacheMode.NvidiaProprietary
            ? new[] { $"__GL_SHADER_DISK_CACHE_PATH={Quote(g.NvidiaCacheDir)}",
                      $"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME={Quote(CacheSetup.GameReadOnlyList())}" }
            : new[] { $"MESA_SHADER_CACHE_DIR={Quote(g.ShaderCacheDir)}", "MESA_SHADER_CACHE_MAX_SIZE=" };
        return need.All(launchOptions.Contains);
    }

    /// <summary>Double-quote a value for the shell that runs the launch options.</summary>
    static string Quote(string v) =>
        "\"" + Regex.Replace(v, @"[""\\$`]", m => "\\" + m.Value) + "\"";

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
            var merged = Merge(lo == null ? "" : Unescape(text[lo.Value.valStart..lo.Value.valEnd]), assignments);
            var updated = SetKey(text, open, close, "LaunchOptions", merged);
            Backup(f);
            File.WriteAllText(f, updated);
            changed.Add($"{f}: {merged}");
        }
        if (changed.Count == 0)
            throw new InvalidOperationException("Overwatch wasn't found in any Steam user's config. Launch the game once from Steam first.");
        return changed;
    }

    /// <summary>Turn off Steam's Shader Pre-Caching (Settings → Downloads),
    /// for NVIDIA: the precompiled cache already holds everything Steam would
    /// process before a launch. This is a Steam-wide setting
    /// (config/config.vdf → ShaderCacheManager). Steam must be closed.
    /// Returns false when it was already off.</summary>
    public static bool DisableSteamShaderPreCaching(string steamRoot, bool skipSteamCheck = false) {
        if (!skipSteamCheck && Steam.IsRunning())
            throw new InvalidOperationException("Steam is running. Exit Steam completely (tray icon → Exit) and try again.");
        var f = Path.Combine(steamRoot, "config", "config.vdf");
        if (!File.Exists(f)) throw new InvalidOperationException($"Steam's settings file wasn't found: {f}");
        var onDisk = File.ReadAllText(f);
        var text = onDisk;
        var steam = FindPath(text, "InstallConfigStore", "Software", "Valve", "Steam")
            ?? throw new InvalidOperationException($"Unexpected layout in {f}.");
        var scm = FindBlock(text, steam.open + 1, steam.close, "ShaderCacheManager");
        if (scm == null) {
            int lineStart = text.LastIndexOf('\n', steam.close) + 1;
            string indent = text[lineStart..steam.close] + "\t";
            text = text[..lineStart] + $"{indent}\"ShaderCacheManager\"\n{indent}{{\n{indent}}}\n" + text[lineStart..];
        }
        foreach (var (key, val) in new[] { ("DisableShaderCache", "1"), ("EnableShaderBackgroundProcessing", "0") }) {
            var b = FindPath(text, "InstallConfigStore", "Software", "Valve", "Steam", "ShaderCacheManager")!.Value;
            var cur = FindKey(text, b.open, b.close, key);
            if (cur != null && text[cur.Value.valStart..cur.Value.valEnd] == val) continue;
            text = SetKey(text, b.open, b.close, key, val);
        }
        if (text == onDisk) return false;
        Backup(f);
        File.WriteAllText(f, text);
        return true;
    }

    /// <summary>Set key's value inside the block [open, close], adding it if missing.</summary>
    static string SetKey(string text, int open, int close, string key, string value) {
        var k = FindKey(text, open, close, key);
        if (k != null) return text[..k.Value.valStart] + Escape(value) + text[k.Value.valEnd..];
        // insert a new key on its own line before the block's closing brace
        int lineStart = text.LastIndexOf('\n', close) + 1;
        string indent = text[lineStart..close] + "\t";
        return text[..lineStart] + $"{indent}\"{key}\"\t\t\"{Escape(value)}\"\n" + text[lineStart..];
    }

    static void Backup(string f) => File.Copy(f, f + $".bak-{DateTime.Now:yyyyMMdd-HHmmss}", false);

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

    static (int open, int close)? FindPath(string s, params string[] keys) {
        (int open, int close)? cur = (-1, s.Length);
        foreach (var key in keys) {
            cur = FindBlock(s, cur!.Value.open + 1, cur.Value.close, key);
            if (cur == null) return null;
        }
        return cur;
    }

    static (int open, int close)? FindAppBlock(string s) =>
        FindPath(s, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", Steam.AppId);

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
