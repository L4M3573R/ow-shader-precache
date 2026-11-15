using System.Security.Cryptography;
using System.Text;

namespace OwShaderPrecache.Core;

/// <summary>Remembers what has already been compiled, so later runs only do
/// what's left. Compiled code depends on the GPU driver and on the DXVK
/// version (Proton), so each combination gets its own list.</summary>
public sealed class Manifest {
    readonly string path;
    readonly HashSet<ulong> set = new();

    public int Count => set.Count;
    public bool Contains(ulong h) => set.Contains(h);
    public void AddRange(IEnumerable<ulong> hs) { foreach (var h in hs) set.Add(h); }

    Manifest(string path) {
        this.path = path;
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
            if (line.Length == 16 && ulong.TryParse(line, System.Globalization.NumberStyles.HexNumber, null, out var h))
                set.Add(h);
    }

    static string Dir => Path.Combine(AppState.ConfigDir, "compiled");

    /// <summary>Identifies the GPU driver + DXVK combination.</summary>
    public static string Key(CacheMode mode, string driverId, string dxvkId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{mode}|{driverId}|{dxvkId}")))[..16].ToLowerInvariant();

    /// <summary>kind: "shaders" (game-file DXBC hashes) or "pipelines" (replayed Fossilize hashes).</summary>
    public static Manifest Load(string key, string kind) => new(Path.Combine(Dir, $"{key}-{kind}.txt"));

    public void Save() {
        Directory.CreateDirectory(Dir);
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, set.Select(h => h.ToString("x16")));
        File.Move(tmp, path, true);
    }

    public void Clear() {
        set.Clear();
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>64-bit content hash used for shaders.</summary>
    public static ulong Hash(ReadOnlySpan<byte> data) => BitConverter.ToUInt64(SHA256.HashData(data), 0);
}
