using System.IO;
using FileManager.Core.Journal;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Crash-safe framing tests (§6.3 acceptance): round-trip, and a torn tail record (truncated length,
/// truncated payload, corrupted checksum) is skipped cleanly while earlier records still decode.
/// </summary>
public sealed class JournalFramingTests
{
    private static JournalRecord Rec(string jobId, JournalEventType evt) => new()
    {
        SchemaVersion = JournalRecord.CurrentSchemaVersion,
        Event = evt,
        JobId = jobId,
        ProfileId = "p",
        SourcePath = @"C:\src\a.txt",
        Timestamp = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero),
    };

    private static List<JournalRecord> Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return JournalFraming.Decode(stream).ToList();
    }

    [Fact]
    public void Encode_Decode_RoundTrips()
    {
        JournalRecord original = Rec("job1", JournalEventType.TargetPlaced) with
        {
            FinalPath = @"C:\t\a.txt",
            TempPath = @"C:\t\.tmp",
        };

        List<JournalRecord> decoded = Decode(JournalFraming.Encode(original));

        Assert.Single(decoded);
        Assert.Equal(original, decoded[0]);
    }

    [Fact]
    public void Decode_MultiRecordStream_ReturnsAllInOrder()
    {
        using var ms = new MemoryStream();
        ms.Write(JournalFraming.Encode(Rec("j", JournalEventType.Open)));
        ms.Write(JournalFraming.Encode(Rec("j", JournalEventType.Screened)));
        ms.Write(JournalFraming.Encode(Rec("j", JournalEventType.AllTargetsVerified)));

        List<JournalRecord> decoded = Decode(ms.ToArray());

        Assert.Equal(3, decoded.Count);
        Assert.Equal(JournalEventType.Open, decoded[0].Event);
        Assert.Equal(JournalEventType.Screened, decoded[1].Event);
        Assert.Equal(JournalEventType.AllTargetsVerified, decoded[2].Event);
    }

    [Fact]
    public void Decode_TruncatedHeader_SkipsTornTail_KeepsEarlier()
    {
        byte[] good = JournalFraming.Encode(Rec("j", JournalEventType.Open));
        byte[] partialHeader = { 0x00, 0x00, 0x05 }; // only 3 of the 8 header bytes survived.

        using var ms = new MemoryStream();
        ms.Write(good);
        ms.Write(partialHeader);

        List<JournalRecord> decoded = Decode(ms.ToArray());

        Assert.Single(decoded);
        Assert.Equal(JournalEventType.Open, decoded[0].Event);
    }

    [Fact]
    public void Decode_TruncatedPayload_SkipsTornTail_KeepsEarlier()
    {
        byte[] good = JournalFraming.Encode(Rec("j", JournalEventType.Open));
        byte[] torn = JournalFraming.Encode(Rec("j", JournalEventType.Screened));
        // Drop the last 5 payload bytes of the second frame (crash mid-write).
        byte[] tornPartial = torn[..^5];

        using var ms = new MemoryStream();
        ms.Write(good);
        ms.Write(tornPartial);

        List<JournalRecord> decoded = Decode(ms.ToArray());

        Assert.Single(decoded);
        Assert.Equal(JournalEventType.Open, decoded[0].Event);
    }

    [Fact]
    public void Decode_CorruptedChecksum_SkipsTornTail_KeepsEarlier()
    {
        byte[] good = JournalFraming.Encode(Rec("j", JournalEventType.Open));
        byte[] corrupt = JournalFraming.Encode(Rec("j", JournalEventType.Screened));
        // Flip a payload byte so the CRC no longer matches.
        corrupt[^1] ^= 0xFF;

        using var ms = new MemoryStream();
        ms.Write(good);
        ms.Write(corrupt);

        List<JournalRecord> decoded = Decode(ms.ToArray());

        Assert.Single(decoded);
        Assert.Equal(JournalEventType.Open, decoded[0].Event);
    }

    [Fact]
    public void Decode_LengthPrefixOverCap_TreatedAsTornTail_KeepsEarlier()
    {
        byte[] good = JournalFraming.Encode(Rec("j", JournalEventType.Open));

        // A torn header that encodes a huge positive length (well over the sanity cap) — trusting it
        // would attempt a multi-gigabyte allocation. It must be treated as a torn tail instead.
        var oversizedHeader = new byte[JournalFraming.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            oversizedHeader.AsSpan(0, 4), JournalFraming.MaxRecordBytes + 1);

        using var ms = new MemoryStream();
        ms.Write(good);
        ms.Write(oversizedHeader);
        ms.Write(new byte[1024]); // garbage bytes after the bad header

        List<JournalRecord> decoded = Decode(ms.ToArray());

        Assert.Single(decoded);
        Assert.Equal(JournalEventType.Open, decoded[0].Event);
    }

    [Fact]
    public void Decode_StopsAtFirstCorruption_DropsRecordsAfterIt()
    {
        byte[] good = JournalFraming.Encode(Rec("j", JournalEventType.Open));
        byte[] corrupt = JournalFraming.Encode(Rec("j", JournalEventType.Screened));
        corrupt[JournalFraming.HeaderSize] ^= 0xFF; // corrupt first payload byte
        byte[] afterCorruption = JournalFraming.Encode(Rec("j", JournalEventType.AllTargetsVerified));

        using var ms = new MemoryStream();
        ms.Write(good);
        ms.Write(corrupt);
        ms.Write(afterCorruption); // must NOT be returned — decoding stopped at the corruption.

        List<JournalRecord> decoded = Decode(ms.ToArray());

        Assert.Single(decoded);
        Assert.Equal(JournalEventType.Open, decoded[0].Event);
    }
}
