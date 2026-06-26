using System.IO;
using System.Runtime.InteropServices;

namespace FileManager.Core.Configuration;

/// <summary>
/// Resolves the per-OS configuration directory and the well-known files/subfolders within it:
/// Windows <c>%APPDATA%\FileManager\</c>; Linux <c>$XDG_CONFIG_HOME/filemanager/</c> with a
/// fallback to <c>~/.config/filemanager/</c>. The <c>profiles/</c> subfolder holds one JSON file
/// per Profile and <c>config.json</c> holds the global <see cref="ServiceConfig"/>.
/// </summary>
/// <remarks>
/// TODO (Appendix B): path-format rules (UNC, <c>\\?\</c> long paths, <c>~</c>/env expansion, case
/// sensitivity) are deferred to M1; this type only assembles the base locations.
/// </remarks>
public static class ConfigPaths
{
    /// <summary>Application folder name used under <c>%APPDATA%</c> on Windows.</summary>
    public const string WindowsAppFolderName = "FileManager";

    /// <summary>Application folder name used under XDG config on Linux/macOS.</summary>
    public const string XdgAppFolderName = "filemanager";

    /// <summary>Subfolder (under the config dir) containing per-Profile JSON files.</summary>
    public const string ProfilesFolderName = "profiles";

    /// <summary>The global service-config file name (in the config dir).</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>The resolved per-OS configuration directory for the current environment.</summary>
    public static string GetConfigDirectory() =>
        BuildConfigDirectory(
            isWindows: RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            appData: Environment.GetEnvironmentVariable("APPDATA"),
            xdgConfigHome: Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            home: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The <c>profiles/</c> subfolder of the config directory.</summary>
    public static string GetProfilesDirectory() =>
        Path.Combine(GetConfigDirectory(), ProfilesFolderName);

    /// <summary>The <c>config.json</c> path in the config directory.</summary>
    public static string GetConfigFilePath() =>
        Path.Combine(GetConfigDirectory(), ConfigFileName);

    /// <summary>
    /// Pure resolver for the config directory from explicit inputs — the testable core of
    /// <see cref="GetConfigDirectory"/>, independent of the host environment.
    /// </summary>
    /// <param name="isWindows">Whether the target platform is Windows.</param>
    /// <param name="appData">Value of <c>%APPDATA%</c> (Windows).</param>
    /// <param name="xdgConfigHome">Value of <c>$XDG_CONFIG_HOME</c> (Linux/macOS).</param>
    /// <param name="home">The user's home directory (fallback base).</param>
    public static string BuildConfigDirectory(bool isWindows, string? appData, string? xdgConfigHome, string? home)
    {
        if (isWindows)
        {
            string baseDir = !string.IsNullOrEmpty(appData)
                ? appData
                : Path.Combine(home ?? string.Empty, "AppData", "Roaming");
            return Path.Combine(baseDir, WindowsAppFolderName);
        }

        // Linux/macOS: prefer XDG_CONFIG_HOME, else ~/.config.
        string xdgBase = !string.IsNullOrEmpty(xdgConfigHome)
            ? xdgConfigHome
            : Path.Combine(home ?? string.Empty, ".config");
        return Path.Combine(xdgBase, XdgAppFolderName);
    }
}
