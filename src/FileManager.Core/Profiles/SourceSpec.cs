namespace FileManager.Core.Profiles;

/// <summary>A configured input directory belonging to a Profile (spec §5.1 <c>Sources[]</c>).</summary>
public sealed record SourceSpec
{
    /// <summary>Absolute path of the source directory. Stored verbatim; normalization is M1.</summary>
    public required string Path { get; init; }

    /// <summary>Seconds to wait after a file appears before considering it for processing.</summary>
    public int SettleDelaySeconds { get; init; }

    /// <summary>Polling interval (ms) used to confirm a file's size has stabilized.</summary>
    public int StabilityIntervalMs { get; init; }

    /// <summary>Optional per-source filter overrides. Null inherits the Profile-level filters.</summary>
    public FilterSet? Filters { get; init; }
}
