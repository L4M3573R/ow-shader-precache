// ShaderDump: extract every ShaderCode asset (type 0x087, gzip-compressed DXBC)
// from an Overwatch install (Battle.net CASC or Steam static container).
//
// Output layout:
//   <out>/<guid>.<vs|ps|gs|hs|ds|cs|xx>.dxbc   raw DXBC blob
//   <out>/manifest.tsv                          guid, kind, so_count, size, dxbc_hash
//
// Usage: ShaderDump <overwatch dir> <out dir> [--limit N]

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using TACTLib.Client;
using TACTLib.Client.HandlerArgs;
using TACTLib.Core.Product.Tank;
using TankLib;

if (args.Length < 2) {
    Console.Error.WriteLine("usage: ShaderDump <overwatch dir> <out dir> [--limit N]");
    return 2;
}
string gameDir = args[0], outDir = args[1];
int limit = int.MaxValue, raw = 0;
for (int i = 2; i < args.Length; i++) {
    if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[++i]);
    else if (args[i] == "--raw" && i + 1 < args.Length) raw = int.Parse(args[++i]);
}

Directory.CreateDirectory(outDir);
var sw = Stopwatch.StartNew();

var createArgs = new ClientCreateArgs {
    TextLanguage = "enUS",
    SpeechLanguage = "enUS",
    HandlerArgs = new ClientCreateArgs_Tank { ManifestRegion = ClientCreateArgs_Tank.REGION_DEV },
    Online = false,
};
var client = new ClientHandler(gameDir, createArgs);
if (client.ProductHandler is not ProductHandler_Tank tank) {
    Console.Error.WriteLine($"not an Overwatch install (product: {client.Product})");
    return 1;
}
client.InstallationInfo.Values.TryGetValue("Version", out var ver);
Console.WriteLine($"opened {gameDir} (static container: {client.IsStaticContainer}, version {ver}) in {sw.Elapsed.TotalSeconds:F1}s");

var guids = tank.m_assets.Keys.Where(g => teResourceGUID.Type(g) == 0x087).OrderBy(g => g).Take(limit).ToList();
Console.WriteLine($"{guids.Count} ShaderCode assets");

static string Kind(Enums.teSHADER_TYPE t, byte[] dxbc) {
    // Prefer the program type from the DXBC SHEX/SHDR chunk: it is authoritative.
    // version token: bits 16..31 = program type (0 PS, 1 VS, 2 GS, 3 HS, 4 DS, 5 CS)
    if (dxbc.Length > 32 && dxbc[0] == 'D' && dxbc[1] == 'X' && dxbc[2] == 'B' && dxbc[3] == 'C') {
        int chunks = BitConverter.ToInt32(dxbc, 28);
        for (int c = 0; c < chunks && 32 + c * 4 + 4 <= dxbc.Length; c++) {
            int off = BitConverter.ToInt32(dxbc, 32 + c * 4);
            if (off < 0 || off + 12 > dxbc.Length) continue;
            uint fourcc = BitConverter.ToUInt32(dxbc, off);
            if (fourcc == 0x58454853 /*SHEX*/ || fourcc == 0x52444853 /*SHDR*/) {
                uint token = BitConverter.ToUInt32(dxbc, off + 8);
                return (token >> 16) switch {
                    0 => "ps", 1 => "vs", 2 => "gs", 3 => "hs", 4 => "ds", 5 => "cs", _ => "xx"
                };
            }
        }
    }
    return t switch {
        Enums.teSHADER_TYPE.VERTEX => "vs", Enums.teSHADER_TYPE.PIXEL => "ps",
        Enums.teSHADER_TYPE.GEOMETRY => "gs", Enums.teSHADER_TYPE.COMPUTE => "cs", _ => "xx"
    };
}

static bool IsDxbc(byte[] b, int at = 0) =>
    b.Length >= at + 32 && b[at] == 'D' && b[at + 1] == 'X' && b[at + 2] == 'B' && b[at + 3] == 'C';

