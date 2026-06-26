using System.IO.Enumeration;

namespace FileManager.Core.Filtering;

/// <summary>
/// Glob matching for filter screening. Backed by
/// <see cref="FileSystemName.MatchesSimpleExpression(System.ReadOnlySpan{char}, System.ReadOnlySpan{char}, bool)"/>,
/// the framework's built-in <c>*</c>/<c>?</c> wildcard matcher — AOT-clean and consistent with how
/// the OS enumerates names. Case sensitivity follows the host filesystem (matching
/// <see cref="FileManager.Core.IO.PathNormalizer.Comparison"/>): case-insensitive on Windows/macOS,
/// case-sensitive on Linux, so a pattern matches exactly the names the OS would.
/// </summary>
public static class GlobMatcher
{
    private static readonly bool IgnoreCase =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Whether <paramref name="name"/> matches the single glob <paramref name="pattern"/>.</summary>
    public static bool IsMatch(string pattern, string name) =>
        FileSystemName.MatchesSimpleExpression(pattern, name, IgnoreCase);

    /// <summary>Whether <paramref name="name"/> matches any of <paramref name="patterns"/>.</summary>
    public static bool MatchesAny(IReadOnlyList<string> patterns, string name)
    {
        foreach (string pattern in patterns)
        {
            if (IsMatch(pattern, name))
                return true;
        }

        return false;
    }
}
