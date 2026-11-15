using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OwShaderPrecache.Core;

/// <summary>Replays Steam's recorded pipelines (this PC's play sessions plus
/// Valve's community database) with Steam's fossilize_replay. Everything that
/// is already installed is attached read-only, so the driver skips it and only
/// missing pipelines are compiled.</summary>
public static class Replayer {
    public sealed record Result(int Replayed, int Total, int FailedChunks, TimeSpan Elapsed);

    static Process Start(string fossilize, IEnumerable<string> args, Dictionary<string, string> env) {
        var psi = new ProcessStartInfo(fossilize) {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        CacheSetup.Sanitize(psi.Environment);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start fossilize_replay");
    }

    static (int code, string output) RunToEnd(Process p, CancellationToken ct) {
        var sb = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        while (!p.WaitForExit(500)) {
            if (ct.IsCancellationRequested) {
                try { p.Kill(true); } catch { }
                ct.ThrowIfCancellationRequested();
            }
        }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    public sealed record Plan(string? FozPath, List<ulong> NewPipelines, int TotalRecorded);

    /// <summary>Writes a reduced database holding only the recorded pipelines
    /// not compiled yet, plus what they need (shader modules, parent pipeline
    /// libraries, layouts). Returns FozPath = null when nothing is left to do.</summary>
    public static Plan BuildIncremental(FozSet dbs, Manifest done, string outPath, Action<string> log, CancellationToken ct) {
        var files = dbs.All.ToList();
        var streams = files.ToDictionary(f => f, f => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16));
        try {
            // index every object by (tag, hash); first occurrence wins
            var index = new Dictionary<(int, ulong), (string file, Foz.Entry e)>();
            foreach (var f in files)
                foreach (var e in Foz.Entries(f)) {
                    ct.ThrowIfCancellationRequested();
                    index.TryAdd((e.Tag, e.Hash), (f, e));
                }
            var pipelines = index.Keys.Where(k => k.Item1 is Foz.TagGraphics or Foz.TagCompute).ToList();
            var fresh = pipelines.Where(k => !done.Contains(k.Item2)).ToList();
            if (fresh.Count == 0) return new Plan(null, new List<ulong>(), pipelines.Count);

            // resolve dependencies
            var needPipes = new HashSet<(int, ulong)>(fresh);
            var needModules = new HashSet<ulong>();
            var queue = new Queue<(int, ulong)>(fresh);
            while (queue.Count > 0) {
                ct.ThrowIfCancellationRequested();
                var k = queue.Dequeue();
                if (!index.TryGetValue(k, out var loc)) continue;
                try {
                    using var doc = System.Text.Json.JsonDocument.Parse(ReadPayload(streams[loc.file], loc.e));
                    var rootName = k.Item1 == Foz.TagGraphics ? "graphicsPipelines" : "computePipelines";
                    foreach (var p in doc.RootElement.GetProperty(rootName).EnumerateObject()) {
                        var v = p.Value;
                        if (v.TryGetProperty("stages", out var stages) && stages.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var st in stages.EnumerateArray()) AddModule(st);
                        if (v.TryGetProperty("stage", out var cst)) AddModule(cst);
                        if (v.TryGetProperty("pNext", out var pn) && pn.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var n in pn.EnumerateArray())
                                if (n.TryGetProperty("libraries", out var libs))
                                    foreach (var l in libs.EnumerateArray()) {
                                        var lk = (Foz.TagGraphics, Convert.ToUInt64(l.GetString(), 16));
                                        if (needPipes.Add(lk)) queue.Enqueue(lk);
                                    }
                    }
                } catch (System.Text.Json.JsonException) { }
            }
            void AddModule(System.Text.Json.JsonElement st) {
                if (st.TryGetProperty("module", out var m) && m.GetString() is { } hex)
                    needModules.Add(Convert.ToUInt64(hex, 16));
            }

            // write: small objects (app info, layouts, render passes, ...) + needed modules + pipelines
            using var outFs = File.Create(outPath);
            using (var first = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                var head = new byte[16];
                first.ReadExactly(head);
                outFs.Write(head);
            }
            foreach (var (k, loc) in index) {
                bool keep = k.Item1 switch {
                    Foz.TagGraphics or Foz.TagCompute => needPipes.Contains(k),
                    4 => needModules.Contains(k.Item2),
                    9 => false, // ray tracing: not used by DXVK
                    _ => true,
                };
                if (!keep) continue;
                var fs = streams[loc.file];
                var raw = new byte[56 + loc.e.Size];
                fs.Seek(loc.e.Offset - 56, SeekOrigin.Begin);
                fs.ReadExactly(raw);
                outFs.Write(raw);
            }
            log($"Prepared {fresh.Count:N0} new pipelines (+{needPipes.Count - fresh.Count:N0} already-compiled parents, " +
                $"{needModules.Count:N0} shader modules).");
            return new Plan(outPath, fresh.Select(k => k.Item2).ToList(), pipelines.Count);
        } finally {
            foreach (var fs in streams.Values) fs.Dispose();
        }
    }

    static byte[] ReadPayload(FileStream fs, Foz.Entry e) {
        var data = new byte[e.Size];
        fs.Seek(e.Offset, SeekOrigin.Begin);
        fs.ReadExactly(data);
        if (e.Flags != 2) return data;
        using var z = new System.IO.Compression.ZLibStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    public static Result Run(string fossilize, List<string> files, Dictionary<string, string> env,
                             Action<int, int> progress, Action<string> log, CancellationToken ct) {
        log($"Counting recorded pipelines in {files.Count} database(s)...");
        int total = Foz.Hashes(files, Foz.TagGraphics).Count;
        int computeTotal = Foz.Hashes(files, Foz.TagCompute).Count;
        log($"{total:N0} graphics + {computeTotal:N0} compute pipelines recorded");
        int threads = Environment.ProcessorCount;
        int chunk = Math.Max(10000, total / 100);
        var sw = Stopwatch.StartNew();
        int replayed = 0, failedChunks = 0;
        var re = new Regex(@"graphics pipelines:\s+(\d+)");
        progress(0, total);
        for (int start = 0; start < total; start += chunk) {
            int end = Math.Min(start + chunk, total);
            bool last = end >= total;
            var args = new List<string> {
                "--num-threads", threads.ToString(),
                "--graphics-pipeline-range", start.ToString(), last ? int.MaxValue.ToString() : end.ToString(),
                "--compute-pipeline-range", "0", start == 0 ? computeTotal.ToString() : "0",
                "--raytracing-pipeline-range", "0", "0",
            };
            args.AddRange(files);
            using var p = Start(fossilize, args, env);
            var (code, output) = RunToEnd(p, ct);
            var m = re.Match(output);
            if (code == 0 && m.Success) {
                replayed += int.Parse(m.Groups[1].Value);
            } else {
                // A driver crash on one pipeline only loses this chunk.
                failedChunks++;
                log($"chunk {start:N0}-{end:N0} failed (exit {code}); skipped");
            }
            progress(end, total);
        }
        return new Result(replayed, total, failedChunks, sw.Elapsed);
    }

    public sealed record Coverage(int Libraries, int LibrariesHit, int Pipelines, int PipelinesHit);

    /// <summary>Asks the driver to *only* return pipelines it already has
    /// compiled (fail-on-compile-required) while replaying this PC's recent
    /// game sessions, and counts how many were found in the installed caches.</summary>
    public static Coverage Verify(string fossilize, List<string> recordings, Dictionary<string, string> env,
                                  Action<string> log, CancellationToken ct) {
        var libs = Foz.ShaderLibraries(recordings);
        HashSet<ulong> Missing(Dictionary<string, string> e) {
            var args = new List<string> { "--num-threads", Environment.ProcessorCount.ToString(),
                                          "--fail-on-pipeline-compile-required" };
            args.AddRange(recordings);
            using var p = Start(fossilize, args, e);
            var (_, output) = RunToEnd(p, ct);
            var set = new HashSet<ulong>();
            foreach (Match m in Regex.Matches(output, @"Recompile required for graphics pipeline hash 0x([0-9a-f]+)"))
                set.Add(Convert.ToUInt64(m.Groups[1].Value, 16));
            return set;
        }
        // Baseline with nothing installed: the pipelines fossilize can actually
        // ask the driver about (some recorded ones reference missing parents).
        var bare = new Dictionary<string, string>(env);
        if (bare.ContainsKey("__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME")) bare["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"] = "";
        if (bare.ContainsKey("MESA_SHADER_CACHE_DIR")) bare["MESA_SHADER_CACHE_DISABLE"] = "true";
        log("Checking which recorded pipelines can be tested...");
        var testable = Missing(bare);
        log("Checking them against the installed caches...");
        var missing = Missing(env);
        int pipelinesHit = testable.Count(h => !missing.Contains(h));
        var testableLibs = libs.Where(testable.Contains).ToList();
        int libsHit = testableLibs.Count(h => !missing.Contains(h));
        return new Coverage(testableLibs.Count, libsHit, testable.Count, pipelinesHit);
    }
}
