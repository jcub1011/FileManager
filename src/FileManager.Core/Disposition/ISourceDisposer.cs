using FileManager.Core.Profiles;

namespace FileManager.Core.Disposition;

/// <summary>
/// Phase 6 source disposition (§4): applies a Profile's <see cref="OnSuccess"/> policy to the source
/// file after a successful Job. The M1 implementation (<see cref="SourceDisposer"/>) uses a placeholder
/// local <c>trash/</c> folder for <see cref="OnSuccess.MoveToTrash"/>; the M3 native Recycle Bin /
/// FreeDesktop Trash integration can be substituted behind this contract.
/// </summary>
public interface ISourceDisposer
{
    /// <summary>
    /// Applies the disposition. <paramref name="trashRoot"/> is the placeholder trash folder used for
    /// <see cref="OnSuccess.MoveToTrash"/>; <paramref name="now"/> stamps the trashed name.
    /// </summary>
    public DispositionOutcome Dispose(
        string sourcePath,
        PolicySet policies,
        string trashRoot,
        DateTimeOffset now);
}
