using System.Text.Json;

namespace OwShaderPrecache.Core;

/// <summary>What was compiled, for which game build and driver.</summary>
public sealed class AppState {
    public string? GameBuild { get; set; }
    public string? DriverId { get; set; }
    public string? Mode { get; set; }
    public DateTime? CompiledAt { get; set; }
    public int ShadersCompiled { get; set; }
    public DateTime? ReplayedAt { get; set; }
    public string? GameDir { get; set; }
    /// <summary>Driver + DXVK combination the installed caches were built for.</summary>
    public string? CacheKey { get; set; }

    public static string ConfigDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "ow-shader-precache");

    public static string CacheDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "ow-shader-precache");

    static string FilePath => Path.Combine(ConfigDir, "state.json");

    public static AppState Load() {
        try { return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) ?? new AppState(); }
        catch { return new AppState(); }
    }

    public void Save() {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
