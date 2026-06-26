using System.IO;

namespace FileManager.Core.IO;

/// <summary>
/// <see cref="FileStream"/>-backed <see cref="IDurableAppendWriter"/>: opens the target in
/// <see cref="FileMode.Append"/> (creating its parent directory and the file as needed) and maps
/// <see cref="Flush"/> to <c>FileStream.Flush(flushToDisk: true)</c>, the framework's portable
/// <c>fsync</c>/<c>FlushFileBuffers</c>. Part of the AOT-clean surface; exceptions propagate by design
/// so a failed durable write becomes an observable fact to the journal/audit layer above.
/// </summary>
public sealed class SystemDurableAppendWriter : IDurableAppendWriter
{
    private readonly FileStream _stream;

    /// <summary>Opens (creating parents as needed) <paramref name="path"/> for durable appends.</summary>
    public SystemDurableAppendWriter(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // FileShare.Read lets a recovery reader open the file concurrently (single writer, M4).
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public void Append(ReadOnlySpan<byte> frame) => _stream.Write(frame);

    public void Flush() => _stream.Flush(flushToDisk: true);

    public void Dispose() => _stream.Dispose();
}
