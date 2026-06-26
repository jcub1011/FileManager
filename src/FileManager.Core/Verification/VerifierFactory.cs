using FileManager.Core.IO;
using FileManager.Core.Profiles;

namespace FileManager.Core.Verification;

/// <summary>
/// Maps a Profile's <see cref="VerificationMethod"/> to the matching <see cref="IVerifier"/>. Pure
/// selection — the implementations themselves carry the I/O surface.
/// </summary>
public static class VerifierFactory
{
    /// <summary>
    /// The verifier for <paramref name="method"/>. <paramref name="files"/> backs metadata/content
    /// reads; <paramref name="tolerance"/> (when supplied) overrides the
    /// <see cref="SizeTimestampVerifier"/> window.
    /// </summary>
    public static IVerifier Create(VerificationMethod method, IFileOperations files, TimeSpan? tolerance = null) =>
        method switch
        {
            VerificationMethod.SizeTimestamp => new SizeTimestampVerifier(files, tolerance),
            VerificationMethod.SHA256 => new Sha256Verifier(files),
            VerificationMethod.None => new NoneVerifier(),
            _ => new NoneVerifier(),
        };
}
