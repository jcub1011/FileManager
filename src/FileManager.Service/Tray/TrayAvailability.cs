using System.Runtime.InteropServices;

namespace FileManager.Service.Tray;

/// <summary>
/// Best-effort detection of whether a system tray exists for the current session (spec §1.1 / milestone
/// Risk: "GNOME without an extension has none"). The detection degrades silently: when it cannot be sure
/// a tray exists, it returns <c>false</c> so the service never assumes one. Pure and testable — the
/// environment inputs are injectable.
/// </summary>
public static class TrayAvailability
{
    /// <summary>
    /// Whether a tray is available in the current process environment. Windows interactive sessions
    /// effectively always have a tray; Linux is a heuristic over desktop-environment env vars
    /// (<c>XDG_CURRENT_DESKTOP</c> / a StatusNotifier hint) and defaults to false when unknown.
    /// </summary>
    public static bool IsAvailable() => IsAvailable(
        isWindows: RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
        isInteractive: Environment.UserInteractive,
        xdgCurrentDesktop: Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
        display: Environment.GetEnvironmentVariable("DISPLAY"),
        waylandDisplay: Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>
    /// Pure resolver from explicit inputs — the testable core of <see cref="IsAvailable()"/>.
    /// </summary>
    /// <param name="isWindows">Whether the platform is Windows.</param>
    /// <param name="isInteractive">Whether the process runs in an interactive session.</param>
    /// <param name="xdgCurrentDesktop">Value of <c>$XDG_CURRENT_DESKTOP</c> (Linux).</param>
    /// <param name="display">Value of <c>$DISPLAY</c> (X11).</param>
    /// <param name="waylandDisplay">Value of <c>$WAYLAND_DISPLAY</c> (Wayland).</param>
    public static bool IsAvailable(
        bool isWindows,
        bool isInteractive,
        string? xdgCurrentDesktop,
        string? display,
        string? waylandDisplay)
    {
        if (isWindows)
            return isInteractive; // a Windows interactive logon session has a tray.

        // Linux: need a graphical session at all (X11 or Wayland) AND a desktop environment.
        bool hasGraphicalSession =
            !string.IsNullOrEmpty(display) || !string.IsNullOrEmpty(waylandDisplay);
        if (!hasGraphicalSession)
            return false;

        // A known desktop environment usually provides (or can provide) a StatusNotifier tray. We only
        // claim availability when a desktop is named; unknown ⇒ false (degrade silently, §1.1).
        return !string.IsNullOrWhiteSpace(xdgCurrentDesktop);
    }
}
