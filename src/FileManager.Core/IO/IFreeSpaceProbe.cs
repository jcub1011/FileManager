namespace FileManager.Core.IO;

/// <summary>
/// A point-in-time free-space reading for the volume that contains a given path: the resolved
/// <paramref name="VolumeKey"/> (a normalized volume root used as a ledger key) and the bytes
/// currently available for writing on it.
/// </summary>
/// <param name="VolumeKey">
/// A stable, normalized identifier for the volume (its root directory). Two paths on the same volume
/// resolve to the same key, so the reservation ledger can aggregate per volume.
/// </param>
/// <param name="AvailableBytes">
/// Bytes available for writing on the volume. <see cref="long.MaxValue"/> signals "unconstrained" —
/// the volume could not be resolved, so the proactive check must not false-fail (the reactive
/// rollback path still protects against a genuinely full disk).
/// </param>
public readonly record struct VolumeSpace(string VolumeKey, long AvailableBytes);

/// <summary>
/// Reads the free space of the volume containing a path. Mirrors <see cref="IFileOperations"/> as an
/// interface-backed I/O seam so the engine's proactive disk-space checks can be driven with a
/// deterministic fake in tests. Implementations never throw for an unresolvable path: they return an
/// "unconstrained" reading instead (see <see cref="VolumeSpace.AvailableBytes"/>).
/// </summary>
public interface IFreeSpaceProbe
{
    /// <summary>The free-space reading for the volume containing <paramref name="path"/>.</summary>
    public VolumeSpace Probe(string path);
}
