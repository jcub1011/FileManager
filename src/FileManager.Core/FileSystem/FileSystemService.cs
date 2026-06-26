using System.IO;

namespace FileManager.Core.FileSystem;

/// <summary>
/// <see cref="System.IO"/>-based implementation. Reflection-free and platform-neutral:
/// all path handling goes through <see cref="Path"/> and drive enumeration through
/// <see cref="DriveInfo"/>, so it behaves correctly on both Windows and Linux. Keep it
/// that way — this type is part of the AOT-clean surface.
/// </summary>
public sealed class FileSystemService : IFileSystemService
{
    public IReadOnlyList<FileSystemEntry> GetEntries(string path)
    {
        var entries = new List<FileSystemEntry>();

        DirectoryInfo dir;
        try
        {
            dir = new DirectoryInfo(path);
            if (!dir.Exists)
                return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return entries;
        }

        // Enumerate directories then files. Each access is guarded individually so a
        // single unreadable entry doesn't abort the whole listing.
        try
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                try
                {
                    entries.Add(new FileSystemEntry(sub.Name, sub.FullName, true, 0, sub.LastWriteTime));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip entries we can't stat.
                }
            }

            foreach (var file in dir.EnumerateFiles())
            {
                try
                {
                    entries.Add(new FileSystemEntry(file.Name, file.FullName, false, file.Length, file.LastWriteTime));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip entries we can't stat.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Directory became unreadable mid-enumeration; return whatever we gathered.
        }

        return entries;
    }

    public IReadOnlyList<FileSystemEntry> GetRoots()
    {
        var roots = new List<FileSystemEntry>();

        string home = GetHomeDirectory();
        if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
            roots.Add(new FileSystemEntry("Home", home, true, 0, SafeModified(home)));

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                // On Windows this is "C:\", "D:\"; on Linux the single ready root is "/".
                string rootPath = drive.RootDirectory.FullName;
                roots.Add(new FileSystemEntry(rootPath, rootPath, true, 0, SafeModified(rootPath)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Drive enumeration failed; home (if any) is still returned.
        }

        return roots;
    }

    public string GetHomeDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string? GetParent(string path)
    {
        try
        {
            return Directory.GetParent(path)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static DateTime SafeModified(string path)
    {
        try
        {
            return Directory.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTime.MinValue;
        }
    }
}
