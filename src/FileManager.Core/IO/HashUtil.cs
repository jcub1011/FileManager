using System.IO;
using System.Security.Cryptography;

namespace FileManager.Core.IO;

/// <summary>
/// Streamed content hashing. SHA256 is computed incrementally over a read stream so large files
/// never sit in memory whole (§11).
/// </summary>
public static class HashUtil
{
    private const int BufferSize = 1 << 20; // 1 MiB

    /// <summary>Computes the lowercase hex SHA256 of a file via <paramref name="files"/>.</summary>
    public static string ComputeSha256(IFileOperations files, string path)
    {
        using Stream stream = files.OpenRead(path);
        return ComputeSha256(stream);
    }

    /// <summary>Computes the lowercase hex SHA256 of a stream, reading it in bounded chunks.</summary>
    public static string ComputeSha256(Stream stream)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer, 0, read);

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
