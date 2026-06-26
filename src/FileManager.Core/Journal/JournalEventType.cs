using System.Text.Json.Serialization;

namespace FileManager.Core.Journal;

/// <summary>
/// The lifecycle transition a <see cref="JournalRecord"/> records (§4 / §6.3). Recovery reconstructs a
/// Job's furthest-reached state from the latest event seen for its Job ID. The crucial fact for the
/// "never dispose a source with missing copies" invariant is <see cref="AllTargetsVerified"/>: source
/// disposition is permitted only after that event is durably recorded.
/// </summary>
/// <remarks>
/// Serialized by name (string enum) so the on-disk journal stays human-readable and schema changes are
/// additive. A reader that meets an unknown future event value treats the record as torn/foreign and
/// stops cleanly (see <see cref="JournalFraming"/>).
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<JournalEventType>))]
public enum JournalEventType
{
    /// <summary>Job ingested; metadata snapshot taken (Phase 1, journal OPEN).</summary>
    Open,

    /// <summary>Passed filter screening (Phase 2).</summary>
    Screened,

    /// <summary>Transformer chain completed (Phase 3).</summary>
    Transformed,

    /// <summary>Pre-flight disk space reserved (Phase 3/4 boundary).</summary>
    SpaceReserved,

    /// <summary>A prior Target version was moved into staging before a rename (StageOverwrites).</summary>
    TargetStaged,

    /// <summary>A Target copy was written to a temp and verified, before promotion.</summary>
    TargetVerified,

    /// <summary>A Target temp was atomically promoted into its final path.</summary>
    TargetPlaced,

    /// <summary>Every Target was placed and verified — the gate for source disposition (§6.3).</summary>
    AllTargetsVerified,

    /// <summary>The Job was rolled back (finals removed, staged originals restored).</summary>
    RolledBack,

    /// <summary>The source was disposed (Phase 6).</summary>
    Disposed,

    /// <summary>Terminal marker: the Job is fully resolved and needs no recovery (journal CLOSED).</summary>
    Closed,
}
