namespace FileManager.Core.Profiles;

/// <summary>Conflict / verification / disposition policies for a Profile (spec §5.1 <c>Policies</c>).</summary>
public sealed record PolicySet
{
    /// <summary>What to do when a destination file already exists.</summary>
    public required ConflictResolution ConflictResolution { get; init; }

    /// <summary>Whether overwrites are written directly or staged first.</summary>
    public required OverwriteHandling OverwriteHandling { get; init; }

    /// <summary>How a written copy is verified against its source.</summary>
    public required VerificationMethod VerificationMethod { get; init; }

    /// <summary>Disposition of the source file after a Job succeeds.</summary>
    public required OnSuccess OnSuccess { get; init; }

    /// <summary>
    /// Archive destination. Required when <see cref="OnSuccess"/> is
    /// <see cref="Profiles.OnSuccess.MoveToArchive"/>; null otherwise.
    /// </summary>
    public string? ArchiveFolder { get; init; }

    /// <summary>Behavior when a Job fails (rollback semantics).</summary>
    public required OnFailure OnFailure { get; init; }

    /// <summary>Behavior when destination metadata cannot be matched.</summary>
    public required MetadataOnConflict MetadataOnConflict { get; init; }
}
