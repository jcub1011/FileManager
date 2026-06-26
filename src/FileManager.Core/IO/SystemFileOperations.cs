using System.IO;

namespace FileManager.Core.IO;

/// <summary>
/// <see cref="System.IO"/>-backed <see cref="IFileOperations"/>. Reflection-free and
/// platform-neutral — part of the AOT-clean surface, like
/// <see cref="FileManager.Core.FileSystem.FileSystemService"/>. Exceptions propagate by design
/// (see <see cref="IFileOperations"/>).
/// </summary>
public sealed class SystemFileOperations : IFileOperations
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public FileMetadata GetMetadata(string path)
    {
        var info = new FileInfo(path);
        FileAttributes attrs = info.Attributes;
        return new FileMetadata
        {
            Length = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            CreationTimeUtc = info.CreationTimeUtc,
            IsHidden = (attrs & FileAttributes.Hidden) != 0,
            IsSystem = (attrs & FileAttributes.System) != 0,
            IsSymlink = (attrs & FileAttributes.ReparsePoint) != 0,
        };
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Stream OpenWrite(string path) =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

    public void Move(string sourcePath, string destPath, bool overwrite) =>
        File.Move(sourcePath, destPath, overwrite);

    public void Delete(string path) => File.Delete(path);

    public IEnumerable<string> EnumerateFiles(string directory, bool recursive)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(
            directory,
            "*",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
    }
}
