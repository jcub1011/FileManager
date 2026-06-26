namespace FileManager.Core.Profiles;

/// <summary>Per-Profile logging and notification settings (spec §5.1 <c>Logging</c>, §7).</summary>
public sealed record LoggingSpec
{
    /// <summary>How much per-Job detail is written to the persistent log.</summary>
    public required Verbosity Verbosity { get; init; }

    /// <summary>Whether to raise an OS/tray notification when a Job fails.</summary>
    public required bool NotifyOnFailure { get; init; }
}
