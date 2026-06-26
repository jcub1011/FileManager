namespace FileManager.Core.Verification;

/// <summary>
/// The no-op verifier (§3.3 <c>VerificationMethod.None</c>): always passes. Permitted for throughput,
/// but pairing it with a source-removing disposition is the one data-losing configuration the
/// <see cref="FileManager.Core.Safety.SafetyAnalyzer"/> flags (§6.1).
/// </summary>
public sealed class NoneVerifier : IVerifier
{
    public VerificationResult Verify(string finalOutputPath, string targetCopyPath) =>
        VerificationResult.Pass;
}