// The ShaderCode header layout changes between game builds, so instead of
// trusting TankLib's struct, locate the payload: raw DXBC, gzip or zstd.
static byte[]? FindDxbc(byte[] file, out string how) {
    // 1. uncompressed DXBC somewhere in the file (size from the DXBC header)
    for (int i = 0; i + 32 <= file.Length; i++) {
        if (IsDxbc(file, i)) {
            int size = BitConverter.ToInt32(file, i + 24);
            if (size > 32 && i + size <= file.Length) { how = $"raw@{i}"; return file[i..(i + size)]; }
        }
    }
    // 2. compressed streams
    for (int i = 0; i + 4 <= file.Length; i++) {
        try {
            if (file[i] == 0x1F && file[i + 1] == 0x8B && file[i + 2] == 0x08) {
                using var gz = new System.IO.Compression.GZipStream(new MemoryStream(file, i, file.Length - i),
                                                                    System.IO.Compression.CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                var outb = ms.ToArray();
                if (IsDxbc(outb)) { how = $"gzip@{i}"; return outb; }
            } else if (file[i] == 0x28 && file[i + 1] == 0xB5 && file[i + 2] == 0x2F && file[i + 3] == 0xFD) {
                using var z = new ZstdNet.Decompressor();
                var outb = z.Unwrap(new ArraySegment<byte>(file, i, file.Length - i)).ToArray();
                if (IsDxbc(outb)) { how = $"zstd@{i}"; return outb; }
            } else if (file[i] == 0x78 && (file[i + 1] == 0x9C || file[i + 1] == 0xDA || file[i + 1] == 0x01)) {
                using var zl = new System.IO.Compression.ZLibStream(new MemoryStream(file, i, file.Length - i),
                                                                    System.IO.Compression.CompressionMode.Decompress);
                using var ms = new MemoryStream();
                zl.CopyTo(ms);
                var outb = ms.ToArray();
                if (IsDxbc(outb)) { how = $"zlib@{i}"; return outb; }
            }
        } catch { /* not a valid stream at this offset; keep scanning */ }
    }
    how = "none";
    return null;
}

if (raw > 0) {
    string rawDir = Path.Combine(outDir, "raw");
    Directory.CreateDirectory(rawDir);
    foreach (var g in guids.Take(raw)) {
        using var s = tank.OpenFile(g);
        if (s == null) continue;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        File.WriteAllBytes(Path.Combine(rawDir, $"{g:X16}.087"), ms.ToArray());
    }
    Console.WriteLine($"wrote {Math.Min(raw, guids.Count)} raw assets to {rawDir}");
}

var hows = new ConcurrentDictionary<string, int>();
var rows = new ConcurrentBag<string>();
var kinds = new ConcurrentDictionary<string, int>();
int done = 0, failed = 0, notDxbc = 0;
long bytes = 0;
Parallel.ForEach(guids, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, guid => {
    try {
        using var stream = tank.OpenFile(guid);
        if (stream == null) { Interlocked.Increment(ref failed); return; }
        using var fileMs = new MemoryStream();
        stream.CopyTo(fileMs);
        var file = fileMs.ToArray();
        var bc = FindDxbc(file, out var how);
        hows.AddOrUpdate(how.Split('@')[0], 1, (_, v) => v + 1);
        if (bc == null) { Interlocked.Increment(ref notDxbc); return; }
        // header fields from TankLib's (possibly outdated) layout: informational only
        uint soCount = file.Length >= 24 ? BitConverter.ToUInt32(file, 20) : 0;
        ushort teType = file.Length >= 38 ? BitConverter.ToUInt16(file, 36) : (ushort)0;
        var sc = new { Header = new { StreamOutDescCount = soCount, ShaderType = (Enums.teSHADER_TYPE)teType } };
        string kind = Kind(sc.Header.ShaderType, bc);
        kinds.AddOrUpdate(kind, 1, (_, v) => v + 1);
        string name = $"{guid:X16}.{kind}.dxbc";
        File.WriteAllBytes(Path.Combine(outDir, name), bc);
        string hash = Convert.ToHexString(SHA1.HashData(bc))[..16];
        rows.Add($"{guid:X16}\t{kind}\t{sc.Header.StreamOutDescCount}\t{bc.Length}\t{hash}\t{(int)sc.Header.ShaderType}");
        Interlocked.Add(ref bytes, bc.Length);
        int d = Interlocked.Increment(ref done);
        if (d % 20000 == 0) Console.WriteLine($"  {d}/{guids.Count}");
    } catch (Exception e) {
        if (Interlocked.Increment(ref failed) <= 5) Console.Error.WriteLine($"{guid:X16}: {e.GetType().Name}: {e.Message}");
    }
});

File.WriteAllLines(Path.Combine(outDir, "manifest.tsv"),
    new[] { "guid\tkind\tso_count\tsize\tsha1_16\tte_type" }.Concat(rows.OrderBy(r => r)));
Console.WriteLine($"extracted {done} shaders ({bytes / 1048576.0:F1} MB), failed {failed}, non-DXBC {notDxbc}, in {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine("by kind: " + string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
Console.WriteLine("payload: " + string.Join(", ", hows.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
return failed > 0 && done == 0 ? 1 : 0;
