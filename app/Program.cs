using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;

namespace OwShaderPrecache;

public static class Program {
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

public sealed class App : Application {
    public override void Initialize() {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default; // follow the desktop's light/dark setting
    }

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
