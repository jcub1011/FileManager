namespace FileManager.Core.IO;

/// <summary>
/// A byte-frame append sink with a durability guarantee. <see cref="Append"/> writes one opaque frame
/// to the end of an append-only file; <see cref="Flush"/> forces every appended byte to durable
/// storage (a real <c>fsync</c>/<c>FlushFileBuffers</c>), so a record that has been flushed survives a
/// crash. The journal and audit trail write through this seam and flush per record; the
/// fault-injection test double replaces it to simulate a torn/short write or a missing flush.
/// </summary>
/// <remarks>
/// Distinct from <see cref="IFileOperations.OpenWrite"/>, whose only mode is create/truncate: an
/// append-only durable log needs <c>FileMode.Append</c> plus a flush-to-disk that <c>OpenWrite</c>
/// does not expose. Implementations are single-writer (M4); M5 owns concurrency.
/// </remarks>
public interface IDurableAppendWriter : IDisposable
{
    /// <summary>Appends one opaque, fully-formed frame to the end of the file.</summary>
    public void Append(ReadOnlySpan<byte> frame);

    /// <summary>Forces all appended bytes to durable storage (maps to a real <c>fsync</c>).</summary>
    public void Flush();
}
