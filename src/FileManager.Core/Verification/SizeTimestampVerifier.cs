using FileManager.Core.IO;

namespace FileManager.Core.Verification;

/// <summary>
/// Tier-1 verification (§3.3): a copy is faithful when its size matches the final output exactly and
/// its last-write time is within a tolerance window.
/// </summary>
/// <remarks>
/// Timestamp resolution and preservation vary across filesystems and copy methods — FAT stores
/// 2-second granularity, NTFS 100 ns, ext4 nanoseconds, and network/SMB shares may round or skew — so
/// an exact equality check would produce false failures. The <see cref="_tolerance"/> window
/// (default 2 s, covering FAT granularity) makes this check best-effort by design; callers that need
/// authoritative equality use <see cref="Sha256Verifier"/>.
/// </remarks>
public sealed class SizeTimestampVerifier : IVerifier
{
    /// <summary>Default last-write tolerance: 2 s, covering FAT's 2-second timestamp granularity.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(2);

    private readonly IFileOperations _files;
    private readonly TimeSpan _tolerance;

    /// <summary>
    /// Creates a verifier reading metadata through <paramref name="files"/>. <paramref name="tolerance"/>
    /// is the maximum allowed absolute last-write difference (defaults to <see cref="DefaultTolerance"/>).
    /// </summary>
    public SizeTimestampVerifier(IFileOperations files, TimeSpan? tolerance = null)
    {
        _files = files;
        _tolerance = tolerance ?? DefaultTolerance;
    }

    public VerificationResult Verify(string finalOutputPath, string targetCopyPath)
    {
        FileMetadata expected = _files.GetMetadata(finalOutputPath);
        FileMetadata actual = _files.GetMetadata(targetCopyPath);

        if (expected.Length != actual.Length)
            return VerificationResult.Fail(
                $"size mismatch (expected {expected.Length}, got {actual.Length})");

        TimeSpan delta = (expected.LastWriteTimeUtc - actual.LastWriteTimeUtc).Duration();
        if (delta > _tolerance)
            return VerificationResult.Fail(
                $"last-write time differs by {delta.TotalSeconds:0.###}s (tolerance {_tolerance.TotalSeconds:0.###}s)");

        return VerificationResult.Pass;
    }
}
