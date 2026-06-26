using FileManager.Core.Profiles;

namespace FileManager.Core.Safety;

/// <summary>
/// The risk level of a Profile's data-safety configuration (§6.1). An engine-side concept (not part of
/// the Profile schema), surfaced as a signal for M7's blocking/non-blocking GUI warnings.
/// </summary>
public enum SafetyLevel
{
    /// <summary>No data-losing combination detected.</summary>
    Safe,

    /// <summary>A recoverable risk: <c>VerificationMethod.None</c> with <c>OnSuccess.MoveToTrash</c>.</summary>
    Warning,

    /// <summary>An unrecoverable risk: <c>VerificationMethod.None</c> with <c>OnSuccess.PermanentDelete</c>.</summary>
    Blocking,
}

/// <summary>The assessed safety of a Profile: its <see cref="SafetyLevel"/> plus an explanatory reason.</summary>
public sealed record SafetyAssessment(SafetyLevel Level, string? Reason);

/// <summary>
/// Detects the one data-losing configuration (§6.1): verification disabled together with a
/// source-removing disposition. Detection-only — it produces an assessment for the GUI to act on; the
/// engine does not refuse to run on the strength of it.
/// </summary>
public static class SafetyAnalyzer
{
    /// <summary>Evaluates <paramref name="profile"/>'s policies for the §6.1 combination.</summary>
    public static SafetyAssessment Evaluate(Profile profile)
    {
        PolicySet policies = profile.Policies;
        if (policies.VerificationMethod != VerificationMethod.None)
            return new SafetyAssessment(SafetyLevel.Safe, null);

        return policies.OnSuccess switch
        {
            OnSuccess.PermanentDelete => new SafetyAssessment(
                SafetyLevel.Blocking,
                "VerificationMethod=None with OnSuccess=PermanentDelete can irrecoverably lose data."),
            OnSuccess.MoveToTrash => new SafetyAssessment(
                SafetyLevel.Warning,
                "VerificationMethod=None with OnSuccess=MoveToTrash removes the source without verifying copies (recoverable from Trash)."),
            _ => new SafetyAssessment(SafetyLevel.Safe, null),
        };
    }
}
