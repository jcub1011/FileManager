namespace FileManager.Core.Profiles;

/// <summary>A configured output directory belonging to a Profile (spec §5.1 <c>Targets[]</c>).</summary>
public sealed record TargetSpec
{
    /// <summary>Absolute path of the target directory. Stored verbatim; normalization is M1.</summary>
    public required string Path { get; init; }
}
