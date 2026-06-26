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
        bool isSymlink = (attrs & FileAttributes.ReparsePoint) != 0;

        // For a symlink, size/timestamps from the link itself describe the reparse point, not the
        // bytes a copy/hash would actually read. Resolve to the final target and stat that so the
        // snapshot matches the content the engine processes. Fall back to the link's own stat if the
        // target can't be resolved (e.g. a dangling link).
        FileInfo statInfo = info;
        if (isSymlink)
        {
            try
            {
                if (info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo resolved && resolved.Exists)
                    statInfo = resolved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dangling or unreadable link target; keep the link's own stat.
            }
        }

        return new FileMetadata
        {
            Length = statInfo.Length,
            LastWriteTimeUtc = statInfo.LastWriteTimeUtc,
            CreationTimeUtc = statInfo.CreationTimeUtc,
            IsHidden = (attrs & FileAttributes.Hidden) != 0,
            IsSystem = (attrs & FileAttributes.System) != 0,
            IsSymlink = isSymlink,
        };
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Stream OpenWrite(string path) =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

    public void Move(string sourcePath, string destPath, bool overwrite) =>
        File.Move(sourcePath, destPath, overwrite);

    public void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) =>
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);

    public void Delete(string path) => File.Delete(path);

    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive);
    }

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
