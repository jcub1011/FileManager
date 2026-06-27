using System.Runtime.Versioning;

namespace FileManager.Shell.Windows;

/// <summary>One generated HKCU shell-verb registration: the sub-key path and the command string.</summary>
/// <param name="KeyPath">The HKCU sub-key (under <c>Software\Classes</c>) holding the verb.</param>
/// <param name="Command">The <c>command</c> sub-key default value that launches the shell entry.</param>
public sealed record RegistryVerbEntry(string KeyPath, string Command);

/// <summary>
/// Per-user (HKCU) Windows context-menu verb registration (spec §5.3): the Win10/classic-menu fallback
/// for the Win11 <c>IExplorerCommand</c> top-level entry. Registers a "Run FileManager" verb under
/// <c>Directory</c>, <c>Directory\Background</c>, and <c>AllFilesystemObjects</c>, each invoking the shell
/// launcher with the right-clicked path and <c>--manual</c> so the always-prompt chooser (§3.2) runs.
/// </summary>
/// <remarks>
/// The ENTRY GENERATION (<see cref="BuildEntries"/>) is pure and testable — it returns the exact
/// (key-path, command) pairs without touching the registry; <see cref="Install"/>/<see cref="Uninstall"/>
/// (guarded <c>[SupportedOSPlatform("windows")]</c>, using <c>Microsoft.Win32.Registry</c>) are separate,
/// mirroring the M6 autostart generators so tests assert the generated entries and NEVER write HKCU.
/// </remarks>
public static class RegistryVerbs
{
    /// <summary>The verb sub-key name registered under each shell node.</summary>
    public const string VerbName = "FileManagerRun";

    /// <summary>The menu caption shown in the context menu.</summary>
    public const string MenuText = "Run FileManager…";

    /// <summary>The HKCU class roots that get the verb (file, folder, folder background, drive, etc.).</summary>
    private static readonly string[] ClassNodes =
    {
        @"Directory",
        @"Directory\Background",
        @"AllFilesystemObjects",
    };

    /// <summary>
    /// Builds the (key-path, command) entries for the three shell nodes, each launching
    /// <paramref name="launcherPath"/> with the invoked path and <c>--manual</c>. Pure: the same input
    /// always yields the same entries, so tests assert them with no side effects.
    /// <c>Directory\Background</c> uses <c>%V</c> (the focused folder) since no item is selected; the
    /// other nodes use <c>%1</c> (the right-clicked item).
    /// </summary>
    public static IReadOnlyList<RegistryVerbEntry> BuildEntries(string launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            throw new ArgumentException("Launcher path must be provided.", nameof(launcherPath));

        var entries = new List<RegistryVerbEntry>(ClassNodes.Length);
        foreach (string node in ClassNodes)
        {
            string commandKey = $@"Software\Classes\{node}\shell\{VerbName}\command";
            string pathToken = node.EndsWith("Background", StringComparison.Ordinal) ? "%V" : "%1";
            string command = $"\"{launcherPath}\" \"{pathToken}\" --manual";
            entries.Add(new RegistryVerbEntry(commandKey, command));
        }

        return entries;
    }

    /// <summary>The HKCU verb sub-key paths (without the trailing <c>\command</c>) used by uninstall.</summary>
    public static IReadOnlyList<string> BuildVerbKeyPaths() =>
        ClassNodes.Select(node => $@"Software\Classes\{node}\shell\{VerbName}").ToList();

    /// <summary>
    /// Writes the verb keys under HKCU (no admin required). Each verb key carries the
    /// <see cref="MenuText"/> caption and a <c>command</c> sub-key launching the shell entry.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string Install(string launcherPath)
    {
        foreach (RegistryVerbEntry entry in BuildEntries(launcherPath))
        {
            // entry.KeyPath ends with \command; the parent verb key holds the menu caption.
            string verbKeyPath = entry.KeyPath[..entry.KeyPath.LastIndexOf(@"\command", StringComparison.Ordinal)];

            using Microsoft.Win32.RegistryKey verbKey =
                Microsoft.Win32.Registry.CurrentUser.CreateSubKey(verbKeyPath, writable: true);
            verbKey.SetValue(null, MenuText);

            using Microsoft.Win32.RegistryKey commandKey =
                Microsoft.Win32.Registry.CurrentUser.CreateSubKey(entry.KeyPath, writable: true);
            commandKey.SetValue(null, entry.Command);
        }

        return $"Registered {ClassNodes.Length} HKCU context-menu verb(s) for {launcherPath}";
    }

    /// <summary>Removes the verb keys from HKCU (idempotent — absent keys are ignored).</summary>
    [SupportedOSPlatform("windows")]
    public static string Uninstall()
    {
        foreach (string verbKeyPath in BuildVerbKeyPaths())
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(verbKeyPath, throwOnMissingSubKey: false);
            }
            catch (System.Security.SecurityException)
            {
                // No write access to that hive (unusual for HKCU) — skip; other nodes still cleaned.
            }
        }

        return $"Removed {ClassNodes.Length} HKCU context-menu verb(s)";
    }
}
