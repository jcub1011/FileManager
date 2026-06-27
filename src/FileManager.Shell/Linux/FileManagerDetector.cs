namespace FileManager.Shell.Linux;

/// <summary>The Linux file managers FileManager can register a right-click action with (spec §5.3).</summary>
public enum LinuxFileManager
{
    /// <summary>GNOME Files (Nautilus) — scripts directory.</summary>
    Nautilus,

    /// <summary>KDE Dolphin — ServiceMenu <c>.desktop</c>.</summary>
    Dolphin,

    /// <summary>Cinnamon Nemo — <c>.nemo_action</c>.</summary>
    Nemo,

    /// <summary>XFCE Thunar — custom action in <c>uca.xml</c>.</summary>
    Thunar,
}

/// <summary>
/// Best-effort detection of installed Linux file managers (spec §5.3): the installer registers an action
/// only for the FMs actually present, rather than assuming a single <c>.desktop</c>. Detection probes for
/// each FM's executable on <c>PATH</c> (and may be extended to config dirs). The probe is INJECTED so the
/// logic is deterministic and testable on any OS with no real environment dependency.
/// </summary>
public sealed class FileManagerDetector
{
    private readonly Func<string, bool> _executableExists;

    /// <summary>The executable name probed for each supported file manager.</summary>
    private static readonly IReadOnlyDictionary<LinuxFileManager, string> Executables =
        new Dictionary<LinuxFileManager, string>
        {
            [LinuxFileManager.Nautilus] = "nautilus",
            [LinuxFileManager.Dolphin] = "dolphin",
            [LinuxFileManager.Nemo] = "nemo",
            [LinuxFileManager.Thunar] = "thunar",
        };

    /// <summary>
    /// Creates a detector. <paramref name="executableExists"/> answers whether a given executable name is
    /// available (defaults to a real <c>PATH</c> scan); tests inject a deterministic predicate.
    /// </summary>
    public FileManagerDetector(Func<string, bool>? executableExists = null) =>
        _executableExists = executableExists ?? ExistsOnPath;

    /// <summary>Returns every supported file manager whose executable is present.</summary>
    public IReadOnlyList<LinuxFileManager> DetectInstalled() =>
        Executables
            .Where(kv => _executableExists(kv.Value))
            .Select(kv => kv.Key)
            .ToList();

    /// <summary>Whether the given file manager's executable is present.</summary>
    public bool IsInstalled(LinuxFileManager fileManager) =>
        Executables.TryGetValue(fileManager, out string? exe) && _executableExists(exe);

    /// <summary>The probed executable name for <paramref name="fileManager"/>.</summary>
    public static string ExecutableName(LinuxFileManager fileManager) => Executables[fileManager];

    // Real PATH scan: split $PATH and test for an executable file of the given name in each entry.
    private static bool ExistsOnPath(string executable)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return false;

        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, executable)))
                    return true;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry — skip it.
            }
        }

        return false;
    }
}
