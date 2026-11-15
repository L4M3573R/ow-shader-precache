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

    public static Result Run(string fossilize, FozSet dbs, Dictionary<string, string> env,
                             Action<int, int> progress, Action<string> log, CancellationToken ct) {
        var files = dbs.All.ToList();
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
