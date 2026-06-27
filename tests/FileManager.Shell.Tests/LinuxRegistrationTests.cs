using FileManager.Shell.Linux;
using Xunit;

namespace FileManager.Shell.Tests;

/// <summary>
/// Asserts the PURE Linux shell-registration generators (Nautilus / Dolphin / Nemo / Thunar content) and
/// the injectable <see cref="FileManagerDetector"/>. Install/uninstall are exercised against a TEMP
/// directory, never the real per-user FM config dirs, so the suite is CI-safe on any OS.
/// </summary>
public sealed class LinuxRegistrationTests
{
    private const string Launcher = "/opt/filemanager/FileManager.Shell";

    [Fact]
    public void Detector_ReturnsOnlyInstalled_FromInjectedProbe()
    {
        var detector = new FileManagerDetector(exe => exe is "nautilus" or "thunar");

        IReadOnlyList<LinuxFileManager> detected = detector.DetectInstalled();

        Assert.Contains(LinuxFileManager.Nautilus, detected);
        Assert.Contains(LinuxFileManager.Thunar, detected);
        Assert.DoesNotContain(LinuxFileManager.Dolphin, detected);
        Assert.DoesNotContain(LinuxFileManager.Nemo, detected);
    }

    [Fact]
    public void Detector_NoneInstalled_ReturnsEmpty()
    {
        var detector = new FileManagerDetector(_ => false);
        Assert.Empty(detector.DetectInstalled());
    }

    [Fact]
    public void Nautilus_Script_InvokesLauncherWithManual()
    {
        string content = LinuxShellActions.BuildNautilusScript(Launcher);

        Assert.Contains("#!/usr/bin/env sh", content);
        Assert.Contains("NAUTILUS_SCRIPT_SELECTED_FILE_PATHS", content);
        Assert.Contains(Launcher, content);
        Assert.Contains("--manual", content);
    }

    [Fact]
    public void Dolphin_ServiceMenu_IsValidDesktopEntryWithManual()
    {
        string content = LinuxShellActions.BuildDolphinServiceMenu(Launcher);

        Assert.Contains("[Desktop Entry]", content);
        Assert.Contains("Type=Service", content);
        Assert.Contains("[Desktop Action FileManagerRun]", content);
        Assert.Contains(Launcher, content);
        Assert.Contains("--manual", content);
    }

    [Fact]
    public void Nemo_Action_IsValidWithManual()
    {
        string content = LinuxShellActions.BuildNemoAction(Launcher);

        Assert.Contains("[Nemo Action]", content);
        Assert.Contains("Name=Run FileManager", content);
        Assert.Contains(Launcher, content);
        Assert.Contains("--manual", content);
    }

    [Fact]
    public void Thunar_ActionElement_IsValidWithManual()
    {
        string content = LinuxShellActions.BuildThunarActionElement(Launcher);

        Assert.Contains("<action>", content);
        Assert.Contains("<name>Run FileManager</name>", content);
        Assert.Contains("filemanager-run-1", content);
        Assert.Contains(Launcher, content);
        Assert.Contains("--manual", content);
    }

    [Fact]
    public void EmptyLauncher_Throws() =>
        Assert.Throws<ArgumentException>(() => LinuxShellActions.BuildNautilusScript(""));

    [Fact]
    public void Install_WritesToTempHome_NotRealConfig_AndUninstallRemoves()
    {
        string home = NewTempHome();
        try
        {
            foreach (LinuxFileManager fm in Enum.GetValues<LinuxFileManager>())
            {
                string path = LinuxShellActions.Install(fm, Launcher, home);
                Assert.True(File.Exists(path), $"{fm} action file should exist at {path}");
                Assert.StartsWith(home, path, StringComparison.Ordinal);
                Assert.Contains(Launcher, File.ReadAllText(path));
            }

            // Uninstall removes the single-file actions; Thunar's uca.xml has the action element stripped.
            foreach (LinuxFileManager fm in Enum.GetValues<LinuxFileManager>())
                LinuxShellActions.Uninstall(fm, home);

            Assert.False(File.Exists(Path.Combine(LinuxShellActions.NautilusScriptsDir(home), LinuxShellActions.NautilusScriptName)));
            Assert.False(File.Exists(Path.Combine(LinuxShellActions.DolphinServiceMenusDir(home), LinuxShellActions.DolphinFileName)));
            Assert.False(File.Exists(Path.Combine(LinuxShellActions.NemoActionsDir(home), LinuxShellActions.NemoFileName)));

            string uca = Path.Combine(LinuxShellActions.ThunarConfigDir(home), LinuxShellActions.ThunarFileName);
            Assert.DoesNotContain("filemanager-run-1", File.ReadAllText(uca));
        }
        finally
        {
            TryDelete(home);
        }
    }

    [Fact]
    public void Thunar_Install_MergesIntoExistingUcaXml_PreservingOtherActions()
    {
        string home = NewTempHome();
        try
        {
            string ucaDir = LinuxShellActions.ThunarConfigDir(home);
            Directory.CreateDirectory(ucaDir);
            string ucaPath = Path.Combine(ucaDir, LinuxShellActions.ThunarFileName);
            File.WriteAllText(ucaPath, """
                <?xml version="1.0" encoding="UTF-8"?>
                <actions>
                <action>
                	<name>Open Terminal</name>
                	<unique-id>user-term</unique-id>
                	<command>xterm</command>
                </action>
                </actions>
                """);

            LinuxShellActions.Install(LinuxFileManager.Thunar, Launcher, home);
            string merged = File.ReadAllText(ucaPath);

            Assert.Contains("user-term", merged);            // existing action preserved
            Assert.Contains("filemanager-run-1", merged);    // ours merged in
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(merged, "<actions>"));

            LinuxShellActions.Uninstall(LinuxFileManager.Thunar, home);
            string after = File.ReadAllText(ucaPath);
            Assert.Contains("user-term", after);
            Assert.DoesNotContain("filemanager-run-1", after);
        }
        finally
        {
            TryDelete(home);
        }
    }

    private static string NewTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), "fmshelltest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
