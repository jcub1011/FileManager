namespace FileManager.Core.Filtering;

/// <summary>
/// The outcome of screening one file against a <see cref="FileManager.Core.Profiles.FilterSet"/>.
/// When <see cref="Included"/> is false, <see cref="DecidingFilter"/> names the rule that rejected the
/// file (e.g. <c>"ExcludeGlob"</c>, <c>"MinSizeBytes"</c>) so a <c>SKIPPED</c> log line — and the
/// future dry-run report (§8) — can explain the decision.
/// </summary>
public sealed record FilterDecision(bool Included, string? DecidingFilter, string? Detail)
{
    /// <summary>The file passed all filters.</summary>
    public static FilterDecision Pass { get; } = new(true, null, null);

    /// <summary>The file was rejected by <paramref name="rule"/> (optionally with a <paramref name="detail"/>).</summary>
    public static FilterDecision Reject(string rule, string? detail = null) => new(false, rule, detail);
}
