using System.Text.Json.Serialization;

namespace FileManager.Core.Profiles;

// All enums below are the authoritative values from spec §5.1 ("Enum authority").
// Each is annotated with the generic, source-generator-friendly JsonStringEnumConverter<T>
// (introduced in .NET 8) so values serialize as their string names without reflection,
// keeping the engine library AOT-clean.

/// <summary>How a Profile reconciles Targets with Sources.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SyncMode>))]
public enum SyncMode
{
    AdditiveArchive,
    Mirror,
}

/// <summary>How the source directory tree is reflected under a Target.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TargetLayout>))]
public enum TargetLayout
{
    PreserveStructure,
    Flatten,
}

/// <summary>What to do when a destination file already exists.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConflictResolution>))]
public enum ConflictResolution
{
    Overwrite,
    OverwriteIfNewer,
    RenameSuffix,
    Skip,
}

/// <summary>Whether overwrites are written directly or staged first.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OverwriteHandling>))]
public enum OverwriteHandling
{
    DirectOverwrite,
    StageOverwrites,
}

/// <summary>How a written copy is verified against its source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerificationMethod>))]
public enum VerificationMethod
{
    SizeTimestamp,
    SHA256,
    None,
}

/// <summary>Disposition of the source file after a Job succeeds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OnSuccess>))]
public enum OnSuccess
{
    KeepSource,
    MoveToTrash,
    MoveToArchive,
    PermanentDelete,
}

/// <summary>Behavior when destination metadata cannot be matched.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataOnConflict>))]
public enum MetadataOnConflict
{
    WarnAndContinue,
    FailJob,
}

/// <summary>
/// Behavior when a Job fails. A single value today; the enum exists as an extension point —
/// <see cref="AbortRestoreAndClean"/> denotes the §3.3 rollback behavior.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OnFailure>))]
public enum OnFailure
{
    AbortRestoreAndClean,
}

/// <summary>How a transformer's <c>Arguments</c> string is interpreted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArgumentMode>))]
public enum ArgumentMode
{
    Literal,
    Shell,
}

/// <summary>Whether a transformer produces a new file or edits in place.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputMode>))]
public enum OutputMode
{
    NewFile,
    InPlace,
}

/// <summary>What to do with scheduled runs missed while the engine was down.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MissedRunPolicy>))]
public enum MissedRunPolicy
{
    CatchUpOnce,
    Skip,
}

/// <summary>Logging verbosity for a Profile (see §7).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Verbosity>))]
public enum Verbosity
{
    FailuresOnly,
    FailuresAndSkips,
    All,
}
