using System.Text;

namespace FileManager.Shell.Linux;

/// <summary>
/// Pure content generators + per-user installers for the Linux file-manager right-click actions
/// (spec §5.3): a Nautilus script, a Dolphin ServiceMenu <c>.desktop</c>, a Nemo <c>.nemo_action</c>, and
/// a Thunar custom action (<c>uca.xml</c> entry). Each action shells out to the FileManager launcher with
/// the selected path and <c>--manual</c> so the always-prompt chooser (§3.2) runs.
/// </summary>
/// <remarks>
/// Every generator (<c>Build*</c>) is pure and testable — it returns the file content as a string with no
/// filesystem access. <c>Install</c>/<c>Uninstall</c> take an explicit base directory (defaulting to the
/// real per-user locations under <c>$HOME</c>) so tests write to a temp dir and NEVER touch the real FM
/// config dirs, mirroring the M6 generation-only pattern.
/// </remarks>
public static class LinuxShellActions
{
    /// <summary>The action caption shown in each file manager's context menu.</summary>
    public const string ActionName = "Run FileManager";

    private const string Comment = "Send the selected item to FileManager (always prompts for a Profile)";

    // ---- Nautilus (GNOME Files): an executable script in the scripts dir. ----

    /// <summary>The Nautilus script file name (its base name is the menu caption).</summary>
    public const string NautilusScriptName = "Run FileManager";

    /// <summary>
    /// Builds the Nautilus script: a shell script that forwards the selected URI/path to the launcher with
    /// <c>--manual</c>. Nautilus passes selected paths in <c>$NAUTILUS_SCRIPT_SELECTED_FILE_PATHS</c>.
    /// </summary>
    public static string BuildNautilusScript(string launcherPath)
    {
        RequireLauncher(launcherPath);
        return $"""
            #!/usr/bin/env sh
            # FileManager Nautilus script (spec §5.3). Forwards each selected path to the launcher.
            IFS='
            '
            for selected in $NAUTILUS_SCRIPT_SELECTED_FILE_PATHS; do
                [ -n "$selected" ] && "{launcherPath}" "$selected" --manual
            done

            """;
    }

    /// <summary>The Nautilus scripts directory under a home dir.</summary>
    public static string NautilusScriptsDir(string home) =>
        Path.Combine(home, ".local", "share", "nautilus", "scripts");

    // ---- Dolphin (KDE): a ServiceMenu .desktop file. ----

    /// <summary>The Dolphin ServiceMenu file name.</summary>
    public const string DolphinFileName = "filemanager-run.desktop";

    /// <summary>Builds the Dolphin ServiceMenu <c>.desktop</c> content (uses <c>%f</c> for the selected file).</summary>
    public static string BuildDolphinServiceMenu(string launcherPath)
    {
        RequireLauncher(launcherPath);
        return $"""
            [Desktop Entry]
            Type=Service
            ServiceTypes=KonqPopupMenu/Plugin,all/allfiles
            MimeType=application/octet-stream;inode/directory;
            Actions=FileManagerRun;
            X-KDE-Priority=TopLevel

            [Desktop Action FileManagerRun]
            Name={ActionName}
            Comment={Comment}
            Exec="{launcherPath}" "%f" --manual

            """;
    }

    /// <summary>The Dolphin ServiceMenus directory under a home dir.</summary>
    public static string DolphinServiceMenusDir(string home) =>
        Path.Combine(home, ".local", "share", "kio", "servicemenus");

    // ---- Nemo (Cinnamon): a .nemo_action file. ----

    /// <summary>The Nemo action file name.</summary>
    public const string NemoFileName = "filemanager-run.nemo_action";

    /// <summary>Builds the Nemo <c>.nemo_action</c> content (uses <c>%F</c> for the selected path).</summary>
    public static string BuildNemoAction(string launcherPath)
    {
        RequireLauncher(launcherPath);
        return $"""
            [Nemo Action]
            Name={ActionName}
            Comment={Comment}
            Exec="{launcherPath}" "%F" --manual
            Selection=any
            Extensions=any;

            """;
    }

    /// <summary>The Nemo actions directory under a home dir.</summary>
    public static string NemoActionsDir(string home) =>
        Path.Combine(home, ".local", "share", "nemo", "actions");

    // ---- Thunar (XFCE): a custom action entry for uca.xml. ----

    /// <summary>The Thunar custom-actions file name.</summary>
    public const string ThunarFileName = "uca.xml";

    /// <summary>
    /// Builds a single Thunar custom <c>&lt;action&gt;</c> element (the unit merged into <c>uca.xml</c>),
    /// using <c>%f</c> for the selected path. Returned standalone so a test can assert it; the installer
    /// merges it into the existing <c>uca.xml</c> (or seeds a new one).
    /// </summary>
    public static string BuildThunarActionElement(string launcherPath)
    {
        RequireLauncher(launcherPath);
        return $"""
            <action>
            	<icon></icon>
            	<name>{ActionName}</name>
            	<unique-id>filemanager-run-1</unique-id>
            	<command>"{launcherPath}" "%f" --manual</command>
            	<description>{Comment}</description>
            	<patterns>*</patterns>
            	<directories/>
            	<audio-files/>
            	<image-files/>
            	<other-files/>
            	<text-files/>
            	<video-files/>
            </action>
            """;
    }

