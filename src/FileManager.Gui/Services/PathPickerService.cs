using FileManager.Core.FileSystem;

namespace FileManager.Gui.Services;

/// <summary>
/// A directory/file picker built on the read-only <see cref="IFileSystemService"/> (the abstraction
/// migrated from the retired Avalonia shell in M0). It exposes navigation — roots, listing a directory,
/// the parent — so a picker view can browse without touching Avalonia's storage-provider API directly,
/// keeping the navigation logic testable. The view layers a native folder dialog on top where available;
/// this service is the platform-neutral core.
/// </summary>
public sealed class PathPickerService
{
    private readonly IFileSystemService _fileSystem;

    /// <summary>Creates a picker over <paramref name="fileSystem"/> (production: <see cref="FileSystemService"/>).</summary>
    public PathPickerService(IFileSystemService fileSystem) => _fileSystem = fileSystem;

    /// <summary>The starting locations (drive roots + home) to offer.</summary>
    public IReadOnlyList<FileSystemEntry> GetRoots() => _fileSystem.GetRoots();

    /// <summary>The directories and files directly under <paramref name="path"/> (never throws).</summary>
    public IReadOnlyList<FileSystemEntry> GetEntries(string path) => _fileSystem.GetEntries(path);

    /// <summary>The directory entries directly under <paramref name="path"/> (folder-picker view).</summary>
    public IReadOnlyList<FileSystemEntry> GetDirectories(string path) =>
        _fileSystem.GetEntries(path).Where(e => e.IsDirectory).ToList();

    /// <summary>The parent of <paramref name="path"/>, or null when it is already a root.</summary>
    public string? GetParent(string path) => _fileSystem.GetParent(path);

    /// <summary>The user's home directory.</summary>
    public string GetHomeDirectory() => _fileSystem.GetHomeDirectory();
}
