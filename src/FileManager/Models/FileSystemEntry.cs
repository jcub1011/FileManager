namespace FileManager.Models;

/// <summary>
/// A single file-system entry (file, directory, or drive root). Immutable snapshot
/// produced by <see cref="Services.IFileSystemService"/>.
/// </summary>
public sealed record FileSystemEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    System.DateTime Modified);