    /// <summary>Wraps the action element in a complete (new) <c>uca.xml</c> document.</summary>
    public static string BuildThunarDocument(string launcherPath) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <actions>
        {BuildThunarActionElement(launcherPath)}
        </actions>

        """;

    /// <summary>The Thunar config directory under a home dir.</summary>
    public static string ThunarConfigDir(string home) =>
        Path.Combine(home, ".config", "Thunar");

    // ---- Install / uninstall (explicit base home so tests use a temp dir) ----

    /// <summary>
    /// Installs the action for <paramref name="fileManager"/> under <paramref name="home"/>, writing the
    /// generated file to that FM's per-user location and returning the written path. Thunar merges into an
    /// existing <c>uca.xml</c> when present; the others write a single file.
    /// </summary>
    public static string Install(LinuxFileManager fileManager, string launcherPath, string home)
    {
        RequireLauncher(launcherPath);
        switch (fileManager)
        {
            case LinuxFileManager.Nautilus:
            {
                string path = Path.Combine(NautilusScriptsDir(home), NautilusScriptName);
                WriteFile(path, BuildNautilusScript(launcherPath));
                TryMakeExecutable(path);
                return path;
            }

            case LinuxFileManager.Dolphin:
            {
                string path = Path.Combine(DolphinServiceMenusDir(home), DolphinFileName);
                WriteFile(path, BuildDolphinServiceMenu(launcherPath));
                return path;
            }

            case LinuxFileManager.Nemo:
            {
                string path = Path.Combine(NemoActionsDir(home), NemoFileName);
                WriteFile(path, BuildNemoAction(launcherPath));
                return path;
            }

            case LinuxFileManager.Thunar:
            {
                string path = Path.Combine(ThunarConfigDir(home), ThunarFileName);
                WriteFile(path, MergeThunarAction(path, launcherPath));
                return path;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(fileManager), fileManager, "Unsupported file manager.");
        }
    }

    /// <summary>
    /// Removes the action file for <paramref name="fileManager"/> under <paramref name="home"/>. For
    /// Thunar, the merged action element is stripped from <c>uca.xml</c> rather than deleting the file
    /// (which may hold the user's other actions). Idempotent — an absent file is a no-op.
    /// </summary>
    public static string Uninstall(LinuxFileManager fileManager, string home)
    {
        string path = fileManager switch
        {
            LinuxFileManager.Nautilus => Path.Combine(NautilusScriptsDir(home), NautilusScriptName),
            LinuxFileManager.Dolphin => Path.Combine(DolphinServiceMenusDir(home), DolphinFileName),
            LinuxFileManager.Nemo => Path.Combine(NemoActionsDir(home), NemoFileName),
            LinuxFileManager.Thunar => Path.Combine(ThunarConfigDir(home), ThunarFileName),
            _ => throw new ArgumentOutOfRangeException(nameof(fileManager), fileManager, "Unsupported file manager."),
        };

        if (fileManager == LinuxFileManager.Thunar && File.Exists(path))
        {
            string stripped = StripThunarAction(File.ReadAllText(path));
            File.WriteAllText(path, stripped);
            return $"Removed FileManager action from {path}";
        }

        if (File.Exists(path))
            File.Delete(path);
        return $"Removed {path}";
    }

    // Merges the FileManager action into an existing uca.xml, or seeds a new document. Pure given the
    // existing content; reads the file only to obtain that content.
    private static string MergeThunarAction(string ucaPath, string launcherPath)
    {
        if (!File.Exists(ucaPath))
            return BuildThunarDocument(launcherPath);

        string existing = StripThunarAction(File.ReadAllText(ucaPath));
        int close = existing.LastIndexOf("</actions>", StringComparison.Ordinal);
        if (close < 0)
            return BuildThunarDocument(launcherPath); // unrecognized shape — replace with a clean document.

        return existing[..close]
            + BuildThunarActionElement(launcherPath) + "\n"
            + existing[close..];
    }

    // Removes a previously-merged FileManager action element (matched by its unique-id) from uca.xml.
    private static string StripThunarAction(string content)
    {
        const string marker = "<unique-id>filemanager-run-1</unique-id>";
        if (!content.Contains(marker, StringComparison.Ordinal))
            return content;

        var result = new StringBuilder(content.Length);
        int index = 0;
        while (index < content.Length)
        {
            int actionStart = content.IndexOf("<action>", index, StringComparison.Ordinal);
            if (actionStart < 0)
            {
                result.Append(content, index, content.Length - index);
                break;
            }

            int actionEnd = content.IndexOf("</action>", actionStart, StringComparison.Ordinal);
            if (actionEnd < 0)
            {
                result.Append(content, index, content.Length - index);
                break;
            }

            actionEnd += "</action>".Length;
            string block = content[actionStart..actionEnd];
            result.Append(content, index, actionStart - index);
            if (!block.Contains(marker, StringComparison.Ordinal))
                result.Append(block); // keep the user's other actions verbatim.
            index = actionEnd;
        }

        return result.ToString();
    }

    private static void WriteFile(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    private static void TryMakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (IOException)
        {
            // Non-fatal: the script is still written; the user may chmod it manually.
        }
    }

    private static void RequireLauncher(string launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            throw new ArgumentException("Launcher path must be provided.", nameof(launcherPath));
    }
}
