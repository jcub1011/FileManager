using System.Text.Json.Serialization;

namespace FileManager.Core.Audit;

/// <summary>
/// The kind of deletion/removal recorded in the audit trail (§7): every source disposition (matching
/// <see cref="FileManager.Core.Profiles.OnSuccess"/>) plus a Mirror surplus deletion. Serialized by
/// name so the trail stays human-readable.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuditAction>))]
public enum AuditAction
{
    /// <summary>Source kept in place (<see cref="FileManager.Core.Profiles.OnSuccess.KeepSource"/>).</summary>
    KeepSource,

    /// <summary>Source soft-deleted to trash (<see cref="FileManager.Core.Profiles.OnSuccess.MoveToTrash"/>).</summary>
    MoveToTrash,

    /// <summary>Source moved to an archive folder (<see cref="FileManager.Core.Profiles.OnSuccess.MoveToArchive"/>).</summary>
    MoveToArchive,

    /// <summary>Source permanently deleted (<see cref="FileManager.Core.Profiles.OnSuccess.PermanentDelete"/>).</summary>
    PermanentDelete,

    /// <summary>A surplus Target file removed by a Mirror reconciliation (§3.1.1).</summary>
    MirrorDeletion,
}
