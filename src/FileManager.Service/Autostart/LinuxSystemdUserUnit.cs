using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace FileManager.Service.Autostart;

/// <summary>
/// Per-user autostart on Linux via a <c>systemd --user</c> unit (spec §5.3). Installs
/// <c>~/.config/systemd/user/filemanager.service</c> and enables it with <c>systemctl --user enable
/// --now</c>; uninstall disables it and removes the file. The unit-file CONTENT generation is the pure,
/// testable <see cref="BuildUnitContent"/> — install/uninstall (which actually touch the filesystem and
/// invoke <c>systemctl</c>) are deliberately separate so tests assert the content without enabling
/// anything.
/// </summary>
public static class LinuxSystemdUserUnit
{
    /// <summary>The unit file name (matches the spec's <c>filemanager.service</c>).</summary>
    public const string UnitFileName = "filemanager.service";

    /// <summary>
    /// Builds the systemd user-unit file content that launches <paramref name="execStart"/>. Pure: the
    /// same input always yields the same text, so tests assert it without any side effects.
    /// </summary>
    public static string BuildUnitContent(string execStart)
    {
        if (string.IsNullOrWhiteSpace(execStart))
            throw new ArgumentException("ExecStart command must be provided.", nameof(execStart));

        return $"""
            [Unit]
            Description=FileManager per-user file-automation service
            After=default.target

            [Service]
            Type=simple
            ExecStart={execStart}
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=default.target

            """;
    }

    /// <summary>The resolved unit-file path under the user's systemd config directory.</summary>
    public static string ResolveUnitPath(string? home = null)
    {
        string baseHome = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(baseHome, ".config", "systemd", "user", UnitFileName);
    }

    /// <summary>
    /// Writes the unit file and runs <c>systemctl --user daemon-reload</c> + <c>enable --now</c>. Returns
    /// the launched command's combined exit summary; throws only on a filesystem error writing the file.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static string Install(string execStart)
    {
        string unitPath = ResolveUnitPath();
        string? dir = Path.GetDirectoryName(unitPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(unitPath, BuildUnitContent(execStart));

        RunSystemctl("daemon-reload");
        RunSystemctl("enable", "--now", UnitFileName);
        return $"Installed and enabled {unitPath}";
    }

    /// <summary>Disables the unit and removes the file.</summary>
    [UnsupportedOSPlatform("windows")]
    public static string Uninstall()
    {
        RunSystemctl("disable", "--now", UnitFileName);

        string unitPath = ResolveUnitPath();
        if (File.Exists(unitPath))
            File.Delete(unitPath);

        return $"Disabled and removed {unitPath}";
    }

    private static void RunSystemctl(params string[] args)
    {
        var psi = new ProcessStartInfo("systemctl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--user");
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using Process? process = Process.Start(psi);
            process?.WaitForExit();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // systemctl missing (non-systemd host): the unit file is still written for manual use.
        }
    }
}
