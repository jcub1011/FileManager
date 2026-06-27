using Avalonia;

namespace FileManager.Gui;

/// <summary>
/// The desktop entry point. Builds the Avalonia application and starts the classic desktop lifetime.
/// Kept minimal and reflection-free so the headless build (CI, no display server) succeeds; the app only
/// opens a window when actually run with a display.
/// </summary>
public static class Program
{
    /// <summary>Process entry point.</summary>
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>The Avalonia app builder (also used by the visual designer).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
