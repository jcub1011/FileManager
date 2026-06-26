using FileManager.Core.IO;

namespace FileManager.Core.Verification;

/// <summary>
/// Tier-2 verification (§3.3): a copy is faithful when its full SHA256 content hash equals the final
/// output's. Authoritative but costs a full read of both files. Hashing streams in bounded chunks via
/// <see cref="HashUtil"/>, so file size never drives memory use (§11).
/// </summary>
public sealed class Sha256Verifier(IFileOperations files) : IVerifier
{
    public VerificationResult Verify(string finalOutputPath, string targetCopyPath)
    {
        string expected = HashUtil.ComputeSha256(files, finalOutputPath);
        string actual = HashUtil.ComputeSha256(files, targetCopyPath);

        return string.Equals(expected, actual, StringComparison.Ordinal)
            ? VerificationResult.Pass
            : VerificationResult.Fail($"SHA256 mismatch (expected {expected}, got {actual})");
    }
}
