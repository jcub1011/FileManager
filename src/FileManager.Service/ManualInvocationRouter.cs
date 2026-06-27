using FileManager.Contracts.Messages;
using FileManager.Core.Profiles;

namespace FileManager.Service;

/// <summary>
/// The service-side engine of the spec §3.2 "always prompt, never auto-run" manual shell invocation
/// flow. A manual <see cref="SubmitPayload"/> (<see cref="SubmitPayload.IsManual"/> = true) is NOT
/// enqueued directly: instead <see cref="Register"/> records a <em>pending</em> invocation (a unique id +
/// the path/recursive + ALL Profiles whose Source owns the path) and the host broadcasts a
/// <see cref="ManualInvocationPending"/> so a subscribed GUI raises the chooser. The user's reply routes
/// through <see cref="Resolve"/>: a chosen Profile id enqueues that Profile's Jobs (via the same
/// <see cref="PayloadQueue"/> path used by every other trigger, so MaxDepth recursion is screened by the
/// engine unchanged); a null id (cancel) discards the pending. Nothing ever runs without an explicit
/// choice, and no manual invocation silently drops — the invariant the milestone enforces.
/// </summary>
/// <remarks>
/// State is guarded by a <see cref="Lock"/>. Abandoned pendings (the GUI closed without choosing) would
/// otherwise leak, so <see cref="Register"/> opportunistically expires entries older than the configured
/// time-to-live and the map is bounded — well past the cap the oldest entries are dropped. Expiry/bounding
/// is best-effort cleanup, not a correctness guarantee for a live prompt.
/// </remarks>
public sealed class ManualInvocationRouter
{
    /// <summary>The default lifetime a pending invocation is retained before it is eligible for expiry.</summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(10);

    /// <summary>The default maximum number of concurrently-pending invocations retained.</summary>
    public const int DefaultMaxPending = 256;

    private readonly PayloadQueue _payloads;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeToLive;
    private readonly int _maxPending;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingInvocation> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a router over <paramref name="payloads"/> (the shared queue every trigger feeds).
    /// <paramref name="clock"/> stamps registration time for expiry; <paramref name="timeToLive"/> and
    /// <paramref name="maxPending"/> bound retention so abandoned prompts cannot leak.
    /// </summary>
    public ManualInvocationRouter(
        PayloadQueue payloads,
        TimeProvider? clock = null,
        TimeSpan? timeToLive = null,
        int? maxPending = null)
    {
        _payloads = payloads;
        _clock = clock ?? TimeProvider.System;
        _timeToLive = timeToLive ?? DefaultTimeToLive;
        _maxPending = maxPending ?? DefaultMaxPending;
    }

    /// <summary>The number of currently-pending (unresolved) manual invocations (diagnostic/tests).</summary>
    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>
    /// Registers a manual <paramref name="payload"/> as a pending invocation: computes ALL Profiles whose
    /// Source owns the path, records it under a fresh id, and returns the
    /// <see cref="ManualInvocationPending"/> the host broadcasts to subscribers. NEVER enqueues — the
    /// only way work runs is a subsequent <see cref="Resolve"/> with a chosen Profile id.
    /// </summary>
    public ManualInvocationPending Register(SubmitPayload payload)
    {
        IReadOnlyList<Profile> matches = _payloads.ResolveMatchingProfiles(payload.Path);
        var summaries = matches
            .Select(p => new ProfileSummary(p.ProfileId, p.Name, p.Active))
            .ToList();

        string id = Guid.NewGuid().ToString("N");
        var entry = new PendingInvocation(id, payload.Path, payload.Recursive, summaries, _clock.GetUtcNow());

        lock (_gate)
        {
            ExpireStale();
            _pending[id] = entry;
        }

        return new ManualInvocationPending(id, payload.Path, payload.Recursive, summaries);
    }

    /// <summary>
    /// Snapshots every currently-unresolved pending invocation as a <see cref="ManualInvocationPending"/>,
    /// oldest first. The IPC layer replays these to a newly-subscribed client so a prompt published before
    /// that client subscribed (e.g. a cold-started GUI) is still delivered — closing the lost-push race.
    /// Expired entries are dropped first so a stale prompt is never replayed.
    /// </summary>
    public IReadOnlyList<ManualInvocationPending> SnapshotUnresolved()
    {
        lock (_gate)
        {
            ExpireStale();
            return _pending.Values
                .OrderBy(p => p.RegisteredAt)
                .Select(p => new ManualInvocationPending(p.Id, p.Path, p.Recursive, p.Matches))
                .ToList();
        }
    }

    /// <summary>
    /// Resolves the pending invocation named by <paramref name="resolution"/>. When
    /// <see cref="ResolveManualInvocation.ChosenProfileId"/> is set, the pending path is enqueued under
    /// that Profile (via <see cref="PayloadQueue.Submit"/>, recursive for a directory) and the result is
    /// returned. When it is null (cancel), the pending is discarded and a rejected result is returned.
    /// An unknown/expired id yields a rejected result. The pending is always removed (one-shot).
    /// </summary>
    public SubmitPayloadResult Resolve(ResolveManualInvocation resolution)
    {
        PendingInvocation? entry;
        lock (_gate)
        {
            if (!_pending.Remove(resolution.InvocationId, out entry))
                return SubmitPayloadResult.Rejected(
                    $"No pending manual invocation '{resolution.InvocationId}' (it may have expired or already been resolved).");
        }

        if (string.IsNullOrWhiteSpace(resolution.ChosenProfileId))
            return SubmitPayloadResult.Rejected("Manual invocation cancelled by the user.");

        // Enqueue under the explicitly chosen Profile only — never auto-pick. The engine screens each
        // enumerated file against the Profile's filters (including MaxDepth) at process time, so folder
        // recursion honors MaxDepth without any duplicate depth logic here.
        return _payloads.Submit(new SubmitPayload(
            entry!.Path, resolution.ChosenProfileId, entry.Recursive, IsManual: false));
    }

    // Drops entries past their TTL, then (if still over the cap) the oldest, so an abandoned prompt or a
    // flood of invocations cannot grow the map without bound. Caller holds _gate.
    private void ExpireStale()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (_pending.Count > 0)
        {
            var expired = _pending
                .Where(kv => now - kv.Value.RegisteredAt > _timeToLive)
                .Select(kv => kv.Key)
                .ToList();
            foreach (string key in expired)
                _pending.Remove(key);
        }

        while (_pending.Count >= _maxPending)
        {
            string oldest = _pending.OrderBy(kv => kv.Value.RegisteredAt).First().Key;
            _pending.Remove(oldest);
        }
    }

    // One unresolved manual invocation awaiting the user's profile choice. Carries the matches snapshot
    // computed at registration so a late subscriber's replay shows the same chooser the live push would.
    private sealed record PendingInvocation(
        string Id,
        string Path,
        bool Recursive,
        IReadOnlyList<ProfileSummary> Matches,
        DateTimeOffset RegisteredAt);
}
