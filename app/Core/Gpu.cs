using System.Text.RegularExpressions;

namespace OwShaderPrecache.Core;

public enum GpuVendor { Nvidia, Amd, Intel }

/// <summary>Which shader cache implementation the GPU driver uses.</summary>
public enum CacheMode {
    NvidiaProprietary, // NVIDIA's own driver: __GL_SHADER_DISK_CACHE_*
    Mesa,              // RADV (AMD), ANV (Intel), NVK (NVIDIA open source): MESA_SHADER_CACHE_*
}

public static class Gpu {
    /// <summary>GPU vendors present, discrete cards first.</summary>
    public static List<GpuVendor> Detect() {
        var found = new List<GpuVendor>();
        if (!Directory.Exists("/sys/class/drm")) return found;
        foreach (var card in Directory.GetDirectories("/sys/class/drm").OrderBy(x => x)) {
            if (!Regex.IsMatch(Path.GetFileName(card), @"^card\d+$")) continue;
            try {
                var v = File.ReadAllText(Path.Combine(card, "device", "vendor")).Trim().ToLowerInvariant();
                GpuVendor? g = v switch { "0x10de" => GpuVendor.Nvidia, "0x1002" => GpuVendor.Amd, "0x8086" => GpuVendor.Intel, _ => null };
                if (g != null && !found.Contains(g.Value)) found.Add(g.Value);
            } catch { }
        }
        return found.OrderBy(v => v == GpuVendor.Intel ? 1 : 0).ToList();
    }

    /// <summary>NVIDIA's proprietary kernel module is loaded.</summary>
    public static bool NvidiaProprietaryLoaded() => File.Exists("/sys/module/nvidia/version");

    public static string? NvidiaDriverVersion() {
        try { return File.ReadAllText("/sys/module/nvidia/version").Trim(); } catch { return null; }
    }

    public static CacheMode ModeFor(GpuVendor v) =>
        v == GpuVendor.Nvidia && NvidiaProprietaryLoaded() ? CacheMode.NvidiaProprietary : CacheMode.Mesa;

    /// <summary>A string that changes when the GPU driver changes (which
    /// invalidates every compiled shader).</summary>
    public static string DriverId(CacheMode mode) {
        try {
            if (mode == CacheMode.NvidiaProprietary)
                return "nvidia " + (NvidiaDriverVersion() ?? "?");
            var parts = new List<string>();
            foreach (var dir in new[] { "/usr/share/vulkan/icd.d", "/etc/vulkan/icd.d" }) {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x)) {
                    var m = Regex.Match(File.ReadAllText(f), "\"library_path\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success && File.Exists(m.Groups[1].Value)) {
                        var fi = new FileInfo(m.Groups[1].Value);
                        parts.Add($"{fi.Name}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}");
                    }
                }
            }
            return "mesa " + string.Join(",", parts);
        } catch {
            return "unknown";
        }
    }

    public static string Label(GpuVendor v) => v switch {
        GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", _ => "Intel"
    };

    public static string Label(CacheMode m) => m switch {
        CacheMode.NvidiaProprietary => "NVIDIA proprietary driver" + (NvidiaDriverVersion() is { } v ? $" {v}" : ""),
        _ => "Mesa driver (RADV / ANV / NVK)"
    };
}
