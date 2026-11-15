using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OwShaderPrecache.Core;

namespace OwShaderPrecache;

public sealed class MainWindow : Window {
    // --- controls ---
    readonly TextBox gameDirBox = new() { Watermark = "…/steamapps/common/Overwatch" };
    readonly TextBlock gameInfo = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    readonly RadioButton nvidiaRadio = new() { Content = "NVIDIA", GroupName = "gpu" };
    readonly RadioButton amdRadio = new() { Content = "AMD", GroupName = "gpu" };
    readonly RadioButton intelRadio = new() { Content = "Intel", GroupName = "gpu" };
    readonly TextBlock driverInfo = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    readonly ComboBox protonBox = new() { MinWidth = 260 };
    readonly TextBlock protonInfo = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    readonly NumericUpDown cacheSize = new() { Minimum = 5, Maximum = 1000, Value = 50, Increment = 5, FormatString = "0", Width = 140 };
    readonly CheckBox replayBox = new() { IsChecked = true, Content = "Also compile Steam's recorded pipelines (your play sessions + Valve's community database).\nAnything already installed is skipped." };
    readonly CheckBox keepBox = new() { IsChecked = false, Content = "Keep the extracted shader files afterwards (~2 GB)" };
    readonly CheckBox forceBox = new() { IsChecked = false, Content = "Recompile everything even if the cache is up to date" };
    readonly Button compileButton = new() { Content = "Precompile shaders" };
    readonly Button cancelButton = new() { Content = "Cancel", IsEnabled = false };
    readonly Button launchButton = new() { Content = "Write Steam launch options" };
    readonly Button verifyButton = new() { Content = "Check against my play sessions" };
    readonly Button logButton = new() { Content = "Open log folder" };
    readonly TextBlock stepText = new() { FontWeight = FontWeight.SemiBold };
    readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1000, Height = 10 };
    readonly TextBox logBox = new() {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 230,
        FontFamily = new FontFamily("monospace"), FontSize = 12,
    };
    readonly TextBlock statusText = new() { TextWrapping = TextWrapping.Wrap };

    GameInstall? game;
    AppState state = AppState.Load();
    CancellationTokenSource? cts;
    readonly string logPath = Path.Combine(AppState.CacheDir, "last-run.log");

    public MainWindow() {
        Title = "Overwatch Shader Precompiler";
        Width = 820;
        Height = 900;
        MinWidth = 600;

        compileButton.Classes.Add("accent");
        foreach (var b in new[] { compileButton, cancelButton, launchButton, verifyButton, logButton })
            b.Padding = new Thickness(14, 7);

        var browse = new Button { Content = "Browse…" };
        var detect = new Button { Content = "Find automatically" };
        browse.Click += async (_, _) => await BrowseAsync();
        detect.Click += (_, _) => AutoDetect(true);
        gameDirBox.LostFocus += (_, _) => UseGameDir(gameDirBox.Text ?? "");
        foreach (var r in new[] { nvidiaRadio, amdRadio, intelRadio })
            r.IsCheckedChanged += (_, _) => UpdateDriverInfo();
        protonBox.SelectionChanged += (_, _) => UpdateProtonInfo();
        compileButton.Click += async (_, _) => await RunAsync(PrecompileAsync);
        verifyButton.Click += async (_, _) => await RunAsync(VerifyAsync);
        launchButton.Click += async (_, _) => await RunAsync(WriteLaunchOptionsAsync);
        cancelButton.Click += (_, _) => { cts?.Cancel(); Log("Cancelling…"); };
        logButton.Click += (_, _) => {
            Directory.CreateDirectory(AppState.CacheDir);
            try { Process.Start(new ProcessStartInfo("xdg-open", AppState.CacheDir) { UseShellExecute = false }); } catch { }
        };

        var gameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        gameRow.Children.Add(gameDirBox);
        gameRow.Children.Add(browse); Grid.SetColumn(browse, 1);
        gameRow.Children.Add(detect); Grid.SetColumn(detect, 2);

        var cacheRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        cacheRow.Children.Add(cacheSize);
        cacheRow.Children.Add(new TextBlock {
            Text = "GB maximum shader cache size (a limit only; nothing is reserved)",
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8, TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { compileButton, cancelButton, launchButton, verifyButton, logButton }) {
            b.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(b);
        }

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        root.Children.Add(new TextBlock { Text = "Overwatch Shader Precompiler", FontSize = 22, FontWeight = FontWeight.Bold });
        root.Children.Add(new TextBlock {
            Text = "Compiles every shader in the game for your graphics card before you play, so you don't get " +
                   "stutter the first time a skin, map or effect appears. Run it again after each Overwatch patch " +
                   "or graphics driver update.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
        });
        root.Children.Add(Card("1. Overwatch folder", gameRow, gameInfo));
        root.Children.Add(Card("2. Graphics card", Row(nvidiaRadio, amdRadio, intelRadio), driverInfo));
        root.Children.Add(Card("3. Proton", protonBox, protonInfo));
        root.Children.Add(Card("4. Options", cacheRow, replayBox, keepBox, forceBox));
        root.Children.Add(buttons);
        root.Children.Add(stepText);
        root.Children.Add(progress);
        root.Children.Add(logBox);
        root.Children.Add(statusText);
        Content = new ScrollViewer { Content = root };

        // GPU defaults from what's detected
        var vendors = Gpu.Detect();
        (vendors.FirstOrDefault() switch {
            GpuVendor.Amd => amdRadio, GpuVendor.Intel => intelRadio, _ => nvidiaRadio,
        }).IsChecked = true;
        if (vendors.Count > 1) Log($"Several GPUs found: {string.Join(", ", vendors.Select(Gpu.Label))}. Pick the one the game runs on.");

        Opened += async (_, _) => {
            if (state.GameDir != null && Directory.Exists(state.GameDir)) UseGameDir(state.GameDir);
            else AutoDetect(false);
            // --auto: start precompiling right away (unattended runs / troubleshooting);
            // --no-replay: skip the Steam-recordings step; --exit: close when finished.
            var args = Environment.GetCommandLineArgs();
            if (args.Contains("--no-replay")) replayBox.IsChecked = false;
            if (args.Contains("--auto")) {
                await RunAsync(PrecompileAsync);
                if (args.Contains("--exit")) Close();
            }
        };
    }

    // ------------------------------------------------------------------ UI helpers

    static Control Card(string title, params Control[] children) {
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        foreach (var c in children) sp.Children.Add(c);
        return new Border {
            Child = sp, Padding = new Thickness(14), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
        };
    }

    static Control Row(params Control[] children) {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    void Log(string msg) {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        try {
            Directory.CreateDirectory(AppState.CacheDir);
            File.AppendAllText(logPath, line + Environment.NewLine);
        } catch { }
        Dispatcher.UIThread.Post(() => {
            logBox.Text = (logBox.Text ?? "") + line + "\n";
            logBox.CaretIndex = logBox.Text.Length;
        });
    }

    void Step(string text, double? fraction = null) => Dispatcher.UIThread.Post(() => {
        stepText.Text = text;
        progress.IsIndeterminate = fraction == null;
        if (fraction != null) progress.Value = Math.Clamp(fraction.Value, 0, 1) * 1000;
    });

    GpuVendor Vendor => amdRadio.IsChecked == true ? GpuVendor.Amd : intelRadio.IsChecked == true ? GpuVendor.Intel : GpuVendor.Nvidia;
    CacheMode Mode => Gpu.ModeFor(Vendor);
    int CacheGiB => (int)(cacheSize.Value ?? 50);

    void UpdateDriverInfo() {
        var mode = Mode;
        var text = $"Shader cache type: {Gpu.Label(mode)}.";
        if (Vendor == GpuVendor.Nvidia && mode == CacheMode.Mesa)
            text += " NVIDIA's own driver isn't loaded, so the open-source NVK driver is assumed.";
        if (Vendor != GpuVendor.Nvidia && Gpu.NvidiaProprietaryLoaded())
            text += " Note: an NVIDIA driver is loaded on this PC - pick NVIDIA if the game runs on that card.";
        driverInfo.Text = text;
        UpdateStatus();
    }

    void UpdateProtonInfo() {
        if (game == null) return;
        var sel = protonBox.SelectedItem as Steam.ProtonInfo;
        var gp = Steam.GameProton(game, Steam.Protons(game));
        protonInfo.Text = gp == null
            ? "Couldn't tell which Proton Overwatch uses. Launch the game once from Steam, or pick the Proton you set for it."
            : sel?.Dir == gp.Dir
                ? "This is the Proton Overwatch last ran with. It must match, so the compiled shaders are identical to the game's."
                : $"Warning: Overwatch last ran with {gp.Name}. Shaders compiled with a different Proton may not match.";
    }

    void UpdateStatus() {
        if (game == null) { statusText.Text = ""; return; }
        var build = Steam.GameBuild(game.GameDir);
        var drv = Gpu.DriverId(Mode);
        string s;
        if (state.CompiledAt == null) s = "Not compiled yet.";
        else if (state.GameBuild != build) s = $"Overwatch was updated (build {state.GameBuild} → {build}). Compile again.";
        else if (state.DriverId != drv || state.Mode != Mode.ToString()) s = "The graphics driver changed since the last compile. Compile again.";
        else s = $"Up to date: {state.ShadersCompiled:N0} shaders compiled on {state.CompiledAt:g} for build {build}.";
        var lo = LaunchOptions.Current(game.SteamRoot);
        bool applied = lo != null && (Mode == CacheMode.NvidiaProprietary
            ? lo.Contains(CacheSetup.GameReadOnlyList()) : lo.Contains("MESA_SHADER_CACHE_MAX_SIZE"));
        s += applied ? "  Steam launch options: set." : "  Steam launch options: NOT set yet (needed so the game uses the cache).";
        statusText.Text = s;
    }

    // ------------------------------------------------------------------ game folder

    void AutoDetect(bool verbose) {
        var g = Steam.Find();
        if (g == null) {
            gameInfo.Text = "Overwatch (Steam version) wasn't found automatically. Use Browse… to pick its folder.";
            if (verbose) Log("No Overwatch install found in any Steam library.");
            return;
        }
        UseGameDir(g.GameDir);
    }

    async Task BrowseAsync() {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Select the Overwatch folder (…/steamapps/common/Overwatch)", AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) UseGameDir(path);
    }

    void UseGameDir(string dir) {
        if (string.IsNullOrWhiteSpace(dir)) return;
        var g = Steam.FromGameDir(dir, out var err);
        if (g == null) {
            gameInfo.Text = err;
            game = null;
            return;
        }
        game = g;
        gameDirBox.Text = g.GameDir;
        state.GameDir = g.GameDir;
        var protons = Steam.Protons(g);
        protonBox.ItemsSource = protons;
        protonBox.SelectedItem = Steam.GameProton(g, protons) ?? protons.FirstOrDefault();
        gameInfo.Text = $"Build {Steam.GameBuild(g.GameDir) ?? "?"} · shader cache: {g.ShaderCacheDir}";
        UpdateProtonInfo();
        UpdateDriverInfo();
    }

    // ------------------------------------------------------------------ actions

    async Task RunAsync(Func<GameInstall, CancellationToken, Task> action) {
        if (game == null) { Log("Pick the Overwatch folder first."); return; }
        cts = new CancellationTokenSource();
        SetBusy(true);
        try {
            await action(game, cts.Token);
        } catch (OperationCanceledException) {
            Log("Cancelled.");
            Step("Cancelled", 0);
        } catch (Exception e) {
            Log("ERROR: " + e.Message);
            Step("Failed - see the log", 0);
        } finally {
            SetBusy(false);
            state.Save();
            UpdateStatus();
        }
    }

    void SetBusy(bool busy) {
        compileButton.IsEnabled = launchButton.IsEnabled = verifyButton.IsEnabled = !busy;
        cancelButton.IsEnabled = busy;
        gameDirBox.IsEnabled = protonBox.IsEnabled = cacheSize.IsEnabled = !busy;
        nvidiaRadio.IsEnabled = amdRadio.IsEnabled = intelRadio.IsEnabled = !busy;
        if (!busy) progress.IsIndeterminate = false;
    }

    async Task PrecompileAsync(GameInstall g, CancellationToken ct) {
        var mode = Mode;
        var gib = CacheGiB;
        var proton = protonBox.SelectedItem as Steam.ProtonInfo
                     ?? throw new InvalidOperationException("No Proton found. Install one in Steam (e.g. Proton Experimental).");
        bool replay = replayBox.IsChecked == true, keep = keepBox.IsChecked == true, force = forceBox.IsChecked == true;
        File.WriteAllText(logPath, "");
        Log($"Overwatch: {g.GameDir}");
        Log($"GPU cache: {Gpu.Label(mode)} · Proton: {proton.Name} · limit {gib} GB");

        await Task.Run(() => {
            if (Steam.GameRunning()) throw new InvalidOperationException("Overwatch is running. Close it first.");
            var build = Steam.GameBuild(g.GameDir);
            var drv = Gpu.DriverId(mode);
            bool upToDate = !force && state.GameBuild == build && state.DriverId == drv && state.Mode == mode.ToString() &&
                            (mode != CacheMode.NvidiaProprietary || CacheSetup.Exists(g, CacheSetup.ExtractName));
            int steps = replay ? 3 : 2;

            if (upToDate) {
                Log("Game shaders are already compiled for this game build and driver - skipping.");
            } else {
                var shaderDir = Path.Combine(AppState.CacheDir, "shaders");
                var need = 6L * 1024 * 1024 * 1024;
                var free = new DriveInfo(g.ShaderCacheDir.Length > 0 ? Path.GetPathRoot(g.ShaderCacheDir)! : "/").AvailableFreeSpace;
                if (free < need) Log($"Warning: only {free / 1073741824.0:F1} GB free near the shader cache.");

                Step($"Step 1/{steps}: reading shaders from the game files…");
                Log("Extracting shaders from the game files…");
                var ex = ShaderExtractor.Extract(g.GameDir, shaderDir,
                    (d, t) => Step($"Step 1/{steps}: extracting shaders ({d:N0} / {t:N0})", t == 0 ? 0 : (double)d / t), ct);
                Log($"Extracted {ex.Extracted:N0} shaders ({ex.Bytes / 1048576.0:N0} MB) from {ex.Version}; {ex.Failed} failed.");

                Step($"Step 2/{steps}: compiling (first run sets up a Wine prefix)…");
                Log($"Compiling through DXVK with {proton.Name}. This takes a while; the PC will be busy.");
                var helper = ShaderCompiler.ExtractHelper(Path.Combine(AppState.CacheDir, "bin"));
                var work = CacheSetup.ExtractName + CacheSetup.WorkSuffix;
                if (mode == CacheMode.NvidiaProprietary) CacheSetup.DeleteWork(g, work);
                var env = CacheSetup.Env(g, mode, gib, work, Array.Empty<string>());
                var started = DateTime.Now;
                var res = ShaderCompiler.Run(g, proton, helper, Path.Combine(AppState.CacheDir, "prefix"), shaderDir, env,
                    (d, t) => {
                        var frac = t == 0 ? 0 : (double)d / t;
                        var el = DateTime.Now - started;
                        var eta = frac > 0.02 ? TimeSpan.FromSeconds(el.TotalSeconds / frac - el.TotalSeconds) : (TimeSpan?)null;
                        Step($"Step 2/{steps}: compiling shaders ({d:N0} / {t:N0})" +
                             (eta != null ? $" · about {Fmt(eta.Value)} left" : ""), frac);
                    }, Log, ct);
                Log($"Compiled {res.Compiled:N0} shaders in {Fmt(res.Elapsed)} ({res.Failed} failed).");

                if (mode == CacheMode.NvidiaProprietary) {
                    var fin = CacheSetup.Finalize(g, work, CacheSetup.ExtractName)
                              ?? throw new InvalidOperationException("The driver didn't write a shader cache. Is the NVIDIA driver selected correctly?");
                    Log($"Installed {fin.size / 1048576.0:N0} MB cache: {fin.path}");
                }
                state.GameBuild = build;
                state.DriverId = drv;
                state.Mode = mode.ToString();
                state.CompiledAt = DateTime.Now;
                state.ShadersCompiled = res.Compiled;
                state.Save();
                if (!keep) {
                    try { Directory.Delete(shaderDir, true); } catch { }
                }
            }

            if (replay) ReplayStep(g, mode, gib, steps, ct);

            Step("Done", 1);
            Log("Done.");
            var lo = LaunchOptions.Current(g.SteamRoot) ?? "";
            if (!lo.Contains(mode == CacheMode.NvidiaProprietary ? CacheSetup.GameReadOnlyList() : "MESA_SHADER_CACHE_MAX_SIZE"))
                Log("NEXT: exit Steam completely, then click \"Write Steam launch options\" so the game uses the cache.");
        }, ct);
    }

    void ReplayStep(GameInstall g, CacheMode mode, int gib, int steps, CancellationToken ct) {
        var fossilize = Steam.Fossilize(g);
        var dbs = FozSet.ForDxvk(g);
        if (fossilize == null || !dbs.All.Any()) {
            Log("No Steam pipeline recordings for Overwatch yet (turn on Steam > Settings > Downloads > " +
                "Shader Pre-Caching and play once). Skipping that step.");
            return;
        }
        Step($"Step {steps}/{steps}: compiling Steam's recorded pipelines…");
        Log($"Replaying Steam's recordings: {dbs.Local.Count} from your sessions, {dbs.Community.Count} community database(s).");
        Dictionary<string, string> env;
        string? slot = null, work = null;
        if (mode == CacheMode.NvidiaProprietary) {
            var used = CacheSetup.ReplayNames().Where(n => CacheSetup.Exists(g, n)).ToList();
            slot = CacheSetup.ReplayNames().FirstOrDefault(n => !used.Contains(n));
            if (slot == null) {
                // all slots used: consolidate into one fresh cache
                foreach (var n in used) CacheSetup.Delete(g, n);
                used.Clear();
                slot = CacheSetup.ReplayNames().First();
                Log("Consolidating earlier replay caches into one.");
            }
            work = slot + CacheSetup.WorkSuffix;
            CacheSetup.DeleteWork(g, work);
            var installed = new[] { "steam_shader_cache", "steamapp_merged_shader_cache", CacheSetup.ExtractName }.Concat(used);
            env = CacheSetup.Env(g, mode, gib, work, installed);
        } else {
            env = CacheSetup.Env(g, mode, gib, "", Array.Empty<string>());
        }
        var started = DateTime.Now;
        var r = Replayer.Run(fossilize, dbs, env, (d, t) => {
            var frac = t == 0 ? 1 : (double)d / t;
            var el = DateTime.Now - started;
            var eta = frac > 0.02 && frac < 1 ? TimeSpan.FromSeconds(el.TotalSeconds / frac - el.TotalSeconds) : (TimeSpan?)null;
            Step($"Step {steps}/{steps}: recorded pipelines ({d:N0} / {t:N0})" + (eta != null ? $" · about {Fmt(eta.Value)} left" : ""), frac);
        }, Log, ct);
        Log($"Checked {r.Total:N0} recorded pipelines in {Fmt(r.Elapsed)}; already-installed ones were skipped." +
            (r.FailedChunks > 0 ? $" {r.FailedChunks} chunk(s) failed." : ""));
        if (slot != null && work != null) {
            var fin = CacheSetup.Finalize(g, work, slot);
            Log(fin == null || fin.Value.size < 4096
                ? "Nothing new needed compiling - everything was already installed."
                : $"Installed {fin.Value.size / 1048576.0:N0} MB of new pipelines: {fin.Value.path}");
            if (fin is { size: < 4096 }) CacheSetup.Delete(g, slot);
        }
        state.ReplayedAt = DateTime.Now;
    }

    async Task VerifyAsync(GameInstall g, CancellationToken ct) {
        var mode = Mode;
        var gib = CacheGiB;
        await Task.Run(() => {
            var fossilize = Steam.Fossilize(g) ?? throw new InvalidOperationException("Steam's fossilize_replay wasn't found.");
            var rec = FozSet.ForDxvk(g).RecentLocal(2);
            if (rec.Count == 0)
                throw new InvalidOperationException("No recordings of your own play sessions yet. Play a match with Steam's " +
                                                    "Shader Pre-Caching on, then check again.");
            Step("Checking the cache against your recent play sessions…");
            Log($"Testing against {rec.Count} recent session recording(s)…");
            const string probe = "shader_precache_verify";
            var env = CacheSetup.Env(g, mode, gib, probe, CacheSetup.GameReadOnlyList().Split(';'));
            try {
                var c = Replayer.Verify(fossilize, rec, env, Log, ct);
                double pl = c.Libraries == 0 ? 0 : 100.0 * c.LibrariesHit / c.Libraries;
                double pa = c.Pipelines == 0 ? 0 : 100.0 * c.PipelinesHit / c.Pipelines;
                Log($"Per-shader compiles from your sessions found in the cache: {c.LibrariesHit:N0} of {c.Libraries:N0} ({pl:F1}%)");
                Log($"All pipelines from your sessions (incl. linked/optimized): {c.PipelinesHit:N0} of {c.Pipelines:N0} ({pa:F1}%)");
                Step($"Cache covers {pl:F1}% of the shaders your recent sessions compiled", 1);
            } finally {
                if (mode == CacheMode.NvidiaProprietary) CacheSetup.DeleteWork(g, probe);
            }
        }, ct);
    }

    async Task WriteLaunchOptionsAsync(GameInstall g, CancellationToken ct) {
        var assignments = LaunchOptions.Assignments(Mode, CacheGiB);
        await Task.Run(() => {
            foreach (var line in LaunchOptions.Apply(g.SteamRoot, assignments))
                Log("Launch options written: " + line);
            Log("A backup of the Steam config was saved next to it (.bak-…). You can start Steam again.");
            Step("Launch options written", 1);
        }, ct);
    }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
                                   : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s" : $"{t.Seconds}s";
}
