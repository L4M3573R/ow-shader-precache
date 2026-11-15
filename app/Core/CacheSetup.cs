using System.Collections.Specialized;

namespace OwShaderPrecache.Core;

/// <summary>Where compiled shaders go and how the game is told to load them.
///
/// NVIDIA (proprietary): under Proton, Wine helper processes grab the first
/// numbered cache files, so the game never reads a file we write directly.
/// Instead each finished cache is renamed to a fixed name and added to the
/// driver's read-only list (__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME), which
/// every process - including the game - loads.
///
/// Mesa: the cache is shared between processes, so we compile straight into
/// the directory Steam gives the game.</summary>
public static class CacheSetup {
    public const string ExtractName = "shader_precache";                // game-files cache (first run)
    public const string GamePrefix = "shader_precache_g";               // later game-file additions g1..g8
    public const int GameSlots = 8;
    public const string ReplayPrefix = "shader_precache_r";             // replay caches r1..r4
    public const int ReplaySlots = 4;
    public const string WorkSuffix = "_work";

    /// <summary>Steam's own caches, which stay in the list.</summary>
    static readonly string[] SteamReadOnly = { "steam_shader_cache", "steamapp_merged_shader_cache" };

    public static IEnumerable<string> ReplayNames() =>
        Enumerable.Range(1, ReplaySlots).Select(i => $"{ReplayPrefix}{i}");

    /// <summary>Game-file caches: the first full compile, then one file per
    /// later run that found new shaders (e.g. after a patch).</summary>
    public static IEnumerable<string> GameNames() =>
        Enumerable.Range(1, GameSlots).Select(i => $"{GamePrefix}{i}").Prepend(ExtractName);

    public static IEnumerable<string> OurNames() => GameNames().Concat(ReplayNames());

    /// <summary>Steam's caches plus every cache this app installed and that exists now.</summary>
    public static List<string> Installed(GameInstall g) =>
        SteamReadOnly.Concat(OurNames().Where(n => Exists(g, n))).ToList();

    /// <summary>Everything the game should load read-only (missing files are ignored by the driver).</summary>
    public static string GameReadOnlyList() =>
        string.Join(";", SteamReadOnly.Concat(OurNames()));

    /// <summary>Remove every cache this app installed (driver/Proton changed, or a full rebuild).</summary>
    public static void DeleteAll(GameInstall g, IEnumerable<string> names) {
        foreach (var n in names) Delete(g, n);
    }

    public static long CacheBytes(int gib) => (long)gib * 1024 * 1024 * 1024;

    /// <summary>Environment for a compile run.</summary>
    /// <param name="writeName">NVIDIA: name of the writable cache for this run.</param>
    /// <param name="readOnly">NVIDIA: caches to treat as already installed.</param>
    public static Dictionary<string, string> Env(GameInstall g, CacheMode mode, int gib,
                                                 string writeName, IEnumerable<string> readOnly) {
        var env = new Dictionary<string, string>();
        if (mode == CacheMode.NvidiaProprietary) {
            Directory.CreateDirectory(g.NvidiaCacheDir); // the driver won't create it
            env["__GL_SHADER_DISK_CACHE"] = "1";
            env["__GL_SHADER_DISK_CACHE_PATH"] = g.NvidiaCacheDir;
            env["__GL_SHADER_DISK_CACHE_APP_NAME"] = writeName;
            env["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"] = string.Join(";", readOnly);
            env["__GL_SHADER_DISK_CACHE_SKIP_CLEANUP"] = "1";
            // A hard cap on writes, even with SKIP_CLEANUP; the default is far too small.
            env["__GL_SHADER_DISK_CACHE_SIZE"] = CacheBytes(gib).ToString();
        } else {
            Directory.CreateDirectory(g.ShaderCacheDir);
            env["MESA_SHADER_CACHE_DIR"] = g.ShaderCacheDir;
            env["MESA_GLSL_CACHE_DIR"] = g.ShaderCacheDir;
            env["MESA_SHADER_CACHE_MAX_SIZE"] = $"{gib}G";
            env["MESA_GLSL_CACHE_MAX_SIZE"] = $"{gib}G";
        }
        return env;
    }

