using System.Buffers.Binary;
using System.IO;

namespace FileManager.Contracts.Messages;

/// <summary>
/// The length-prefixed JSON wire framing for the IPC transport (spec §2.1): each message is a
/// <c>[4-byte big-endian length][UTF-8 JSON]</c> frame over a reliable local stream (named pipe / Unix
/// socket). No CRC is carried — the underlying stream is reliable and ordered, unlike the durable
/// on-disk journal which frames against torn writes. An absurd length prefix is rejected against a sane
/// cap (mirroring the journal's <c>MaxRecordBytes</c> guard) so a corrupt/hostile peer cannot drive an
/// unbounded allocation.
/// </summary>
public static class Framing
{
    /// <summary>The 4-byte big-endian length prefix size.</summary>
    public const int LengthPrefixBytes = 4;

    /// <summary>
    /// Maximum accepted frame payload size (16 MiB). IPC messages are small control DTOs; a prefix
    /// claiming more than this is treated as corruption and rejected rather than allocated.
    /// </summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Writes one frame: the 4-byte big-endian length of <paramref name="payload"/> followed by the
    /// payload bytes, then flushes. Throws <see cref="ArgumentOutOfRangeException"/> if the payload
    /// exceeds <see cref="MaxFrameBytes"/> (a programmer error — control DTOs are tiny).
    /// </summary>
    public static async Task WriteMessageAsync(
        Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaxFrameBytes)
            throw new ArgumentOutOfRangeException(
                nameof(payload), $"Frame payload {payload.Length} exceeds the {MaxFrameBytes}-byte cap.");

        byte[] prefix = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);

        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame. Returns the payload bytes, or <c>null</c> on a clean EOF (the peer closed the
    /// connection at a frame boundary). Throws <see cref="InvalidDataException"/> when the prefix is
    /// negative or exceeds <see cref="MaxFrameBytes"/>, or when the stream ends mid-frame (a torn frame).
    /// </summary>
    public static async Task<byte[]?> ReadMessageAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] prefix = new byte[LengthPrefixBytes];
        int prefixRead = await ReadAtLeastAsync(stream, prefix, allowEof: true, cancellationToken)
            .ConfigureAwait(false);
        if (prefixRead == 0)
            return null; // clean EOF at a frame boundary.
        if (prefixRead < LengthPrefixBytes)
            throw new InvalidDataException("Stream ended within the length prefix (torn frame).");

        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"Frame length {length} is out of range (cap {MaxFrameBytes}).");

        if (length == 0)
            return Array.Empty<byte>();

        byte[] payload = new byte[length];
        int payloadRead = await ReadAtLeastAsync(stream, payload, allowEof: false, cancellationToken)
            .ConfigureAwait(false);
        if (payloadRead < length)
            throw new InvalidDataException("Stream ended within the frame payload (torn frame).");

        return payload;
    }

    // Fills `buffer` from the stream, returning the number of bytes read. Stops early only at EOF. When
    // allowEof is true and EOF arrives before any byte is read, returns 0 (clean close at a boundary).
    private static async Task<int> ReadAtLeastAsync(
        Stream stream, byte[] buffer, bool allowEof, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (total == 0 && allowEof)
                    return 0;
                break; // mid-frame EOF — caller treats a short read as a torn frame.
            }

            total += read;
        }

        return total;
    }
}
