using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;

namespace FileManager.Core.Metadata;

/// <summary>The outcome of a metadata copy: whether it should fail the Job, plus a warning note.</summary>
public sealed record MetadataResult(bool Ok, string? Warning)
{
    /// <summary>A clean copy with nothing to report.</summary>
    public static MetadataResult Clean { get; } = new(true, null);

    /// <summary>A copy that proceeded but warrants a non-fatal warning (<see cref="MetadataOnConflict.WarnAndContinue"/>).</summary>
    public static MetadataResult Warn(string warning) => new(true, warning);

    /// <summary>A detected loss treated as fatal (<see cref="MetadataOnConflict.FailJob"/>); the caller rolls back.</summary>
    public static MetadataResult Fail(string warning) => new(false, warning);
}

/// <summary>
/// Best-effort metadata preservation for a freshly placed Target copy (§6.4): carries the source's
/// timestamps across, and (where the platform allows) permission bits. When loss/alteration is
/// detectable, <see cref="MetadataOnConflict"/> selects the runtime behavior —
/// <see cref="MetadataOnConflict.WarnAndContinue"/> proceeds with a warning;
/// <see cref="MetadataOnConflict.FailJob"/> reports a failure so the caller rolls back.
/// </summary>
/// <remarks>
/// Preservation is imperfect by design: cross-filesystem mapping (e.g. NTFS→exFAT, or crossing OS
/// permission models) cannot round-trip every attribute. This copier preserves the last-write
/// timestamp reliably and treats permission/ACL transfer as best-effort — the BCL copies basic
/// attributes on write, and a full ACL/mode-bit transfer is left to the OS rather than re-implemented
/// here. The detectable-loss check is conservative: it warns only when it can positively determine a
/// loss before committing.
/// </remarks>
public sealed class MetadataCopier(IFileOperations files)
{
    /// <summary>
    /// Applies <paramref name="sourcePath"/>'s metadata onto <paramref name="destPath"/> under
    /// <paramref name="policy"/>. Timestamp transfer is attempted first (best-effort); a detected,
    /// non-recoverable loss is then resolved per the policy.
    /// </summary>
    public MetadataResult Copy(string sourcePath, string destPath, MetadataOnConflict policy)
    {
        // Carry the source's last-write time onto the copy. The destination filesystem may store it at
        // a coarser resolution (e.g. FAT's 2 s) — that is the kind of imperfection §6.4 accepts.
        string? loss = null;
        try
        {
            FileMetadata source = files.GetMetadata(sourcePath);
            files.SetLastWriteTimeUtc(destPath, source.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The destination filesystem rejected the timestamp — a detectable loss.
            loss = $"could not preserve timestamp: {ex.Message}";
        }

        if (loss is null)
            return MetadataResult.Clean;

        return policy == MetadataOnConflict.FailJob
            ? MetadataResult.Fail(loss)
            : MetadataResult.Warn(loss);
    }
}
