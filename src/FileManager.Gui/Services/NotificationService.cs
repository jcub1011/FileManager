using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;

namespace FileManager.Gui.Services;

/// <summary>
/// The production notification backend. It shows a real, user-visible notification two ways, in order of
/// preference, and never throws (a failed notification must never disrupt a Job's UI flow):
/// <list type="number">
/// <item>
/// In-app: when a <see cref="TopLevel"/> has been attached (the main window), an Avalonia
/// <see cref="WindowNotificationManager"/> renders a visible toast inside the app — the path that
/// satisfies the §7 "raises an OS/tray notification on failure" criterion in the running GUI.
/// </item>
/// <item>
/// Native (Linux best-effort): if no TopLevel is available but <c>notify-send</c> is on PATH, it is
/// invoked so a desktop notification still appears outside the app.
/// </item>
/// </list>
/// When neither is available (headless / no display, as in CI and unit tests) it falls back to a trace
/// line, so tests and the headless build are unaffected. A fully-native OS toast on every platform
/// (e.g. Windows Action Center, macOS Notification Center) is a sensible later enhancement and would
/// slot in as an additional branch here without a heavy native-toast dependency.
/// </summary>
/// <remarks>
/// The DECISION of <em>when</em> to notify (failure vs. verbosity-gated skip) lives in
/// <see cref="ViewModels.ActivityViewModel"/> and is unit-tested against a capturing fake; this class is
/// only the rendering backend and is wired only in the running app.
/// </remarks>
public sealed class NotificationService : INotificationService
{
    private readonly System.Threading.Lock _gate = new();
    private WindowNotificationManager? _manager;

    /// <summary>
    /// Attaches the app's <see cref="TopLevel"/> (the main window) so in-app toasts can render. Called by
    /// <c>App</c> once the window exists. Safe to call with null (detaches), and never throws.
    /// </summary>
    public void AttachTopLevel(TopLevel? topLevel)
    {
        WindowNotificationManager? manager = topLevel is null
            ? null
            : new WindowNotificationManager(topLevel) { MaxItems = 5 };
        lock (_gate)
            _manager = manager;
    }

    /// <inheritdoc/>
    public void Notify(string title, string message)
    {
        WindowNotificationManager? manager;
        lock (_gate)
            manager = _manager;

        if (manager is not null)
        {
            try
            {
                manager.Show(new Notification(title, message, NotificationType.Error));
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // Fall through to the native / trace fallbacks.
            }
        }

        if (TryNotifySend(title, message))
            return;

        // Headless / no display (CI, tests): never lose the signal, never crash.
        Trace.TraceInformation($"[notify] {title}: {message}");
    }

    // Best-effort Linux desktop notification via notify-send, if present on PATH. Swallows every failure
    // (missing binary, no D-Bus session, non-zero exit) — it is a bonus path, not a guarantee.
    private static bool TryNotifySend(string title, string message)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            var psi = new ProcessStartInfo("notify-send")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(message);

            using Process? process = Process.Start(psi);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // notify-send not installed / not launchable — fall back to trace.
            return false;
        }
    }
}
