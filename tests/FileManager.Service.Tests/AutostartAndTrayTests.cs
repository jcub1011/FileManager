using FileManager.Service.Autostart;
using FileManager.Service.Tray;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>
/// Asserts the pure autostart generators (systemd unit content, Windows task command) and the tray
/// abstraction (no-op indicator, availability detection) — no real registration or native UI.
/// </summary>
public sealed class AutostartAndTrayTests
{
    // ---- Linux systemd user unit ----

    [Fact]
    public void SystemdUnit_Content_ContainsExecStartAndInstallSection()
    {
        string content = LinuxSystemdUserUnit.BuildUnitContent("/opt/fm/FileManager.Service");

        Assert.Contains("[Unit]", content);
        Assert.Contains("[Service]", content);
        Assert.Contains("ExecStart=/opt/fm/FileManager.Service", content);
        Assert.Contains("Restart=on-failure", content);
        Assert.Contains("[Install]", content);
        Assert.Contains("WantedBy=default.target", content);
    }

    [Fact]
    public void SystemdUnit_EmptyExecStart_Throws() =>
        Assert.Throws<ArgumentException>(() => LinuxSystemdUserUnit.BuildUnitContent("  "));

    [Fact]
    public void SystemdUnit_PathResolvesUnderUserConfig()
    {
        string path = LinuxSystemdUserUnit.ResolveUnitPath("/home/alice");
        Assert.Equal(
            Path.Combine("/home/alice", ".config", "systemd", "user", "filemanager.service"),
            path);
    }

    // ---- Windows logon task ----

    [Fact]
    public void WindowsTask_CreateArguments_AreOnLogonPerUserNonAdmin()
    {
        IReadOnlyList<string> args = WindowsLogonTask.BuildCreateArguments(@"C:\fm\FileManager.Service.exe");

        Assert.Contains("/Create", args);
        Assert.Contains("/SC", args);
        Assert.Contains("ONLOGON", args);
        Assert.Contains("/RL", args);
        Assert.Contains("LIMITED", args); // non-admin
        Assert.Contains("/TN", args);
        Assert.Contains(WindowsLogonTask.TaskName, args);
        Assert.Contains("\"C:\\fm\\FileManager.Service.exe\"", args); // quoted run target
    }

    [Fact]
    public void WindowsTask_DeleteArguments_TargetTheTask()
    {
        IReadOnlyList<string> args = WindowsLogonTask.BuildDeleteArguments();
        Assert.Contains("/Delete", args);
        Assert.Contains("/TN", args);
        Assert.Contains(WindowsLogonTask.TaskName, args);
        Assert.Contains("/F", args);
    }

    [Fact]
    public void WindowsTask_EmptyExecutable_Throws() =>
        Assert.Throws<ArgumentException>(() => WindowsLogonTask.BuildCreateArguments(""));

    // ---- Tray ----

    [Fact]
    public void NullTrayIndicator_IsNoOp()
    {
        ITrayIndicator tray = NullTrayIndicator.Instance;
        // None of these throw or do anything observable — the headless guarantee.
        tray.Show();
        tray.SetStatus("running");
        tray.Dispose();
    }

    [Fact]
    public void TrayAvailability_Windows_InteractiveSession_True() =>
        Assert.True(TrayAvailability.IsAvailable(
            isWindows: true, isInteractive: true, xdgCurrentDesktop: null, display: null, waylandDisplay: null));

    [Fact]
    public void TrayAvailability_Windows_NonInteractive_False() =>
        Assert.False(TrayAvailability.IsAvailable(
            isWindows: true, isInteractive: false, xdgCurrentDesktop: null, display: null, waylandDisplay: null));

    [Fact]
    public void TrayAvailability_Linux_NoGraphicalSession_False() =>
        Assert.False(TrayAvailability.IsAvailable(
            isWindows: false, isInteractive: true, xdgCurrentDesktop: "GNOME", display: null, waylandDisplay: null));

    [Fact]
    public void TrayAvailability_Linux_DesktopAndDisplay_True() =>
        Assert.True(TrayAvailability.IsAvailable(
            isWindows: false, isInteractive: true, xdgCurrentDesktop: "KDE", display: ":0", waylandDisplay: null));

    [Fact]
    public void TrayAvailability_Linux_DisplayButNoDesktop_False() =>
        Assert.False(TrayAvailability.IsAvailable(
            isWindows: false, isInteractive: true, xdgCurrentDesktop: null, display: ":0", waylandDisplay: null));
}
