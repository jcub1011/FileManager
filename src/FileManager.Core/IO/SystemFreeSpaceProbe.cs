using System.IO;

namespace FileManager.Core.IO;

/// <summary>
/// <see cref="System.IO.DriveInfo"/>-backed <see cref="IFreeSpaceProbe"/>. Reflection-free and
/// platform-neutral — part of the AOT-clean surface, like
/// <see cref="FileManager.Core.FileSystem.FileSystemService"/> (which uses the same
/// <see cref="DriveInfo.GetDrives"/> enumeration). Resolves the containing volume by longest-prefix
/// match over the ready drives so a path under a deeply nested mount point picks that mount rather
/// than the OS root.
/// </summary>
public sealed class SystemFreeSpaceProbe : IFreeSpaceProbe
{
    public VolumeSpace Probe(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // The path can't even be rooted; treat the volume as unconstrained so we never false-fail.
            return new VolumeSpace(PathNormalizer.Normalize(path.Length == 0 ? "." : path), long.MaxValue);
        }

        try
        {
            DriveInfo? best = null;
            int bestRootLength = -1;

            // On Windows the roots are "C:\", "D:\"; on Linux the mounts are "/", "/mnt/data", … —
            // longest matching root wins so a file under a sub-mount resolves to that mount, not "/".
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                string root = drive.RootDirectory.FullName;
                if (!StartsWithVolumeRoot(fullPath, root))
                    continue;

                if (root.Length > bestRootLength)
                {
                    best = drive;
                    bestRootLength = root.Length;
                }
            }

            if (best is not null)
            {
                return new VolumeSpace(
                    PathNormalizer.Normalize(best.RootDirectory.FullName),
                    best.AvailableFreeSpace);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Drive enumeration or AvailableFreeSpace access failed; fall through to the path-root
            // fallback below and ultimately to "unconstrained" rather than false-failing.
        }

        // Fallback: derive the volume from the path root alone (no live free-space figure available),
        // and report it as unconstrained so the proactive check defers to the reactive rollback path.
        string? root2 = TryGetPathRoot(fullPath);
        return new VolumeSpace(
            PathNormalizer.Normalize(string.IsNullOrEmpty(root2) ? fullPath : root2),
            long.MaxValue);
    }

    /// <summary>
    /// Whether <paramref name="fullPath"/> sits under volume <paramref name="root"/>: equal to the
    /// root, or sharing it as a directory prefix. Uses <see cref="PathNormalizer.Comparison"/> so
    /// case-sensitivity follows the host.
    /// </summary>
    private static bool StartsWithVolumeRoot(string fullPath, string root)
    {
        if (fullPath.Equals(root, PathNormalizer.Comparison))
            return true;

        // A bare root ("C:\" / "/") already ends in a separator; otherwise append one so "C:\Foo"
        // does not spuriously match a path under "C:\FooBar".
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSep, PathNormalizer.Comparison);
    }

    private static string? TryGetPathRoot(string fullPath)
    {
        try
        {
            return Path.GetPathRoot(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
