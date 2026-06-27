using System.Diagnostics;
using System.Runtime.InteropServices;
using FileManager.Shell.Linux;
using FileManager.Shell.Windows;

namespace FileManager.Shell;

/// <summary>
/// Ties the per-OS shell-integration registration together (spec §5.3) and integrates with the M6
/// autostart installer so ONE install/uninstall flow covers both autostart and the right-click entries.
/// On Windows it registers the HKCU context-menu verbs (the Win10/classic fallback; the Win11 top-level
/// MSIX is deferred to M9 — see <c>Windows/msix/SIGNING.md</c>). On Linux it registers an action for every
/// detected file manager (Nautilus/Dolphin/Nemo/Thunar). All registration is per-user (no admin).
/// </summary>
/// <remarks>
/// The two side-effecting collaborators are injectable seams so tests exercise the routing without
/// touching the real registry / FM config dirs / spawning the service:
/// <list type="bullet">
/// <item><b>autostart</b> — a delegate that installs/uninstalls the M6 autostart entry (defaults to
/// spawning the sibling <c>FileManager.Service</c> with <c>--install</c>/<c>--uninstall</c>, since the
/// autostart generators live in the Service assembly which the Shell deliberately does not reference).</item>
/// <item><b>home</b> — the base directory the Linux actions are written under (defaults to <c>$HOME</c>;
/// tests pass a temp dir).</item>
/// </list>
/// </remarks>
public sealed class RegistrationInstaller
{
    private readonly Func<bool, string> _autostart;
    private readonly Func<IReadOnlyList<LinuxFileManager>> _detectFileManagers;
    private readonly string _home;
    private readonly bool _isWindows;

    /// <summary>
    /// Creates an installer. All parameters default to production behaviour; tests inject seams so nothing
    /// real is registered. <paramref name="autostart"/> receives <c>true</c> to install or <c>false</c> to
    /// uninstall the M6 autostart entry and returns a status line.
    /// </summary>
    public RegistrationInstaller(
        Func<bool, string>? autostart = null,
        Func<IReadOnlyList<LinuxFileManager>>? detectFileManagers = null,
        string? home = null,
        bool? isWindows = null)
    {
        _autostart = autostart ?? DefaultAutostart;
        _detectFileManagers = detectFileManagers ?? (() => new FileManagerDetector().DetectInstalled());
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _isWindows = isWindows ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    /// <summary>
    /// Registers the shell entries for the current OS plus the M6 autostart entry, returning one status
    /// line per action. <paramref name="launcherPath"/> is the FileManager.Shell executable the menu
    /// entries invoke (defaults to the running shell binary).
    /// </summary>
    public IReadOnlyList<string> Register(string? launcherPath = null)
    {
        string launcher = launcherPath ?? CurrentLauncherPath();
        var results = new List<string> { _autostart(true) };

        if (_isWindows)
        {
            results.Add(RegisterWindows(launcher));
        }
        else
        {
            results.AddRange(RegisterLinux(launcher));
        }

        return results;
    }

    /// <summary>Removes the shell entries for the current OS plus the M6 autostart entry.</summary>
    public IReadOnlyList<string> Unregister()
    {
        var results = new List<string> { _autostart(false) };

        if (_isWindows)
        {
            results.Add(UnregisterWindows());
        }
        else
        {
            results.AddRange(UnregisterLinux());
        }

        return results;
    }

    private static string RegisterWindows(string launcher)
    {
        if (OperatingSystem.IsWindows())
            return RegistryVerbs.Install(launcher);
        // Reachable only via an injected isWindows:true on a non-Windows host (tests): generation only.
        return $"Windows verbs (generation): {RegistryVerbs.BuildEntries(launcher).Count} entries for {launcher}";
    }

    private static string UnregisterWindows()
    {
        if (OperatingSystem.IsWindows())
            return RegistryVerbs.Uninstall();
        return "Windows verbs (generation): would remove HKCU entries";
    }

    private IReadOnlyList<string> RegisterLinux(string launcher)
    {
        var results = new List<string>();
        IReadOnlyList<LinuxFileManager> detected = _detectFileManagers();
        if (detected.Count == 0)
            results.Add("No supported Linux file manager detected; right-click menu not registered.");

        foreach (LinuxFileManager fm in detected)
            results.Add($"{fm}: {LinuxShellActions.Install(fm, launcher, _home)}");

        return results;
    }

    private IReadOnlyList<string> UnregisterLinux()
    {
        var results = new List<string>();
        foreach (LinuxFileManager fm in Enum.GetValues<LinuxFileManager>())
            results.Add($"{fm}: {LinuxShellActions.Uninstall(fm, _home)}");
        return results;
    }

    // Default autostart integration: spawn the sibling FileManager.Service with --install/--uninstall so
    // the single flow covers the M6 autostart entry without the Shell referencing the Service assembly.
    private static string DefaultAutostart(bool install)
    {
        string dir = AppContext.BaseDirectory;
        string exeName = OperatingSystem.IsWindows() ? "FileManager.Service.exe" : "FileManager.Service";
        string exePath = Path.Combine(dir, exeName);
        string verb = install ? "--install" : "--uninstall";

        try
        {
            var psi = new ProcessStartInfo(exePath, verb)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? process = Process.Start(psi);
            process?.WaitForExit();
            return $"Autostart {(install ? "installed" : "uninstalled")} via {exeName} {verb}.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Autostart {verb} skipped: {exeName} not found next to the shell binary.";
        }
    }

    private static string CurrentLauncherPath() =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? (OperatingSystem.IsWindows() ? "FileManager.Shell.exe" : "FileManager.Shell");
}
