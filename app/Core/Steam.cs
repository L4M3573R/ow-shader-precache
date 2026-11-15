using System.Text.RegularExpressions;

namespace OwShaderPrecache.Core;

/// <summary>An Overwatch install found in a Steam library.</summary>
public sealed record GameInstall(string SteamRoot, string Library, string GameDir) {
    public string ShaderCacheDir => Path.Combine(Library, "steamapps", "shadercache", Steam.AppId);
    public string CompatDataDir => Path.Combine(Library, "steamapps", "compatdata", Steam.AppId);
    public string NvidiaCacheDir => Path.Combine(ShaderCacheDir, "nvidiav1");
}

public static class Steam {
    public const string AppId = "2357570"; // Overwatch on Steam
    const string FlatpakId = "com.valvesoftware.Steam";

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static List<string> Roots() {
        var cands = new List<string>();
        var env = Environment.GetEnvironmentVariable("STEAM_ROOT");
        if (!string.IsNullOrEmpty(env)) cands.Add(env);
        cands.AddRange(new[] {
            Path.Combine(Home, ".local/share/Steam"),
            Path.Combine(Home, ".steam/steam"),
            Path.Combine(Home, ".steam/root"),
            Path.Combine(Home, ".var/app", FlatpakId, ".local/share/Steam"),
            Path.Combine(Home, ".var/app", FlatpakId, "data/Steam"),
            Path.Combine(Home, "snap/steam/common/.local/share/Steam"),
        });
        var seen = new HashSet<string>();
        var roots = new List<string>();
        foreach (var c in cands) {
            if (!Directory.Exists(c)) continue;
            var real = RealPath(c);
            if (!seen.Add(real)) continue;
            if (Directory.Exists(Path.Combine(real, "steamapps")) ||
                File.Exists(Path.Combine(real, "ubuntu12_64", "fossilize_replay")))
                roots.Add(real);
        }
        return roots;
    }

    public static string RealPath(string p) {
        try {
            var info = new DirectoryInfo(p);
            var target = info.ResolveLinkTarget(true);
            return Path.GetFullPath(target?.FullName ?? info.FullName).TrimEnd('/');
        } catch {
            return Path.GetFullPath(p).TrimEnd('/');
        }
    }

    public static List<string> Libraries(string root) {
        var libs = new List<string> { root };
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf)) {
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        }
        return libs.Where(Directory.Exists).Select(RealPath).Distinct().ToList();
    }

    /// <summary>Find Overwatch in any Steam library on this machine.</summary>
    public static GameInstall? Find() {
        foreach (var root in Roots())
            foreach (var lib in Libraries(root)) {
                var acf = Path.Combine(lib, "steamapps", $"appmanifest_{AppId}.acf");
                if (!File.Exists(acf)) continue;
                var dir = InstallDirFromManifest(acf, lib);
                if (dir != null) return new GameInstall(root, lib, dir);
            }
        return null;
    }

    static string? InstallDirFromManifest(string acf, string lib) {
        var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
        var dir = Path.Combine(lib, "steamapps", "common", m.Success ? m.Groups[1].Value : "Overwatch");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Build a GameInstall from a folder the user picked.</summary>
    public static GameInstall? FromGameDir(string gameDir, out string error) {
        error = "";
        gameDir = gameDir.TrimEnd('/');
        if (!File.Exists(Path.Combine(gameDir, "Overwatch.exe"))) {
            error = "That folder doesn't contain Overwatch.exe.";
            return null;
        }
        if (!File.Exists(Path.Combine(gameDir, "data", ".build.config"))) {
            error = "That doesn't look like the Steam version of Overwatch (data/.build.config missing).";
            return null;
        }
        // <library>/steamapps/common/<game>
        var common = Directory.GetParent(gameDir);
        var steamapps = common?.Parent;
        var lib = steamapps?.Parent;
        if (lib == null || steamapps!.Name != "steamapps") {
            error = "The game folder must be inside a Steam library (…/steamapps/common/Overwatch).";
            return null;
        }
        var libPath = RealPath(lib.FullName);
        var root = Roots().FirstOrDefault(r => Libraries(r).Contains(libPath)) ?? Roots().FirstOrDefault();
        if (root == null) {
            error = "No Steam installation found.";
            return null;
        }
        return new GameInstall(root, libPath, RealPath(gameDir));
    }

    public static bool IsRunning() {
        foreach (var d in Directory.EnumerateDirectories("/proc")) {
            if (!int.TryParse(Path.GetFileName(d), out _)) continue;
            try {
                var comm = File.ReadAllText(Path.Combine(d, "comm")).Trim();
                if (comm is "steam" or "steamwebhelper") return true;
            } catch { }
        }
        return false;
    }

    /// <summary>Is the game (or anything launched for it by Steam) running?</summary>
    public static bool GameRunning() {
        var needle = System.Text.Encoding.ASCII.GetBytes($"SteamAppId={AppId}\0");
        foreach (var d in Directory.EnumerateDirectories("/proc")) {
            if (!int.TryParse(Path.GetFileName(d), out var pid) || pid == Environment.ProcessId) continue;
            try {
                var env = File.ReadAllBytes(Path.Combine(d, "environ"));
                if (env.AsSpan().IndexOf(needle) >= 0) return true;
            } catch { }
        }
        return false;
    }

    public sealed record ProtonInfo(string Name, string Dir) {
        public string Script => Path.Combine(Dir, "proton");
        public override string ToString() => Name;
    }

    /// <summary>All Proton versions installed for this Steam.</summary>
    public static List<ProtonInfo> Protons(GameInstall g) {
        var list = new List<ProtonInfo>();
        void Scan(string dir) {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir))
                if (File.Exists(Path.Combine(d, "proton")))
                    list.Add(new ProtonInfo(Path.GetFileName(d), RealPath(d)));
        }
        Scan(Path.Combine(g.SteamRoot, "compatibilitytools.d"));
        Scan("/usr/share/steam/compatibilitytools.d");
        foreach (var lib in Libraries(g.SteamRoot))
            Scan(Path.Combine(lib, "steamapps", "common"));
        return list.GroupBy(p => p.Dir).Select(x => x.First()).OrderBy(p => p.Name).ToList();
    }

    /// <summary>The Proton the game was last run with (its DXVK must be used
    /// so the generated shader code is identical).</summary>
    public static ProtonInfo? GameProton(GameInstall g, List<ProtonInfo> all) {
        var cfg = Path.Combine(g.CompatDataDir, "config_info");
        if (File.Exists(cfg)) {
            var lines = File.ReadAllLines(cfg);
            foreach (var line in lines.Skip(1)) {
                var i = line.IndexOf("/files/", StringComparison.Ordinal);
                if (i < 0) continue;
                var dir = RealPath(line[..i]);
                var hit = all.FirstOrDefault(p => p.Dir == dir);
                if (hit != null) return hit;
                if (File.Exists(Path.Combine(dir, "proton"))) return new ProtonInfo(Path.GetFileName(dir), dir);
            }
        }
        return null;
    }

    public static string? Fossilize(GameInstall g) {
        var p = Path.Combine(g.SteamRoot, "ubuntu12_64", "fossilize_replay");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Overwatch build number from data/.build.config.</summary>
    public static string? GameBuild(string gameDir) {
        var cfg = Path.Combine(gameDir, "data", ".build.config");
        if (!File.Exists(cfg)) return null;
        var m = Regex.Match(File.ReadAllText(cfg), @"^build-number\s*=\s*(\d+)", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }
}
