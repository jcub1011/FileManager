namespace FileManager.Core.Profiles;

/// <summary>
/// Include/exclude/attribute filters applied to candidate files (spec §5.1 <c>Filters</c>).
/// All fields are optional; null means "no constraint of this kind".
/// </summary>
public sealed record FilterSet
{
    /// <summary>Glob patterns a file must match at least one of to be included.</summary>
    public IReadOnlyList<string>? Include { get; init; }

    /// <summary>Glob patterns that exclude a file when matched.</summary>
    public IReadOnlyList<string>? ExcludeGlob { get; init; }

    /// <summary>Regex patterns a file must match at least one of to be included.</summary>
    public IReadOnlyList<string>? IncludeRegex { get; init; }

    /// <summary>Regex patterns that exclude a file when matched.</summary>
    public IReadOnlyList<string>? ExcludeRegex { get; init; }

    /// <summary>Minimum file size in bytes.</summary>
    public long? MinSizeBytes { get; init; }

    /// <summary>Maximum file size in bytes.</summary>
    public long? MaxSizeBytes { get; init; }

    /// <summary>Only files modified within this duration (ISO-8601 / shorthand; parsed in M1).</summary>
    public string? ModifiedWithin { get; init; }

    /// <summary>Only files modified older than this duration.</summary>
    public string? ModifiedOlderThan { get; init; }

    /// <summary>Only files created within this duration.</summary>
    public string? CreatedWithin { get; init; }

    /// <summary>Hidden/system/symlink handling.</summary>
    public AttributeFilter? Attributes { get; init; }

    /// <summary>Maximum recursion depth under a Source. Null means unlimited.</summary>
    public int? MaxDepth { get; init; }

    /// <summary>Skip files whose content hash duplicates one already seen.</summary>
    public bool ContentHashDedupe { get; init; }
}

/// <summary>File-attribute inclusion flags (spec §5.1 <c>Filters.Attributes</c>).</summary>
public sealed record AttributeFilter
{
    /// <summary>Include files marked hidden.</summary>
    public required bool IncludeHidden { get; init; }

    /// <summary>Include files marked system.</summary>
    public required bool IncludeSystem { get; init; }

    /// <summary>Follow symbolic links when enumerating.</summary>
    public required bool FollowSymlinks { get; init; }
}
