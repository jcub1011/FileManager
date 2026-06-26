using FileManager.Core.IO;
using FileManager.Core.Profiles;

namespace FileManager.Core.Jobs;

/// <summary>
/// One unit of execution: a single source file flowing through the lifecycle for a given Profile
/// (§3.1, §4). Carries the metadata snapshot taken at ingestion and the policies in force, so the
/// run reasons over a stable view.
/// </summary>
public sealed record Job
{
    /// <summary>Unique identifier assigned at ingestion (Phase 1).</summary>
    public required string JobId { get; init; }

    /// <summary>The Profile driving this Job.</summary>
    public required Profile Profile { get; init; }

    /// <summary>Absolute path of the source file.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Absolute path of the Source root that contained the file.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>The file's metadata snapshot captured at ingestion.</summary>
    public required FileMetadata Metadata { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public required JobState State { get; init; }
}
