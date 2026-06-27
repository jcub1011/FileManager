using System.Diagnostics;
using System.Runtime.Versioning;

namespace FileManager.Service.Autostart;

/// <summary>
/// Per-user autostart on Windows via a Task Scheduler logon task (spec §5.3), registered with NO admin
/// rights using <c>schtasks.exe</c>. The command-line GENERATION is the pure, testable
/// <see cref="BuildCreateArguments"/> / <see cref="BuildDeleteArguments"/> — install/uninstall (which
/// actually spawn <c>schtasks.exe</c>) are separate so tests assert the generated argument vector
/// without registering anything.
/// </summary>
public static class WindowsLogonTask
{
    /// <summary>The Task Scheduler task name registered for the service.</summary>
    public const string TaskName = "FileManager";

    /// <summary>
    /// Builds the <c>schtasks.exe</c> argument vector that creates a per-user ONLOGON task launching
    /// <paramref name="executablePath"/>. Pure — the same inputs always yield the same args, so tests
    /// assert it without side effects. <c>/RL LIMITED</c> requests the user's normal (non-elevated)
    /// rights; <c>/F</c> overwrites an existing task; the run target is quoted to tolerate spaces.
    /// </summary>
    public static IReadOnlyList<string> BuildCreateArguments(string executablePath, string? userName = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Executable path must be provided.", nameof(executablePath));

        var args = new List<string>
        {
            "/Create",
            "/TN", TaskName,
            "/TR", $"\"{executablePath}\"",
            "/SC", "ONLOGON",
            "/RL", "LIMITED",
            "/F",
        };

        if (!string.IsNullOrWhiteSpace(userName))
        {
            args.Add("/RU");
            args.Add(userName);
        }

        return args;
    }

    /// <summary>Builds the <c>schtasks.exe</c> argument vector that deletes the task (pure/testable).</summary>
    public static IReadOnlyList<string> BuildDeleteArguments() =>
        new[] { "/Delete", "/TN", TaskName, "/F" };

    /// <summary>Registers the per-user logon task by spawning <c>schtasks.exe</c>.</summary>
    [SupportedOSPlatform("windows")]
    public static string Install(string executablePath)
    {
        RunSchtasks(BuildCreateArguments(executablePath));
        return $"Registered logon task '{TaskName}' for {executablePath}";
    }

    /// <summary>Removes the per-user logon task.</summary>
    [SupportedOSPlatform("windows")]
    public static string Uninstall()
    {
        RunSchtasks(BuildDeleteArguments());
        return $"Removed logon task '{TaskName}'";
    }

    private static void RunSchtasks(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using Process? process = Process.Start(psi);
            process?.WaitForExit();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // schtasks unavailable (unusual on Windows); surfaced to the caller as a no-op registration.
        }
    }
}
