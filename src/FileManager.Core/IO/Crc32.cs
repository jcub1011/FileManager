namespace FileManager.Core.IO;

/// <summary>
/// A hand-rolled CRC-32 (IEEE 802.3 / zlib polynomial <c>0xEDB88420</c>) used as the frame integrity
/// check in the durable journal and audit framing. Hand-rolled (rather than <c>System.IO.Hashing</c>)
/// to keep <see cref="FileManager.Core"/> dependency-free and AOT-clean; the table is computed once.
/// </summary>
/// <remarks>
/// CRC-32 is not cryptographic — it detects accidental corruption (a torn/short tail record, a flipped
/// byte), which is exactly what crash-safe framing needs. Tamper-resistance is out of scope.
/// </remarks>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Computes the CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        const uint polynomial = 0xEDB88420u;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? polynomial ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }
}
