using System.IO.Compression;
using System.Text.Json;

namespace OwShaderPrecache.Core;

/// <summary>Minimal reader for Fossilize (.foz) pipeline databases.</summary>
public static class Foz {
    public const int TagAppInfo = 0, TagGraphics = 6, TagCompute = 7;
    static readonly byte[] Magic = { 0x81, (byte)'F', (byte)'O', (byte)'S', (byte)'S', (byte)'I',
                                     (byte)'L', (byte)'I', (byte)'Z', (byte)'E', (byte)'D', (byte)'B' };

    public readonly record struct Entry(int Tag, ulong Hash, uint Flags, uint Size, long Offset);

    public static IEnumerable<Entry> Entries(string path) {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var head = new byte[16];
        if (f.Read(head, 0, 16) < 16 || !head.AsSpan(0, 12).SequenceEqual(Magic)) yield break;
        var key = new byte[40];
        var hdr = new byte[16];
        while (true) {
            if (f.Read(key, 0, 40) < 40 || f.Read(hdr, 0, 16) < 16) yield break;
            int tag;
            ulong hash;
            try {
                tag = (int)Convert.ToUInt64(System.Text.Encoding.ASCII.GetString(key, 0, 24), 16);
                hash = Convert.ToUInt64(System.Text.Encoding.ASCII.GetString(key, 24, 16), 16);
            } catch { yield break; } // truncated / corrupt tail
            uint size = BitConverter.ToUInt32(hdr, 0), flags = BitConverter.ToUInt32(hdr, 4);
            long off = f.Position;
            yield return new Entry(tag, hash, flags, size, off);
            f.Seek(off + size, SeekOrigin.Begin);
        }
    }

    public static byte[] Payload(string path, Entry e) {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        f.Seek(e.Offset, SeekOrigin.Begin);
        var data = new byte[e.Size];
        f.ReadExactly(data);
        if (e.Flags == 2) {
            using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            data = ms.ToArray();
        }
        return data;
    }

    /// <summary>(engineName, engineVersion) of the first application info entry.</summary>
    public static (string? name, long? version) AppInfo(string path) {
        try {
            foreach (var e in Entries(path)) {
                if (e.Tag != TagAppInfo) continue;
                using var doc = JsonDocument.Parse(Payload(path, e));
                var ai = doc.RootElement.TryGetProperty("applicationInfo", out var x) ? x : doc.RootElement;
                if (ai.TryGetProperty("engineName", out var n) && ai.TryGetProperty("engineVersion", out var v))
                    return (n.GetString(), v.GetInt64());
            }
        } catch { }
        return (null, null);
    }

    public static HashSet<ulong> Hashes(IEnumerable<string> paths, int tag) {
        var set = new HashSet<ulong>();
        foreach (var p in paths)
            foreach (var e in Entries(p))
                if (e.Tag == tag) set.Add(e.Hash);
        return set;
    }

    /// <summary>Graphics pipelines that are DXVK per-shader libraries (one
    /// vertex or fragment stage, not linked from other libraries).</summary>
    public static HashSet<ulong> ShaderLibraries(IEnumerable<string> paths) {
        var libs = new HashSet<ulong>();
        var seen = new HashSet<ulong>();
        foreach (var p in paths) {
            foreach (var e in Entries(p)) {
                if (e.Tag != TagGraphics || !seen.Add(e.Hash)) continue;
                try {
                    using var doc = JsonDocument.Parse(Payload(p, e));
                    foreach (var pipe in doc.RootElement.GetProperty("graphicsPipelines").EnumerateObject()) {
                        var gp = pipe.Value;
                        bool linked = gp.TryGetProperty("pNext", out var pn) && pn.ValueKind == JsonValueKind.Array &&
                                      pn.EnumerateArray().Any(n => n.TryGetProperty("sType", out var s) && s.GetInt64() == 1000290000);
                        var stages = gp.TryGetProperty("stages", out var st) && st.ValueKind == JsonValueKind.Array
                            ? st.EnumerateArray().Select(x => x.GetProperty("stage").GetInt32()).ToList() : new List<int>();
                        if (!linked && stages.Count == 1 && stages[0] is 1 or 16) libs.Add(e.Hash);
                    }
                } catch { }
            }
        }
        return libs;
    }
}

/// <summary>Steam's per-bucket Fossilize databases for the game.</summary>
public sealed class FozSet {
    public List<string> Local { get; } = new();      // recorded on this PC
    public List<string> Community { get; } = new();  // Valve's database
    public IEnumerable<string> All => Community.Concat(Local);

    /// <summary>The DX11 (DXVK) bucket(s). Local recordings are limited to the
    /// newest DXVK version: older ones are never requested again.</summary>
    public static FozSet ForDxvk(GameInstall g) {
        var set = new FozSet();
        var root = Path.Combine(g.ShaderCacheDir, "fozpipelinesv6");
        if (!Directory.Exists(root)) return set;
        foreach (var dir in Directory.GetDirectories(root, "steamapprun_pipeline_cache.*")) {
            var bucket = Path.Combine(dir, "bucket.json");
            if (!File.Exists(bucket) || !File.ReadAllText(bucket).Contains("\"DXVK\"")) continue;
            var community = Path.Combine(dir, "steam_pipeline_cache.foz");
            if (File.Exists(community) && new FileInfo(community).Length > 16) set.Community.Add(community);
            var local = Directory.GetFiles(dir, "steamapprun_pipeline_cache.*.foz")
                                 .Where(f => new FileInfo(f).Length > 16).ToList();
            if (local.Count > 0) {
                var newest = local.OrderByDescending(File.GetLastWriteTimeUtc).First();
                var ver = Foz.AppInfo(newest).version;
                set.Local.AddRange(local.Where(f => ver == null || Foz.AppInfo(f).version == ver));
            }
        }
        return set;
    }

    /// <summary>Only the files from the most recent local play session(s)
    /// with the current DXVK (used to verify coverage).</summary>
    public List<string> RecentLocal(int sessions = 2) {
        if (Local.Count == 0) return new List<string>();
        var newest = Local.OrderByDescending(File.GetLastWriteTimeUtc).First();
        var prefix = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(newest), @"\.\d+\.foz$", "");
        return Local.Where(f => Path.GetFileName(f).StartsWith(prefix + "."))
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(sessions).ToList();
    }
}
