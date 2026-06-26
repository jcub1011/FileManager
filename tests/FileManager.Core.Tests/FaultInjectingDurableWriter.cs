using System.IO;
using FileManager.Core.IO;

namespace FileManager.Core.Tests;

/// <summary>
/// An <see cref="IDurableAppendWriter"/> test double over an in-memory buffer that can simulate a
/// crash mid-append (truncating the byte stream at a configured offset or after a configured number of
/// frames) and counts <see cref="Flush"/> calls — so a test can verify fsync-per-record and that a
/// torn tail is skipped cleanly. Writes go to a backing <see cref="MemoryStream"/> the test can inspect.
/// </summary>
internal sealed class FaultInjectingDurableWriter : IDurableAppendWriter
{
    private readonly MemoryStream _buffer;
    private readonly int? _truncateAtBytes;
    private readonly int? _failAfterFrames;

    private int _framesAppended;

    public FaultInjectingDurableWriter(
        MemoryStream? buffer = null,
        int? truncateAtBytes = null,
        int? failAfterFrames = null)
    {
        _buffer = buffer ?? new MemoryStream();
        _truncateAtBytes = truncateAtBytes;
        _failAfterFrames = failAfterFrames;
    }

    /// <summary>Number of times <see cref="Flush"/> was called (one per durably-committed record).</summary>
    public int FlushCount { get; private set; }

    /// <summary>The bytes appended so far (after any simulated truncation).</summary>
    public byte[] Bytes => _buffer.ToArray();

    /// <summary>A fresh reader over the appended bytes (for framing decode in tests).</summary>
    public Stream OpenReader() => new MemoryStream(_buffer.ToArray(), writable: false);

    public void Append(ReadOnlySpan<byte> frame)
    {
        _framesAppended++;

        // Simulate a crash that truncated the byte stream partway through a frame.
        if (_truncateAtBytes is { } limit)
        {
            long remaining = limit - _buffer.Length;
            if (remaining <= 0)
                return;

            int take = (int)Math.Min(remaining, frame.Length);
            _buffer.Write(frame[..take]);
            return;
        }

        _buffer.Write(frame);

        if (_failAfterFrames is { } max && _framesAppended >= max)
            throw new IOException($"Simulated crash after {max} frame(s).");
    }

    public void Flush() => FlushCount++;

    public void Dispose() { }
}
