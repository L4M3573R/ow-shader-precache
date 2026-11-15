// Applies launch options to a COPY of a localconfig.vdf in a fake Steam root
// and prints before/after; also checks merge cases.
using OwShaderPrecache.Core;

int fails = 0;
void Check(string name, string got, string want) {
    bool ok = got == want;
    if (!ok) fails++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}\n     got:  {got}" + (ok ? "" : $"\n     want: {want}"));
}
var a = LaunchOptions.Assignments(CacheMode.NvidiaProprietary, 50);
Check("empty", LaunchOptions.Merge("", "X=1"), "X=1 %command%");
Check("args only", LaunchOptions.Merge("-novid -high", "X=1"), "X=1 %command% -novid -high");
Check("keeps other vars + wrapper", LaunchOptions.Merge("PROTON_LOG=1 gamemoderun %command% -foo", "X=1"), "X=1 PROTON_LOG=1 gamemoderun %command% -foo");
Check("replaces ours", LaunchOptions.Merge("__GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1 %command%", "X=1"), "X=1 %command%");
Check("replaces quoted ours", LaunchOptions.Merge("__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME=\"a;b\" FOO=2 %command%", "X=1"), "X=1 FOO=2 %command%");
Check("idempotent", LaunchOptions.Merge(LaunchOptions.Merge("__GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1 %command%", a), a), LaunchOptions.Merge("__GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1 %command%", a));

var root = args[0];
Console.WriteLine("\nbefore: " + LaunchOptions.Current(root));
foreach (var l in LaunchOptions.Apply(root, a, skipSteamCheck: true)) Console.WriteLine("applied: " + l);
Console.WriteLine("after:  " + LaunchOptions.Current(root));
return fails;
