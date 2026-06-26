namespace FileManager.Core.Profiles;

/// <summary>
/// A single, named, machine-specific automation definition (sources, transformers, targets,
/// policies, filters). The unit a user configures, serialized as one JSON file (spec §5.1).
/// Property names are PascalCase to match the on-disk schema exactly.
/// </summary>
public sealed record Profile
{
    /// <summary>Schema version of this Profile document. Current authority is <c>2</c>.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable unique identifier (GUID) for this Profile.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Human-friendly display name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the engine should act on this Profile.</summary>
    public required bool Active { get; init; }

    /// <summary>How Targets are reconciled with Sources.</summary>
    public required SyncMode SyncMode { get; init; }

    /// <summary>How the source tree is reflected under each Target.</summary>
    public required TargetLayout TargetLayout { get; init; }

    /// <summary>Which triggers may launch Jobs for this Profile.</summary>
    public required TriggerSet Triggers { get; init; }

    /// <summary>Input directories. At least one is required (enforced by the validator).</summary>
    public required IReadOnlyList<SourceSpec> Sources { get; init; }

    /// <summary>Ordered transformer chain. May be null/empty (a pure copy Profile).</summary>
    public IReadOnlyList<TransformerStep>? Transformers { get; init; }

    /// <summary>Output directories. At least one is required (enforced by the validator).</summary>
    public required IReadOnlyList<TargetSpec> Targets { get; init; }

    /// <summary>Conflict / verification / disposition policies.</summary>
    public required PolicySet Policies { get; init; }

    /// <summary>Top-level include/exclude/attribute filters.</summary>
    public required FilterSet Filters { get; init; }

    /// <summary>Logging verbosity and notification settings.</summary>
    public required LoggingSpec Logging { get; init; }
}
