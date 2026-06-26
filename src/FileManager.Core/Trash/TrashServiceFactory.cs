using FileManager.Core.IO;

namespace FileManager.Core.Trash;

/// <summary>
/// Selects the platform <see cref="ITrashService"/>: the Windows Recycle Bin (<c>IFileOperation</c>),
/// the FreeDesktop Trash on Linux, or a local <c>trash/</c> folder fallback on any other platform (and
/// for tests). <paramref name="fallbackRoot"/> is the local folder used by the fallback.
/// </summary>
public static class TrashServiceFactory
{
    /// <summary>
    /// Builds the native trash service for the current OS, falling back to a
    /// <see cref="LocalFolderTrash"/> rooted at <paramref name="fallbackRoot"/>. Each implementation
    /// gets an <see cref="IFreeSpaceProbe"/> (a <see cref="SystemFreeSpaceProbe"/> when
    /// <paramref name="freeSpace"/> is null) and the <paramref name="marginBytes"/> headroom for its
    /// proactive trash-volume free-space check.
    /// </summary>
    public static ITrashService Create(
        IFileOperations files,
        string fallbackRoot,
        IFreeSpaceProbe? freeSpace = null,
        long marginBytes = 0)
    {
        IFreeSpaceProbe probe = freeSpace ?? new SystemFreeSpaceProbe();

        if (OperatingSystem.IsWindows())
            return new WindowsRecycleBin(files, probe, marginBytes);
        if (OperatingSystem.IsLinux())
            return new LinuxTrash(files, probe, trashRoot: null, marginBytes);

        return new LocalFolderTrash(files, probe, fallbackRoot, marginBytes);
    }
}
