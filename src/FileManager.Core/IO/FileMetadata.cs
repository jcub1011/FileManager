namespace FileManager.Core.IO;

/// <summary>
/// An immutable snapshot of a file's stat-level metadata, captured once at ingestion (Phase 1)
/// so the rest of a Job reasons over a stable view rather than re-stat'ing a file that may be
/// changing underneath it. Times are UTC for tolerance-free comparison.
/// </summary>
public sealed record FileMetadata
{
    /// <summary>File length in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>Last-write (modified) time, UTC.</summary>
    public required DateTime LastWriteTimeUtc { get; init; }

    /// <summary>Creation time, UTC.</summary>
    public required DateTime CreationTimeUtc { get; init; }

    /// <summary>Whether the file carries the Hidden attribute.</summary>
    public required bool IsHidden { get; init; }

    /// <summary>Whether the file carries the System attribute.</summary>
    public required bool IsSystem { get; init; }

    /// <summary>Whether the file is a reparse point (symbolic link / junction).</summary>
    public required bool IsSymlink { get; init; }
}
