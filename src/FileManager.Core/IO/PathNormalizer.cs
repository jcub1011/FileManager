using System.IO;

namespace FileManager.Core.IO;

/// <summary>
/// Path normalization and comparison for the engine. Resolves the Appendix B item flagged in M0
/// for the scope M1 exercises: <b>local</b> absolute paths with OS-appropriate case sensitivity.
/// </summary>
/// <remarks>
/// Case folding follows the host: Windows/macOS compare case-insensitively, Linux case-sensitively.
/// UNC and <c>\\?\</c> long-path edge cases are explicitly deferred to M9 network-target work.
/// </remarks>
public static class PathNormalizer
{
    /// <summary>The comparison used for path equality on this OS.</summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Normalizes a path to a full, rooted form with a consistent directory separator and no
    /// trailing separator (except a bare root).
    /// </summary>
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        return TrimTrailingSeparator(full);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is the same as, or nested under, <paramref name="root"/>.
    /// Both are normalized first. A path equal to the root counts as "under" it.
    /// </summary>
    public static bool IsUnder(string root, string path)
    {
        string normRoot = Normalize(root);
        string normPath = Normalize(path);

        if (normPath.Equals(normRoot, Comparison))
            return true;

        string rootWithSep = normRoot + Path.DirectorySeparatorChar;
        return normPath.StartsWith(rootWithSep, Comparison);
    }

    /// <summary>
    /// The portion of <paramref name="path"/> relative to <paramref name="root"/> (e.g.
    /// <c>sub\file.txt</c>), assuming <paramref name="path"/> is under <paramref name="root"/>.
    /// </summary>
    public static string GetRelativePath(string root, string path) =>
        Path.GetRelativePath(Normalize(root), Normalize(path));

    /// <summary>Whether two paths refer to the same location on this OS.</summary>
    public static bool AreEqual(string a, string b) =>
        Normalize(a).Equals(Normalize(b), Comparison);

    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1)
            return path;

        char last = path[^1];
        if (last != Path.DirectorySeparatorChar && last != Path.AltDirectorySeparatorChar)
            return path;

        // Preserve a root like "C:\" or "/".
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetPathRoot(path) == path ? path : trimmed;
    }
}
