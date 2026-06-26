using System.IO;

namespace FileManager.Core.IO;

/// <summary>
/// Write-capable file-system abstraction for the Job engine. Distinct from the read-only
/// <see cref="FileManager.Core.FileSystem.IFileSystemService"/> (a listing surface for a future GUI);
/// this is the engine's mutation surface — copy/rename/delete plus the metadata and streaming
/// primitives the lifecycle needs.
/// </summary>
/// <remarks>
/// Unlike the read-only services, implementations here <b>do not</b> swallow I/O exceptions: a failed
/// write is a Job-affecting fact the engine must observe and translate into a failed Job, not hide.
/// </remarks>
public interface IFileOperations
{
    /// <summary>Whether a file exists at <paramref name="path"/>.</summary>
    public bool FileExists(string path);

    /// <summary>Whether a directory exists at <paramref name="path"/>.</summary>
    public bool DirectoryExists(string path);

    /// <summary>Creates the directory (and any missing parents). No-op if it already exists.</summary>
    public void CreateDirectory(string path);

    /// <summary>Reads the stat-level <see cref="FileMetadata"/> snapshot for a file.</summary>
    public FileMetadata GetMetadata(string path);

    /// <summary>Opens a sequential read stream over a file. Caller disposes.</summary>
    public Stream OpenRead(string path);

    /// <summary>
    /// Opens a write stream, creating or truncating the file. Parent directory must exist.
    /// Caller disposes.
    /// </summary>
    public Stream OpenWrite(string path);

    /// <summary>
    /// Moves/renames <paramref name="sourcePath"/> to <paramref name="destPath"/>. When
    /// <paramref name="overwrite"/> is true an existing destination is replaced. Same-volume moves
    /// are atomic; the caller is responsible for keeping source and destination on one volume when
    /// atomicity matters (see <see cref="AtomicFileWriter"/>).
    /// </summary>
    public void Move(string sourcePath, string destPath, bool overwrite);

    /// <summary>Deletes a file. No-op if it is already absent.</summary>
    public void Delete(string path);

    /// <summary>
    /// Deletes a directory. No-op if it is already absent. With <paramref name="recursive"/> the whole
    /// subtree is removed; otherwise the directory must be empty. Used to tear down the per-Job
    /// transformer workspace.
    /// </summary>
    public void DeleteDirectory(string path, bool recursive);

    /// <summary>
    /// Enumerates file paths under <paramref name="directory"/>. Returns empty when the directory
    /// is missing. <paramref name="recursive"/> descends into subdirectories.
    /// </summary>
    public IEnumerable<string> EnumerateFiles(string directory, bool recursive);
}
