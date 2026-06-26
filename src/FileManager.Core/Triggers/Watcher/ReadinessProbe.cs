using System.IO;
using System.Runtime.InteropServices;

namespace FileManager.Core.Triggers.Watcher;

/// <summary>The outcome of a single readiness probe (§3.2.1 part (b)).</summary>
/// <param name="Ready">Whether the file passed the readiness check and may be ingested.</param>
/// <param name="Reason">A short human-readable reason when not ready (or a caveat note).</param>
/// <param name="NetworkCaveat">
/// True when the probe relaxed to size-stability only because the path looks like a network Source
/// (the per-Job caveat the watcher logs).
/// </param>
public sealed record ReadinessResult(bool Ready, string? Reason, bool NetworkCaveat);

/// <summary>
/// The readiness check (§3.2.1 part (b)) — distinct from the settle debounce (part (a), owned by the
/// watcher). A file is ready only when this probe succeeds <em>in addition to</em> the debounce.
/// </summary>
/// <remarks>
/// Per-OS strategy:
/// <list type="bullet">
/// <item><b>Windows:</b> attempt an exclusive open (deny-share read/write). If another process still
/// holds the file open for writing, the open fails and the file is not ready.</item>
/// <item><b>Linux/Unix:</b> exclusive-open semantics are weaker, so readiness requires the file is not
/// advisory-locked <em>and</em> its size is stable across two samples
/// <c>StabilityIntervalMs</c> apart.</item>
/// <item><b>Network Sources (UNC / non-fixed drive):</b> exclusive-open and advisory-lock checks are
/// unreliable over SMB/NFS, so the probe relaxes to size-stability only and flags
/// <see cref="ReadinessResult.NetworkCaveat"/> so the caller logs the per-Job caveat.</item>
/// </list>
/// The actual filesystem operations go through an injected <see cref="IReadinessFileProbe"/> seam so
/// tests can simulate a locked / still-growing / network file without touching the real filesystem.
/// </remarks>
public sealed class ReadinessProbe
{
    private readonly IReadinessFileProbe _fileProbe;
    private readonly bool _isWindows;

    /// <summary>
    /// Creates a probe over <paramref name="fileProbe"/> (defaults to the real
    /// <see cref="SystemReadinessFileProbe"/>). <paramref name="treatAsWindows"/> overrides the OS
    /// detection so a test can exercise either platform path; null uses the host OS.
    /// </summary>
    public ReadinessProbe(IReadinessFileProbe? fileProbe = null, bool? treatAsWindows = null)
    {
        _fileProbe = fileProbe ?? new SystemReadinessFileProbe();
        _isWindows = treatAsWindows ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    /// <summary>
    /// Probes <paramref name="path"/> for readiness, sampling size <paramref name="stabilityIntervalMs"/>
    /// apart where size-stability applies. The probe is the (b) half of §3.2.1; the caller is
    /// responsible for the (a) settle debounce before calling.
    /// </summary>
    public ReadinessResult Probe(string path, int stabilityIntervalMs)
    {
        bool network = _fileProbe.IsNetworkPath(path);

        // Network Sources: size-stability only (exclusive-open / advisory-lock unreliable over SMB/NFS).
        if (network)
        {
            ReadinessResult stable = CheckSizeStable(path, stabilityIntervalMs);
            return stable with { NetworkCaveat = true, Reason = stable.Reason ?? "network Source: size-stability only" };
        }

        if (_isWindows)
        {
            // Windows: an exclusive-open success means no other process holds a write handle.
            return _fileProbe.TryOpenExclusive(path)
                ? new ReadinessResult(true, null, false)
                : new ReadinessResult(false, "file is still open for writing (exclusive open failed)", false);
        }

        // Linux/Unix: not advisory-locked AND size stable across two samples.
        if (_fileProbe.IsAdvisoryLocked(path))
            return new ReadinessResult(false, "file is advisory-locked by another process", false);

        return CheckSizeStable(path, stabilityIntervalMs);
    }

    private ReadinessResult CheckSizeStable(string path, int stabilityIntervalMs)
    {
        long first = _fileProbe.GetSize(path);
        _fileProbe.Wait(stabilityIntervalMs);
        long second = _fileProbe.GetSize(path);

        if (first < 0 || second < 0)
            return new ReadinessResult(false, "file is not accessible", false);

        return first == second
            ? new ReadinessResult(true, null, false)
            : new ReadinessResult(false, $"size still changing ({first} → {second} bytes)", false);
    }
}

/// <summary>
/// The low-level filesystem operations <see cref="ReadinessProbe"/> needs, behind a seam so tests can
/// drive synthetic locked / growing / network files without the real filesystem.
/// </summary>
public interface IReadinessFileProbe
{
    /// <summary>Whether <paramref name="path"/> looks like a network path (UNC or a non-fixed drive).</summary>
    public bool IsNetworkPath(string path);

    /// <summary>Attempts an exclusive (deny-all-share) open; true on success (no other writer).</summary>
    public bool TryOpenExclusive(string path);

    /// <summary>Whether an advisory lock is held on the file by another process (Unix).</summary>
    public bool IsAdvisoryLocked(string path);

    /// <summary>The file's size in bytes, or a negative value when it cannot be read.</summary>
    public long GetSize(string path);

    /// <summary>Waits the stability sampling interval (real time in production; virtual in tests).</summary>
    public void Wait(int milliseconds);
}

/// <summary>
/// The production <see cref="IReadinessFileProbe"/> over <see cref="System.IO"/> and the OS. Network
/// detection is best-effort: a UNC path (<c>\\server\share</c>) or a drive whose
/// <see cref="DriveInfo.DriveType"/> is <see cref="DriveType.Network"/> is treated as network; anything
/// else (including unresolvable drives) is treated as local. Advisory-lock detection on Unix is
/// approximated by an exclusive-open attempt (a held write lock surfaces as a sharing/IO failure);
/// this is documented as best-effort, matching the spec's "not advisory-locked" intent.
/// </summary>
public sealed class SystemReadinessFileProbe : IReadinessFileProbe
{
    public bool IsNetworkPath(string path)
    {
        try
        {
            string full = System.IO.Path.GetFullPath(path);

            // UNC: \\server\share\...
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
                return true;

            string? root = System.IO.Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return false;

            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best-effort: if we cannot classify the path, treat it as local (the stricter check).
            return false;
        }
    }

    public bool TryOpenExclusive(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool IsAdvisoryLocked(string path) => !TryOpenExclusive(path);

    public long GetSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    public void Wait(int milliseconds)
    {
        if (milliseconds > 0)
            Thread.Sleep(milliseconds);
    }
}
