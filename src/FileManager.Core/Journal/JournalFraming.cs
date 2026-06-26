using System.Buffers.Binary;
using System.IO;
using System.Text;
using FileManager.Core.IO;
using FileManager.Core.Profiles;

namespace FileManager.Core.Journal;

/// <summary>
/// Crash-safe framing for the durable journal. Each record is written as
/// <c>[4-byte big-endian length][4-byte big-endian CRC-32 of the payload][UTF-8 JSON payload]</c>.
/// On read the length and checksum are both validated; the <b>first</b> record whose header is short,
/// whose payload is truncated, or whose CRC does not match the payload is treated as a torn tail
/// (the engine crashed mid-append): decoding STOPS CLEANLY and returns every record parsed before it.
/// A torn tail never throws and never corrupts recovery (§6.3 acceptance criterion).
/// </summary>
public static class JournalFraming
{
    /// <summary>Length of the fixed frame header: a 4-byte length plus a 4-byte CRC-32.</summary>
    public const int HeaderSize = 8;

    /// <summary>
    /// Upper bound on a single record's payload (4 MiB). Journal records are small JSON objects, so any
    /// length prefix above this is a torn/garbage header — the decoder treats it as a torn tail rather
    /// than trusting it and attempting a multi-gigabyte allocation that would OOM the recovery path.
    /// </summary>
    public const int MaxRecordBytes = 4 * 1024 * 1024;

    /// <summary>Encodes <paramref name="record"/> into a single framed byte sequence.</summary>
    public static byte[] Encode(JournalRecord record)
    {
        string json = ProfileSerializer.Serialize(record);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        return Frame(payload);
    }

    /// <summary>Wraps an already-serialized UTF-8 payload in the length+CRC frame.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), Crc32.Compute(payload));
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>
    /// Streams framed records out of <paramref name="stream"/>, validating each frame's length and
    /// CRC-32 and stopping cleanly at the first torn/short/bad-checksum frame. Records whose payload is
    /// well-formed framing but does not deserialize to a current <see cref="JournalRecord"/> are also
    /// treated as the tail boundary (a half-written JSON body). Never throws on a torn tail.
    /// </summary>
    public static IEnumerable<JournalRecord> Decode(Stream stream)
    {
        var header = new byte[HeaderSize];
        while (true)
        {
            if (!ReadExactly(stream, header, HeaderSize))
                yield break; // no (or partial) header left — clean EOF or torn tail.

            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4));
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));

            // A non-positive length, or one above the sanity cap, is a torn/garbage header — not a real
            // record. Stop cleanly rather than trusting the prefix and allocating from it.
            if (length <= 0 || length > MaxRecordBytes)
                yield break;

            byte[] payload = new byte[length];
            if (!ReadExactly(stream, payload, length))
                yield break; // payload truncated by the crash.

            if (Crc32.Compute(payload) != expectedCrc)
                yield break; // checksum mismatch — corrupted/torn frame.

            string json = Encoding.UTF8.GetString(payload);
            if (!ProfileSerializer.TryDeserializeJournalRecord(json, out JournalRecord? record, out _))
                yield break; // well-framed bytes but malformed body — treat as the tail.

            yield return record!;
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/>. Returns false when
    /// the stream ends first (a clean EOF or a crash-truncated tail), leaving the caller to stop.
    /// </summary>
    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n == 0)
                return false;
            read += n;
        }

        return true;
    }
}
