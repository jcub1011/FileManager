using FileManager.Contracts.Messages;
using FileManager.Shell;
using FileManager.Shell.Linux;
using Xunit;

namespace FileManager.Shell.Tests;

/// <summary>
/// Asserts the shell launcher's manual handling and the <see cref="RegistrationInstaller"/> routing, all
/// behind injected seams so NO real process spawns and NO real registry / FM config is touched.
/// </summary>
public sealed class ShellLauncherTests
{
    [Fact]
    public async Task ManualInvocation_EnsuresGuiBeforeSubmitting()
    {
        bool guiLaunched = false;
        var launcher = new FallbackLauncher(
            connect: (_, _) => Task.FromResult<Stream>(new MemoryStreamPair().Client),
            launchService: _ => Task.CompletedTask,
            launchGui: _ => { guiLaunched = true; return Task.CompletedTask; },
            ensureGuiForManual: true,
            initialConnectTimeout: TimeSpan.FromMilliseconds(50),
            postLaunchTimeout: TimeSpan.FromMilliseconds(50));

        // The exchange will fail to read a real response (the stream is empty), but the GUI-launch seam
        // must have fired FIRST for a manual payload — the always-prompt-subscriber invariant.
        await launcher.SubmitAsync(new SubmitPayload("/data", null, false, IsManual: true));

        Assert.True(guiLaunched);
    }

    [Fact]
    public async Task NonManualInvocation_DoesNotLaunchGui()
    {
        bool guiLaunched = false;
        var launcher = new FallbackLauncher(
            connect: (_, _) => Task.FromResult<Stream>(new MemoryStreamPair().Client),
            launchService: _ => Task.CompletedTask,
            launchGui: _ => { guiLaunched = true; return Task.CompletedTask; },
            ensureGuiForManual: true,
            initialConnectTimeout: TimeSpan.FromMilliseconds(50),
            postLaunchTimeout: TimeSpan.FromMilliseconds(50));

        await launcher.SubmitAsync(new SubmitPayload("/data", null, false, IsManual: false));

        Assert.False(guiLaunched);
    }

    [Fact]
    public void RegistrationInstaller_Linux_RegistersDetectedFileManagers_InTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), "fmreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        bool autostartInstalled = false;
        try
        {
            var installer = new RegistrationInstaller(
                autostart: install => { autostartInstalled = install; return "autostart-ok"; },
                detectFileManagers: () => new[] { LinuxFileManager.Nautilus, LinuxFileManager.Nemo },
                home: home,
                isWindows: false);

            IReadOnlyList<string> results = installer.Register("/opt/fm/FileManager.Shell");

            Assert.True(autostartInstalled);
            Assert.Contains(results, r => r.Contains("Nautilus"));
            Assert.Contains(results, r => r.Contains("Nemo"));
            Assert.True(File.Exists(Path.Combine(
                LinuxShellActions.NautilusScriptsDir(home), LinuxShellActions.NautilusScriptName)));

            installer.Unregister();
            Assert.False(File.Exists(Path.Combine(
                LinuxShellActions.NautilusScriptsDir(home), LinuxShellActions.NautilusScriptName)));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RegistrationInstaller_Linux_NoFileManager_ReportsAndStillCoversAutostart()
    {
        string home = Path.Combine(Path.GetTempPath(), "fmreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var installer = new RegistrationInstaller(
                autostart: _ => "autostart-ok",
                detectFileManagers: Array.Empty<LinuxFileManager>,
                home: home,
                isWindows: false);

            IReadOnlyList<string> results = installer.Register("/opt/fm/FileManager.Shell");

            Assert.Contains(results, r => r.Contains("autostart-ok"));
            Assert.Contains(results, r => r.Contains("No supported Linux file manager"));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RegistrationInstaller_Windows_GeneratesVerbs_WithoutTouchingRegistry()
    {
        // On a real Windows host Register() would write HKCU, which we must not do in CI. Run the
        // generation-only branch (isWindows:true) ONLY off Windows, where RegistryVerbs.Install is not
        // reached and the installer reports the generated entry count instead.
        if (OperatingSystem.IsWindows())
            return;

        bool autostartTouched = false;
        var installer = new RegistrationInstaller(
            autostart: install => { autostartTouched = true; return install ? "ai" : "au"; },
            home: "/tmp",
            isWindows: true);

        IReadOnlyList<string> results = installer.Register("/opt/fm/FileManager.Shell");
        Assert.True(autostartTouched);
        Assert.Contains(results, r => r.Contains("generation"));
    }

    // A trivial in-memory bidirectional stream so connect() returns "an open stream" without a real pipe.
    private sealed class MemoryStreamPair
    {
        public Stream Client { get; } = new MemoryStream();
    }
}
