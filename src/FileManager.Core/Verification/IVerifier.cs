namespace FileManager.Core.Verification;

/// <summary>
/// The result of verifying a Target copy against the Job's final temp output (§3.3): whether the
/// copy is considered faithful, plus a human-readable reason when it is not (for logging/rollback).
/// </summary>
public sealed record VerificationResult(bool Ok, string? Reason)
{
    /// <summary>A passing verification.</summary>
    public static VerificationResult Pass { get; } = new(true, null);

    /// <summary>A failing verification carrying <paramref name="reason"/>.</summary>
    public static VerificationResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Verifies that a Target copy faithfully reproduces the Job's final transformed output before any
/// source cleanup runs (§3.3). Implementations gate source disposition: a failed verification triggers
/// rollback across all Targets. Verification reports a <see cref="VerificationResult"/> rather than
/// throwing — a mismatch is an expected, recoverable outcome, not an I/O fault.
/// </summary>
public interface IVerifier
{
    /// <summary>
    /// Compares <paramref name="targetCopyPath"/> (the file just placed at, or staged for, a Target)
    /// against <paramref name="finalOutputPath"/> (the Job's final temp output that was the copy
    /// source). Returns <see cref="VerificationResult.Pass"/> when the copy is faithful.
    /// </summary>
    public VerificationResult Verify(string finalOutputPath, string targetCopyPath);
}
