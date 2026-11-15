using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace OwShaderPrecache.Core;

/// <summary>Runs shadercompile.exe under the game's Proton so DXVK translates
/// every extracted shader and the GPU driver compiles it into its disk cache.</summary>
public static class ShaderCompiler {
    public sealed record Result(int Compiled, int Failed, TimeSpan Elapsed);

    public static string ExtractHelper(string binDir) {
        Directory.CreateDirectory(binDir);
        var path = Path.Combine(binDir, "shadercompile.exe");
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("shadercompile.exe")
                        ?? throw new InvalidOperationException("embedded shadercompile.exe missing");
        using var f = File.Create(path);
        res.CopyTo(f);
        return path;
    }

    public static Result Run(GameInstall g, Steam.ProtonInfo proton, string helperExe, string prefixDir,
                             string shaderDir, Dictionary<string, string> cacheEnv,
                             Action<int, int> progress, Action<string> log, CancellationToken ct) {
        Directory.CreateDirectory(prefixDir);
        var logFile = Path.Combine(shaderDir, "shadercompile.log");
        if (File.Exists(logFile)) File.Delete(logFile);

        var psi = new ProcessStartInfo(proton.Script) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(helperExe);
        psi.ArgumentList.Add("Z:" + shaderDir.Replace('/', '\\'));
        CacheSetup.Sanitize(psi.Environment);
        psi.Environment["STEAM_COMPAT_DATA_PATH"] = prefixDir;
        psi.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = g.SteamRoot;
        psi.Environment["SteamAppId"] = "0";
        psi.Environment["SteamGameId"] = "0";
        psi.Environment["WINEDEBUG"] = "-all";
        psi.Environment["DXVK_LOG_LEVEL"] = "warn";
        psi.Environment["DXVK_SHADER_CACHE"] = "0";
        foreach (var (k, v) in cacheEnv) psi.Environment[k] = v;

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start Proton");
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, e) => {
            if (!string.IsNullOrWhiteSpace(e.Data) && e.Data.Contains("err:", StringComparison.Ordinal))
                log("[proton] " + e.Data);
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // Proton swallows the program's console output, so follow its log file.
        int compiled = 0, failed = 0;
        long pos = 0;
        var progressRe = new Regex(@"^progress (\d+)/(\d+)");
        var doneRe = new Regex(@"^done: (\d+) compiled, (\d+) failed");
        bool done = false;
        while (true) {
            bool exited = p.WaitForExit(1000);
            if (ct.IsCancellationRequested) {
                Kill(p, proton, prefixDir);
                ct.ThrowIfCancellationRequested();
            }
            if (File.Exists(logFile)) {
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(pos, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null) {
                    var m = progressRe.Match(line);
                    if (m.Success) { progress(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)); continue; }
                    var d = doneRe.Match(line);
                    if (d.Success) { compiled = int.Parse(d.Groups[1].Value); failed = int.Parse(d.Groups[2].Value); done = true; }
                    if (!line.StartsWith("failed:")) log(line);
                }
                pos = fs.Position;
            }
            if (exited) break;
        }
        if (!done)
            throw new InvalidOperationException(
                $"The shader compiler stopped before finishing (exit code {p.ExitCode}). Log: {logFile}");
        return new Result(compiled, failed, sw.Elapsed);
    }

    /// <summary>Compiles the shaders in <paramref name="shaderDir"/> in chunks,
    /// each in a fresh process: DXVK keeps every shader it has translated in
    /// memory until the process exits (~130 KB each), so one process for all
    /// ~140k shaders runs out of RAM. A finished chunk is reported right away
    /// (so an interrupted run loses nothing), and a chunk that keeps crashing is
    /// split up until the bad shaders are isolated and skipped.</summary>
    public static Result RunChunked(GameInstall g, Steam.ProtonInfo proton, string helperExe, string prefixDir,
                                    string shaderDir, Dictionary<string, string> cacheEnv, int chunkSize,
                                    Action<List<ulong>> chunkDone, Action<int, int> progress, Action<string> log,
                                    CancellationToken ct) {
        var files = Directory.GetFiles(shaderDir, "*.dxbc").OrderBy(f => f, StringComparer.Ordinal).ToList();
        int total = files.Count, done = 0, compiled = 0, failed = 0, skipped = 0;
        var sw = Stopwatch.StartNew();
        int dirNo = 0;

        void Process(List<string> chunk, int attempt) {
            ct.ThrowIfCancellationRequested();
            var dir = Path.Combine(shaderDir, $"chunk{dirNo++:D4}");
            Directory.CreateDirectory(dir);
            var moved = new List<string>();
            foreach (var f in chunk) {
                var dst = Path.Combine(dir, Path.GetFileName(f));
                File.Move(f, dst);
                moved.Add(dst);
            }
            int baseDone = done;
            try {
                var r = Run(g, proton, helperExe, prefixDir, dir, cacheEnv,
                            (d, _) => progress(baseDone + d, total), log, ct);
                compiled += r.Compiled;
                failed += r.Failed;
                done += chunk.Count;
                chunkDone(chunk.Select(HashOf).Where(h => h != 0).ToList());
                Directory.Delete(dir, true);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception e) {
                // put the files back so a retry/split can use them
                var back = moved.Select(m => { var b = Path.Combine(shaderDir, Path.GetFileName(m)); File.Move(m, b, true); return b; }).ToList();
                try { Directory.Delete(dir, true); } catch { }
                if (attempt == 0) {
                    log($"A compile chunk stopped ({e.Message.Split('.')[0]}); retrying it.");
                    Process(back, 1);
                } else if (back.Count > 50) {
                    int half = back.Count / 2;
                    Process(back.Take(half).ToList(), 1);
                    Process(back.Skip(half).ToList(), 1);
                } else {
                    skipped += back.Count;
                    done += back.Count;
                    log($"Skipped {back.Count} shaders that keep crashing the compiler; they'll be retried next run.");
                    foreach (var b in back) File.Delete(b);
                }
            }
            progress(done, total);
        }

        progress(0, total);
        for (int i = 0; i < files.Count; i += chunkSize)
            Process(files.Skip(i).Take(chunkSize).ToList(), 0);
        if (skipped > 0) log($"{skipped} shaders were skipped.");
        return new Result(compiled, failed + skipped, sw.Elapsed);
    }

    static ulong HashOf(string path) {
        var name = Path.GetFileName(path);
        var dot = name.IndexOf('.');
        return dot == 16 && ulong.TryParse(name[..16], System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : 0;
    }

    static void Kill(Process p, Steam.ProtonInfo proton, string prefixDir) {
        try { p.Kill(true); } catch { }
        foreach (var ws in new[] { "files/bin/wineserver", "dist/bin/wineserver" }) {
            var exe = Path.Combine(proton.Dir, ws);
            if (!File.Exists(exe)) continue;
            try {
                var psi = new ProcessStartInfo(exe, "-k") { UseShellExecute = false };
                psi.Environment["WINEPREFIX"] = Path.Combine(prefixDir, "pfx");
                Process.Start(psi)?.WaitForExit(10000);
            } catch { }
        }
    }
}
