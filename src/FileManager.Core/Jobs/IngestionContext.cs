namespace FileManager.Core.Jobs;

/// <summary>
/// Ambient inputs for a single <see cref="JobEngine.ProcessFile"/> run. <see cref="Now"/> is an
/// injectable clock so age-based filters and trash timestamps are deterministic in tests; real
/// triggers (M5) supply the wall clock.
/// </summary>
public sealed record IngestionContext
{
    /// <summary>The reference "now" for age filters and disposition timestamps.</summary>
    public required DateTimeOffset Now { get; init; }
}
