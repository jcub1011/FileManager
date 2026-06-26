using FileManager.Core.IO;

namespace FileManager.Core.Safety;

/// <summary>
/// A single proactive space request: <paramref name="Bytes"/> are needed on the volume containing
/// <paramref name="Path"/>. Zero/negative requests are ignored by the ledger.
/// </summary>
public readonly record struct SpaceRequest(string Path, long Bytes);

/// <summary>
/// The outcome of <see cref="SpaceReservationLedger.TryReserve"/>: whether the reservation was granted,
/// the <see cref="SpaceReservation"/> handle to release it (only when <paramref name="Ok"/>), and a
/// human-readable reason naming the constrained volume when it was refused.
/// </summary>
public sealed record ReservationResult(bool Ok, SpaceReservation? Handle, string? Reason)
{
    /// <summary>A granted reservation carrying its release <paramref name="handle"/>.</summary>
    public static ReservationResult Granted(SpaceReservation handle) => new(true, handle, null);

    /// <summary>A refused reservation carrying <paramref name="reason"/>; nothing was reserved.</summary>
    public static ReservationResult Refused(string reason) => new(false, null, reason);
}

/// <summary>
/// A live reservation against a <see cref="SpaceReservationLedger"/>: it holds the per-volume amounts
/// that were added to the ledger, and on <see cref="Dispose"/> subtracts them again under the ledger's
/// lock. Disposal is idempotent so the engine's <c>finally</c> can release unconditionally even after
/// an explicit release.
/// </summary>
public sealed class SpaceReservation : IDisposable
{
    private readonly SpaceReservationLedger _ledger;
    private readonly IReadOnlyDictionary<string, long> _amounts;
    private int _released;

    internal SpaceReservation(SpaceReservationLedger ledger, IReadOnlyDictionary<string, long> amounts)
    {
        _ledger = ledger;
        _amounts = amounts;
    }

    /// <summary>Releases the reserved bytes back to the ledger. Idempotent and safe under concurrent calls.</summary>
    public void Dispose()
    {
        // Interlocked so a future concurrent double-dispose (M5) can never double-subtract.
        if (Interlocked.Exchange(ref _released, 1) == 1)
            return;
        _ledger.Release(_amounts);
    }
}

/// <summary>
/// A thread-safe, concurrency-aware ledger of reserved-but-not-yet-written bytes per volume, used for
/// the engine's proactive (pre-flight) disk-space checks. Each Job reserves the bytes it is about to
/// write before writing them and releases the reservation when the Job ends; the ledger ensures two
/// Jobs targeting the same volume cannot both pass a "is there room now?" check when only one fits.
/// </summary>
/// <remarks>
/// The engine is single-threaded today (it reserves then releases per Job), but the ledger is built
/// thread-safe and injectable now: M5/M6 will inject one <b>shared</b> ledger across the worker pool so
/// concurrent Jobs see each other's outstanding reservations.
/// <para>
/// Intentionally conservative: a Job's reserved bytes stay counted in <c>_reserved</c> until that Job
/// releases, so during its own write window the same bytes are briefly reflected both as reserved here
/// <em>and</em> as a drop in the live free space the next probe reads. That double-counts only against
/// <em>other</em> Jobs — the safe direction (over-protect, never under-protect). Every
/// <see cref="TryReserve"/> re-probes live free space, so writes by external processes are accounted for.
/// </para>
/// </remarks>
public sealed class SpaceReservationLedger
{
    private readonly IFreeSpaceProbe _probe;
    private readonly long _marginBytes;
    private readonly Dictionary<string, long> _reserved = new();
    private readonly object _gate = new();

    /// <summary>
    /// Creates a ledger over <paramref name="probe"/>. <paramref name="marginBytes"/> is a headroom
    /// kept free on every volume (each volume must satisfy
    /// <c>available - reserved - marginBytes &gt;= required</c>); default 0 reserves no headroom.
    /// </summary>
    public SpaceReservationLedger(IFreeSpaceProbe probe, long marginBytes = 0)
    {
        _probe = probe;
        _marginBytes = marginBytes < 0 ? 0 : marginBytes;
    }

    /// <summary>
    /// Attempts to reserve the requested bytes, all-or-nothing. Aggregates the requests by volume,
    /// re-probes each volume's live free space, then under the lock requires every volume to satisfy
    /// <c>available - reserved - margin &gt;= required</c>. On success the per-volume totals are added
    /// to the ledger and a release <see cref="SpaceReservation"/> is returned; on failure nothing is
    /// reserved and the reason names the first constrained volume (with required / available /
    /// already-reserved figures).
    /// </summary>
    public ReservationResult TryReserve(IEnumerable<SpaceRequest> requests)
    {
        // 1. Aggregate required bytes per volume and record each volume's live available bytes. Probing
        //    happens outside the lock (it is read-only I/O); the lock only guards the ledger mutation.
        var required = new Dictionary<string, long>();
        var available = new Dictionary<string, long>();
        foreach (SpaceRequest request in requests)
        {
            if (request.Bytes <= 0)
                continue;

            VolumeSpace space = _probe.Probe(request.Path);
            required[space.VolumeKey] = (required.TryGetValue(space.VolumeKey, out long acc) ? acc : 0L) + request.Bytes;
            available[space.VolumeKey] = space.AvailableBytes;
        }

        if (required.Count == 0)
            return ReservationResult.Granted(new SpaceReservation(this, new Dictionary<string, long>()));

        lock (_gate)
        {
            // 2. All-or-nothing feasibility check across every volume; make no partial changes.
            foreach ((string volume, long need) in required)
            {
                long have = available[volume];
                long alreadyReserved = _reserved.TryGetValue(volume, out long r) ? r : 0L;

                // long.MaxValue means "unconstrained". Otherwise compute headroom by subtracting in steps
                // that can never overflow or wrap to a false-positive grant: both _marginBytes and
                // alreadyReserved are non-negative, and each subtraction runs only when it stays >= 0.
                long headroom;
                if (have == long.MaxValue)
                    headroom = long.MaxValue;
                else if (_marginBytes >= have)
                    headroom = 0L;
                else
                {
                    long afterMargin = have - _marginBytes;
                    headroom = alreadyReserved >= afterMargin ? 0L : afterMargin - alreadyReserved;
                }

                if (headroom < need)
                {
                    return ReservationResult.Refused(
                        $"volume '{volume}' needs {need} byte(s) but only {headroom} available "
                        + $"(free {have}, already reserved {alreadyReserved}, margin {_marginBytes}).");
                }
            }

            // 3. Commit: add each volume's required bytes to the running reservation.
            foreach ((string volume, long need) in required)
                _reserved[volume] = (_reserved.TryGetValue(volume, out long r) ? r : 0L) + need;
        }

        return ReservationResult.Granted(new SpaceReservation(this, required));
    }

    /// <summary>
    /// Returns the per-volume amounts of a reservation to the ledger. Called by
    /// <see cref="SpaceReservation.Dispose"/>; never reduces a volume below zero.
    /// </summary>
    internal void Release(IReadOnlyDictionary<string, long> amounts)
    {
        if (amounts.Count == 0)
            return;

        lock (_gate)
        {
            foreach ((string volume, long amount) in amounts)
            {
                if (!_reserved.TryGetValue(volume, out long current))
                    continue;

                long updated = current - amount;
                if (updated <= 0)
                    _reserved.Remove(volume);
                else
                    _reserved[volume] = updated;
            }
        }
    }
}
