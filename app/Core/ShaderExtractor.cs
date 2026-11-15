using System.IO.Compression;
using TACTLib.Client;
using TACTLib.Client.HandlerArgs;
using TACTLib.Core.Product.Tank;
using TACTLib.Exceptions;
using TankLib;

namespace OwShaderPrecache.Core;

/// <summary>Extracts every ShaderCode asset (type 0x087, gzip-compressed
/// DXBC) from the Overwatch Steam install.</summary>
public static class ShaderExtractor {
    public sealed record Result(int Extracted, int Failed, long Bytes, string Version);

    public static Result Extract(string gameDir, string outDir, Action<int, int> progress, CancellationToken ct) {
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);

        // Decryption keys for encrypted game files, shipped inside this app.
        var keyring = Path.Combine(AppState.CacheDir, "bin", "Overwatch.keyring");
        Directory.CreateDirectory(Path.GetDirectoryName(keyring)!);
        using (var res = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Overwatch.keyring")) {
            if (res != null) { using var f = File.Create(keyring); res.CopyTo(f); }
        }

        ClientHandler client;
        try {
            client = new ClientHandler(gameDir, new ClientCreateArgs {
                SupportKeyring = keyring, // absolute path: Path.Combine keeps it as-is
                TextLanguage = "enUS",
                SpeechLanguage = "enUS",
                HandlerArgs = new ClientCreateArgs_Tank { ManifestRegion = ClientCreateArgs_Tank.REGION_DEV },
                Online = false,
            });
        } catch (Exception e) when (Unwrap(e) is UnsupportedBuildVersionException) {
            throw new InvalidOperationException(
                "This Overwatch build is newer than this app supports (a new patch came out). " +
                "Update the app once TACTLib adds the new build.", e);
        }
        if (client.ProductHandler is not ProductHandler_Tank tank)
            throw new InvalidOperationException("Not an Overwatch install.");
        client.InstallationInfo.Values.TryGetValue("Version", out var version);

        var guids = tank.m_assets.Keys.Where(g => teResourceGUID.Type(g) == 0x087).ToList();
        int done = 0, failed = 0;
        long bytes = 0;
        progress(0, guids.Count);
        Parallel.ForEach(guids, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, guid => {
            try {
                using var stream = tank.OpenFile(guid);
                if (stream == null) { Interlocked.Increment(ref failed); return; }
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var dxbc = FindDxbc(ms.ToArray());
                if (dxbc == null) { Interlocked.Increment(ref failed); return; }
                File.WriteAllBytes(Path.Combine(outDir, $"{guid:X16}.{Kind(dxbc)}.dxbc"), dxbc);
                Interlocked.Add(ref bytes, dxbc.Length);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                Interlocked.Increment(ref failed);
            }
            int d = Interlocked.Increment(ref done);
            if (d % 2000 == 0) progress(d, guids.Count);
        });
        progress(guids.Count, guids.Count);
        return new Result(done - failed, failed, bytes, version ?? "?");
    }

    static Exception Unwrap(Exception e) {
        while (e.InnerException != null && e is not UnsupportedBuildVersionException) e = e.InnerException;
        return e;
    }

    static bool IsDxbc(byte[] b, int at = 0) =>
        b.Length >= at + 32 && b[at] == 'D' && b[at + 1] == 'X' && b[at + 2] == 'B' && b[at + 3] == 'C';

    /// <summary>The ShaderCode header layout changes between builds, so find
    /// the payload by its signature instead of trusting a fixed struct.</summary>
    static byte[]? FindDxbc(byte[] file) {
        for (int i = 0; i + 3 < file.Length; i++) {
            if (file[i] == 0x1F && file[i + 1] == 0x8B && file[i + 2] == 0x08) {
                try {
                    using var gz = new GZipStream(new MemoryStream(file, i, file.Length - i), CompressionMode.Decompress);
                    using var ms = new MemoryStream();
                    gz.CopyTo(ms);
                    var outb = ms.ToArray();
                    if (IsDxbc(outb)) return outb;
                } catch { }
            } else if (IsDxbc(file, i)) {
                int size = BitConverter.ToInt32(file, i + 24);
                if (size > 32 && i + size <= file.Length) return file[i..(i + size)];
            }
        }
        return null;
    }

    /// <summary>Shader stage from the DXBC SHEX/SHDR version token.</summary>
    static string Kind(byte[] dxbc) {
        int chunks = BitConverter.ToInt32(dxbc, 28);
        for (int c = 0; c < chunks && 32 + c * 4 + 4 <= dxbc.Length; c++) {
            int off = BitConverter.ToInt32(dxbc, 32 + c * 4);
            if (off < 0 || off + 12 > dxbc.Length) continue;
            uint fourcc = BitConverter.ToUInt32(dxbc, off);
            if (fourcc is 0x58454853 or 0x52444853) { // SHEX / SHDR
                return (BitConverter.ToUInt32(dxbc, off + 8) >> 16) switch {
                    0 => "ps", 1 => "vs", 2 => "gs", 3 => "hs", 4 => "ds", 5 => "cs", _ => "xx"
                };
            }
        }
        return "xx";
    }
}