    /// <summary>Strip things that must not leak into a compile run.</summary>
    public static void Sanitize(IDictionary<string, string?> env) {
        foreach (var k in new[] { "LD_PRELOAD", "ENABLE_VK_LAYER_VALVE_steam_fossilize_1",
                                  "ENABLE_VK_LAYER_VALVE_steam_overlay_1", "STEAM_FOSSILIZE_DUMP_PATH",
                                  "STEAM_FOSSILIZE_DUMP_PATH_READ_ONLY", "VK_INSTANCE_LAYERS",
                                  "MANGOHUD", "DXVK_HUD" })
            env.Remove(k);
        env["DISABLE_MANGOHUD"] = "1";
    }

    /// <summary>The NVIDIA driver's directory for the current driver/GPU
    /// (GLCache/&lt;driver&gt;/&lt;gpu&gt;): the most recently modified one.</summary>
    public static string? NvidiaDriverDir(GameInstall g) {
        var root = Path.Combine(g.NvidiaCacheDir, "GLCache");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root).SelectMany(Directory.GetDirectories)
                        .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public static bool Exists(GameInstall g, string name) {
        var d = NvidiaDriverDir(g);
        return d != null && File.Exists(Path.Combine(d, name + ".bin"));
    }

    public static void Delete(GameInstall g, string name) {
        var root = Path.Combine(g.NvidiaCacheDir, "GLCache");
        if (!Directory.Exists(root)) return;
        foreach (var f in Directory.EnumerateFiles(root, name + ".*", SearchOption.AllDirectories))
            if (f.EndsWith(".bin") || f.EndsWith(".toc")) File.Delete(f);
    }

    /// <summary>Delete leftover work files from an interrupted run.</summary>
    public static void DeleteWork(GameInstall g, string workName) {
        var root = Path.Combine(g.NvidiaCacheDir, "GLCache");
        if (!Directory.Exists(root)) return;
        foreach (var f in Directory.EnumerateFiles(root, workName + "*", SearchOption.AllDirectories))
            File.Delete(f);
    }

    /// <summary>Keep work an interrupted run already compiled: turn leftover
    /// work files into their (still free) cache slot. Returns MB recovered.</summary>
    public static long RecoverLeftovers(GameInstall g) {
        long recovered = 0;
        foreach (var name in OurNames()) {
            var work = name + WorkSuffix;
            var root = Path.Combine(g.NvidiaCacheDir, "GLCache");
            if (!Directory.Exists(root) ||
                !Directory.EnumerateFiles(root, work + "*.bin", SearchOption.AllDirectories).Any()) continue;
            if (Exists(g, name)) { DeleteWork(g, work); continue; }
            var fin = Finalize(g, work, name);
            if (fin is { } f) {
                if (f.size < 4096) Delete(g, name);
                else recovered += f.size;
            }
        }
        return recovered / (1024 * 1024);
    }

    /// <summary>Turn the biggest work fragment into the named read-only cache
    /// (smaller ones come from Wine helper processes and are discarded).</summary>
    public static (string path, long size)? Finalize(GameInstall g, string workName, string finalName) {
        var root = Path.Combine(g.NvidiaCacheDir, "GLCache");
        if (!Directory.Exists(root)) return null;
        var frags = Directory.EnumerateFiles(root, workName + "*.bin", SearchOption.AllDirectories)
                             .Select(f => new FileInfo(f)).OrderByDescending(f => f.Length).ToList();
        if (frags.Count == 0) return null;
        var best = frags[0];
        var dir = best.DirectoryName!;
        var toc = Path.ChangeExtension(best.FullName, ".toc");
        File.Move(best.FullName, Path.Combine(dir, finalName + ".bin"), true);
        File.Move(toc, Path.Combine(dir, finalName + ".toc"), true);
        DeleteWork(g, workName);
        return (Path.Combine(dir, finalName + ".bin"), best.Length);
    }
}
