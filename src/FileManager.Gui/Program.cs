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
    /// <remarks>
    /// One GUI per user (M8 §3.2): a second instance — e.g. another manual right-click that cold-starts
    /// the GUI — detects the running instance via <see cref="GuiSingleInstanceGuard"/> and exits cleanly,
    /// so exactly one chooser subscriber exists. The first instance, with the service's replay-on-subscribe,
    /// still receives the pending. The guard is held for the process lifetime (disposed at exit).
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        using GuiSingleInstanceGuard guard = GuiSingleInstanceGuard.Acquire();
        if (!guard.IsPrimaryInstance)
        {
            Console.Error.WriteLine("FileManager GUI is already running for this user.");
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The Avalonia app builder (also used by the visual designer).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
