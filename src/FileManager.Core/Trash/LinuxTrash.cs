using System.IO;
using System.Text;
using FileManager.Core.IO;

namespace FileManager.Core.Trash;

/// <summary>
/// FreeDesktop Trash Specification soft-delete (§5.3). A trashed file is moved into
/// <c>&lt;trashRoot&gt;/files/</c> and paired with a <c>&lt;trashRoot&gt;/info/&lt;name&gt;.trashinfo</c>
/// metadata file recording its original absolute path (URL-encoded) and the deletion timestamp, so a
/// file manager can restore it. Name collisions in <c>files/</c>/<c>info/</c> are resolved with a
/// <c>(n)</c> suffix on the shared base name.
/// </summary>
/// <remarks>
/// The default <paramref name="trashRoot"/> resolves to the user's home trash
/// (<c>$XDG_DATA_HOME/Trash</c>, falling back to <c>~/.local/share/Trash</c>); tests inject a temp
/// directory. This implementation covers the home-trash case only — per-volume <c>.Trash-$uid</c>
/// directories for files on other mounts are out of scope here.
/// </remarks>
public sealed class LinuxTrash : ITrashService
{
    private readonly IFileOperations _files;
    private readonly IFreeSpaceProbe _freeSpace;
    private readonly long _marginBytes;
    private readonly string _trashRoot;

    public LinuxTrash(IFileOperations files, IFreeSpaceProbe freeSpace, string? trashRoot = null, long marginBytes = 0)
    {
        _files = files;
        _freeSpace = freeSpace;
        _marginBytes = marginBytes;
        _trashRoot = trashRoot ?? DefaultTrashRoot();
    }

    /// <summary>The home trash root: <c>$XDG_DATA_HOME/Trash</c> or <c>~/.local/share/Trash</c>.</summary>
    public static string DefaultTrashRoot()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string dataHome = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(dataHome, "Trash");
    }

    public TrashResult MoveToTrash(string path)
    {
        try
        {
            string original = Path.GetFullPath(path);
            string filesDir = Path.Combine(_trashRoot, "files");
            string infoDir = Path.Combine(_trashRoot, "info");

            // Proactive free-space check against the trash destination volume (<trashRoot>). A
            // cross-volume move into the home trash copies bytes, so a full trash volume fails the
            // move; catch it up front. Trash moves are terminal and quick, so this is a direct probe
            // check rather than ledger-accounted.
            long size = _files.GetMetadata(original).Length;
            long available = _freeSpace.Probe(_trashRoot).AvailableBytes;
            long usable = available == long.MaxValue ? long.MaxValue : Math.Max(0L, available - _marginBytes);
            if (size > usable)
                return TrashResult.Failure($"insufficient space on trash volume {_trashRoot} (need {size}, available {usable}).");

            _files.CreateDirectory(filesDir);
            _files.CreateDirectory(infoDir);

            // Reserve a base name free in BOTH files/ and info/ so the pair stays consistent.
            string baseName = Path.GetFileName(original);
            string chosen = ReserveName(filesDir, infoDir, baseName);

            string destFile = Path.Combine(filesDir, chosen);
            string infoFile = Path.Combine(infoDir, chosen + ".trashinfo");

            // Write the metadata first; if the move then fails, drop the orphaned .trashinfo.
            WriteTrashInfo(infoFile, original);
            try
            {
                _files.Move(original, destFile, overwrite: false);
            }
            catch
            {
                TryDelete(infoFile);
                throw;
            }

            return TrashResult.Success(destFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TrashResult.Failure(ex.Message);
        }
    }

    private string ReserveName(string filesDir, string infoDir, string baseName)
    {
        bool Free(string name) =>
            !_files.FileExists(Path.Combine(filesDir, name))
            && !_files.FileExists(Path.Combine(infoDir, name + ".trashinfo"));

        if (Free(baseName))
            return baseName;

        string stem = Path.GetFileNameWithoutExtension(baseName);
        string ext = Path.GetExtension(baseName);
        int n = 1;
        while (true)
        {
            string candidate = $"{stem} ({n}){ext}";
            if (Free(candidate))
                return candidate;
            n++;
        }
    }

    private void WriteTrashInfo(string infoFile, string originalPath)
    {
        // [Trash Info] / Path=<url-encoded absolute path> / DeletionDate=<local ISO-8601, no offset>.
        string body =
            "[Trash Info]\n"
            + $"Path={EncodePath(originalPath)}\n"
            + $"DeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n";

        using Stream stream = _files.OpenWrite(infoFile);
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// URL-encodes the absolute path per the spec: each path component is percent-encoded, but the
    /// <c>/</c> separators (and a leading <c>/</c>) are preserved.
    /// </summary>
    public static string EncodePath(string absolutePath)
    {
        string[] parts = absolutePath.Split('/');
        for (int i = 0; i < parts.Length; i++)
            parts[i] = Uri.EscapeDataString(parts[i]);
        return string.Join('/', parts);
    }

    private void TryDelete(string path)
    {
        try
        {
            _files.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the orphaned metadata file.
        }
    }
}
