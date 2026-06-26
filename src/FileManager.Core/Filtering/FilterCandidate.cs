using FileManager.Core.IO;

namespace FileManager.Core.Filtering;

/// <summary>
/// The pre-computed view of one file handed to <see cref="FilterEvaluator"/>. Everything the
/// non-dedupe rules need is here as plain data, so screening logic is unit-testable without touching
/// disk; only <see cref="FullPath"/> (for on-demand dedupe hashing) reaches back to the file system.
/// </summary>
public sealed record FilterCandidate
{
    /// <summary>The file's base name (e.g. <c>track.wav</c>).</summary>
    public required string FileName { get; init; }

    /// <summary>Path relative to the owning Source root (e.g. <c>albums\track.wav</c>).</summary>
    public required string RelativePath { get; init; }

    /// <summary>Subfolder depth below the Source root; 0 = directly in the root.</summary>
    public required int Depth { get; init; }

    /// <summary>Absolute path, used only by content-hash dedupe.</summary>
    public required string FullPath { get; init; }

    /// <summary>The metadata snapshot captured at ingestion.</summary>
    public required FileMetadata Metadata { get; init; }
}
