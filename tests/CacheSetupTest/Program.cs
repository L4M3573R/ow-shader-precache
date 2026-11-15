// Exercises CacheSetup.Finalize / RecoverLeftovers on a fake NVIDIA cache layout.
using OwShaderPrecache.Core;

var lib = Path.Combine(Path.GetTempPath(), "cachesetup-test-" + Environment.ProcessId);
var g = new GameInstall("/nonexistent-steam", lib, Path.Combine(lib, "steamapps/common/Overwatch"));
var dir = Path.Combine(g.NvidiaCacheDir, "GLCache", "drv", "gpu");
Directory.CreateDirectory(dir);
void Make(string name, int size) { File.WriteAllBytes(Path.Combine(dir, name + ".bin"), new byte[size]); File.WriteAllBytes(Path.Combine(dir, name + ".toc"), new byte[64]); }
int fails = 0;
void Check(string what, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) fails++; }

// 1. single fragment (native fossilize: index 0 only)
Make("shader_precache_r1_work0", 10000);
try {
    var fin = CacheSetup.Finalize(g, "shader_precache_r1_work", "shader_precache_r1");
    Check($"single fragment finalized ({fin?.size} bytes)", fin?.size == 10000 && File.Exists(Path.Combine(dir, "shader_precache_r1.bin")));
} catch (Exception e) { Check("single fragment: " + e.Message, false); }

// 2. proton layout: helpers take 0/1, ours is 2
Make("shader_precache_g1_work0", 32); Make("shader_precache_g1_work1", 32); Make("shader_precache_g1_work2", 50000);
try {
    var fin = CacheSetup.Finalize(g, "shader_precache_g1_work", "shader_precache_g1");
    Check($"proton layout finalized ({fin?.size} bytes), no work files left",
          fin?.size == 50000 && !Directory.GetFiles(dir, "*work*").Any());
} catch (Exception e) { Check("proton layout: " + e.Message, false); }

// 3. leftovers recovery
Make("shader_precache_g2_work2", 9000);
try {
    var mb = CacheSetup.RecoverLeftovers(g);
    Check("leftover recovered into g2", File.Exists(Path.Combine(dir, "shader_precache_g2.bin")) && !Directory.GetFiles(dir, "*work*").Any());
} catch (Exception e) { Check("recover: " + e.Message, false); }
Directory.Delete(lib, true);
return fails;
