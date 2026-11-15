// Applies launch options to a COPY of a localconfig.vdf in a fake Steam root
// and prints before/after; also checks merge cases and turning off Steam's
// Shader Pre-Caching in the fake root's config/config.vdf (if present).
using OwShaderPrecache.Core;

int fails = 0;
void Check(string name, string got, string want) {
    bool ok = got == want;
    if (!ok) fails++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}\n     got:  {got}" + (ok ? "" : $"\n     want: {want}"));
}
void True(string name, bool ok) { if (!ok) fails++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); }

var root = args[0];
var g = new GameInstall(root, "/mnt/My Games/Steam$Lib", "/x");
var a = LaunchOptions.Assignments(g, CacheMode.NvidiaProprietary, 50);
Check("empty", LaunchOptions.Merge("", "X=1"), "X=1 %command%");
Check("args only", LaunchOptions.Merge("-novid -high", "X=1"), "X=1 %command% -novid -high");
Check("keeps other vars + wrapper", LaunchOptions.Merge("PROTON_LOG=1 gamemoderun %command% -foo", "X=1"), "X=1 PROTON_LOG=1 gamemoderun %command% -foo");
Check("replaces ours", LaunchOptions.Merge("__GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1 %command%", "X=1"), "X=1 %command%");
Check("replaces quoted ours", LaunchOptions.Merge("__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME=\"a;b\" FOO=2 %command%", "X=1"), "X=1 FOO=2 %command%");
Check("idempotent", LaunchOptions.Merge(LaunchOptions.Merge("__GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1 %command%", a), a), LaunchOptions.Merge("__GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1 %command%", a));
True("path quoted for the shell", a.Contains(
    "__GL_SHADER_DISK_CACHE_PATH=\"/mnt/My Games/Steam\\$Lib/steamapps/shadercache/2357570/nvidiav1\" "));
True("old options are out of date", !LaunchOptions.IsApplied(
    $"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME=\"{CacheSetup.GameReadOnlyList()}\" __GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1 %command%",
    g, CacheMode.NvidiaProprietary));
True("new options are applied", LaunchOptions.IsApplied(LaunchOptions.Merge("-novid", a), g, CacheMode.NvidiaProprietary));
True("mesa options are applied", LaunchOptions.IsApplied(
    LaunchOptions.Merge("", LaunchOptions.Assignments(g, CacheMode.Mesa, 10)), g, CacheMode.Mesa));

Console.WriteLine("\nbefore: " + LaunchOptions.Current(root));
foreach (var l in LaunchOptions.Apply(root, a, skipSteamCheck: true)) Console.WriteLine("applied: " + l);
Console.WriteLine("after:  " + LaunchOptions.Current(root));

var cfg = Path.Combine(root, "config", "config.vdf");
if (File.Exists(cfg)) {
    var before = File.ReadAllText(cfg);
    True("pre-caching turned off", LaunchOptions.DisableSteamShaderPreCaching(root, skipSteamCheck: true));
    var after = File.ReadAllText(cfg);
    True("DisableShaderCache=1", after.Contains("\"DisableShaderCache\"\t\t\"1\""));
    True("EnableShaderBackgroundProcessing=0", after.Contains("\"EnableShaderBackgroundProcessing\"\t\t\"0\""));
    True("only those lines changed", after.Split('\n').Length == before.Split('\n').Length
         || !before.Contains("DisableShaderCache"));
    True("second run is a no-op", !LaunchOptions.DisableSteamShaderPreCaching(root, skipSteamCheck: true));
}
return fails;
