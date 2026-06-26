using FileManager.Core.IO;

namespace FileManager.Core.Execution;

/// <summary>
/// An in-process lock keyed by absolute path (§5.4 "same-file collision rule"). A Job acquires locks
/// on its source path AND each Target temp/final path before acting; a second contender for any of
/// those paths waits <b>FIFO</b> rather than racing, so two Jobs never corrupt a shared Target file.
/// </summary>
/// <remarks>
/// <para><b>Deadlock-free within one acquire.</b> A caller acquires its whole key-set in one shot via
/// <see cref="Acquire(System.Collections.Generic.IEnumerable{string})"/>, which sorts the keys into a
/// canonical order before locking them. Because every caller acquires overlapping keys in the same
/// order, the classic lock-ordering deadlock (Job A locks <c>[x,y]</c> while Job B locks
/// <c>[y,x]</c>) cannot occur. The returned handle releases every key on dispose.</para>
/// <para><b>Source/Target disjointness assumption (deadlock note for M6).</b> The
/// <see cref="WorkerPool"/> holds a Job's <em>source</em> path lock for the whole handler, and
/// <see cref="FileManager.Core.Jobs.JobEngine"/> acquires each <em>Target</em> final-path lock nested
/// inside that. Those are two <em>separate</em> <see cref="Acquire(System.Collections.Generic.IEnumerable{string})"/>
/// calls, so the in-one-shot sorted-order guarantee above does not span them. They are deadlock-free
/// only under the assumption that the source path-space and the Target path-space are <b>disjoint
/// across Profiles</b> — i.e. no Profile's Source directory is also another Profile's Target. A
/// configuration that violates this could form a cross-Job hold-and-wait cycle (Job A holds source
/// <c>X</c> and waits for Target <c>Y</c>; Job B holds source <c>Y</c> and waits for Target <c>X</c>).
/// This is an unusual configuration and out of scope for M5; the <b>M6 service host must enforce or
/// revisit it</b> (e.g. by validating Source/Target disjointness across Profiles, or by folding the
/// source key into the engine's single sorted Target-acquire so the whole lock-set is taken atomically).</para>
/// <para><b>Strict FIFO fairness, per key.</b> Each key carries an explicit queue of waiters
/// (<see cref="TaskCompletionSource"/>). The first arrival takes the lock immediately; later arrivals
/// enqueue and are completed <em>in arrival order</em> as the current holder releases — a true
/// first-in-first-out hand-off, not the merely-approximate ordering of <see cref="SemaphoreSlim"/>.</para>
/// <para><b>Keys are normalized.</b> Paths are run through <see cref="PathNormalizer.Normalize"/> and
/// de-duplicated under <see cref="PathNormalizer.Comparison"/>, so two spellings of the same path map
/// to one lock.</para>
/// <para><b>In-process only.</b> This is authoritative only within this engine instance — it is
/// <em>not</em> a cross-machine mutex. Advisory locks over SMB/NFS are unreliable (§5.4), so two
/// separate engine processes writing the same network Target are out of scope here.</para>
/// <para>Thread-safe: <see cref="Acquire(System.Collections.Generic.IEnumerable{string})"/> and
/// <see cref="AcquireAsync"/> may be called concurrently from any thread / worker.</para>
/// </remarks>
public sealed class PathLockManager
{
    // One Entry per normalized key, holding that key's FIFO waiter queue + a ref-count so an idle key's
    // Entry can be removed (keeping the dictionary bounded under high path churn). All mutation of
    // _locks, the ref-counts, and a key's queue/held flag is guarded by _gate.
    private readonly Dictionary<string, Entry> _locks;
    private readonly object _gate = new();

    /// <summary>Creates an empty lock manager.</summary>
    public PathLockManager()
    {
        _locks = new Dictionary<string, Entry>(StringComparerFor(PathNormalizer.Comparison));
    }

    /// <summary>
    /// Synchronously acquires locks on every distinct path in <paramref name="paths"/>, blocking FIFO
    /// per contended key until all are held. Returns a handle that releases them all on dispose. Keys
    /// are normalized, de-duplicated, and locked in a canonical sorted order so crossing key-sets can
    /// never deadlock within this call.
    /// </summary>
    public IDisposable Acquire(IEnumerable<string> paths)
    {
        IReadOnlyList<string> keys = NormalizeKeys(paths);
        foreach (string key in keys)
        {
            // Enqueue this caller for the key (FIFO) and block until it reaches the head and is granted.
            Task wait = Enqueue(key);
            wait.GetAwaiter().GetResult();
        }

        return new Handle(this, keys);
    }

    /// <summary>
    /// Asynchronous counterpart to <see cref="Acquire(System.Collections.Generic.IEnumerable{string})"/>:
    /// awaits each contended key FIFO without blocking a pool thread. Honors
    /// <paramref name="cancellationToken"/> while waiting; any keys already taken are released if the
    /// wait is cancelled, so a cancelled acquire never leaks a partially-held key-set.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(
        IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> keys = NormalizeKeys(paths);

        // Keys [0, granted) are held. The key at index `granted` (when < keys.Count) may have a waiter
        // still queued that must be withdrawn on cancellation so it never silently holds the lock.
        int granted = 0;
        try
        {
            for (int i = 0; i < keys.Count; i++)
            {
                Task wait = Enqueue(keys[i]);
                await WaitWithCancellation(keys[i], wait, cancellationToken).ConfigureAwait(false);
                granted = i + 1;
            }
        }
        catch
        {
            // Roll back cleanly: release the keys we hold (granting each to its next FIFO waiter).
            for (int i = 0; i < granted; i++)
                ReleaseKey(keys[i]);
            throw;
        }

        return new Handle(this, keys);
    }

    /// <summary>Convenience overload for a fixed set of paths.</summary>
    public IDisposable Acquire(params string[] paths) => Acquire((IEnumerable<string>)paths);

    // Awaits `wait` but observes cancellation. If cancelled before the lock is granted, the queued
    // waiter is withdrawn from the key's FIFO queue (and the Entry ref-count returned) so it never
    // later wins the lock without an owner; if it had ALREADY been granted in a race with cancellation,
    // the lock is released to the next waiter so it is not leaked.
    private async Task WaitWithCancellation(string key, Task wait, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await wait.ConfigureAwait(false);
            return;
        }

        var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), cancelTcs))
        {
            Task completed = await Task.WhenAny(wait, cancelTcs.Task).ConfigureAwait(false);
            if (completed == wait)
            {
                await wait.ConfigureAwait(false); // observe the grant (already completed)
                return;
            }
        }

        // Cancellation fired. Try to withdraw the still-queued waiter; if it had already been granted
        // (the grant raced ahead of cancellation), release it so the lock is handed to the next waiter.
        if (!TryWithdraw(key, wait))
        {
            await wait.ConfigureAwait(false);
            ReleaseKey(key);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IReadOnlyList<string> NormalizeKeys(IEnumerable<string> paths)
    {
        // Normalize, de-dupe under the OS path comparison, then sort canonically so every caller
        // acquires shared keys in the same order (deadlock-free within one acquire). Comparison uses the
        // OS path semantics so case-insensitive hosts collapse "A"/"a" and order them consistently.
        var seen = new HashSet<string>(StringComparerFor(PathNormalizer.Comparison));
        var result = new List<string>();
        foreach (string path in paths)
        {
            string key = PathNormalizer.Normalize(path);
            if (seen.Add(key))
                result.Add(key);
        }

        result.Sort((a, b) => string.Compare(a, b, PathNormalizer.Comparison));
        return result;
    }

    // Enqueues the caller for `key` and returns a Task that completes when the lock is granted to it.
    // If the key is currently free, the lock is taken immediately (a completed Task). Otherwise a fresh
    // waiter TCS is appended to the key's FIFO queue and returned uncompleted.
    private Task Enqueue(string key)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out Entry? entry))
            {
                entry = new Entry();
                _locks[key] = entry;
            }

            entry.RefCount++;

            if (!entry.Held)
            {
                entry.Held = true;
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.Waiters.Enqueue(waiter);
            return waiter.Task;
        }
    }

    // Releases `key` held by the current holder: grants it to the next FIFO waiter (completing its TCS)
    // or marks the key free, then drops the holder's ref-count and removes an idle Entry.
    private void ReleaseKey(string key)
    {
        TaskCompletionSource? grant = null;
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out Entry? entry))
                return;

            if (entry.Waiters.Count > 0)
            {
                // Hand the lock straight to the head of the queue (stays Held). Strict FIFO.
                grant = entry.Waiters.Dequeue();
            }
            else
            {
                entry.Held = false;
            }

            entry.RefCount--;
            if (entry.RefCount <= 0 && !entry.Held && entry.Waiters.Count == 0)
                _locks.Remove(key);
        }

        // Complete the grant outside the lock (RunContinuationsAsynchronously keeps it off this stack).
        grant?.TrySetResult();
    }

    // Attempts to remove a not-yet-granted waiter (identified by its Task) from `key`'s FIFO queue,
    // returning its ref-count. Returns false when the waiter is no longer queued (already granted).
    private bool TryWithdraw(string key, Task waiterTask)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out Entry? entry))
                return false;

            // Rebuild the queue without the matching waiter (queues are small — at most the contention
            // depth for one path). If found, the caller never held the lock, so just drop its ref-count.
            bool removed = false;
            int count = entry.Waiters.Count;
            for (int i = 0; i < count; i++)
            {
                TaskCompletionSource w = entry.Waiters.Dequeue();
                if (!removed && w.Task == waiterTask)
                {
                    removed = true;
                    continue;
                }

                entry.Waiters.Enqueue(w);
            }

            if (!removed)
                return false;

            entry.RefCount--;
            if (entry.RefCount <= 0 && !entry.Held && entry.Waiters.Count == 0)
                _locks.Remove(key);
            return true;
        }
    }

    private static StringComparer StringComparerFor(StringComparison comparison) =>
        comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // A single key's FIFO waiter queue, the held flag, and its live-holder/waiter ref-count.
    private sealed class Entry
    {
        public readonly Queue<TaskCompletionSource> Waiters = new();
        public bool Held;
        public int RefCount;
    }

    // The disposable returned to a caller; releases every held key exactly once (granting each to the
    // next FIFO waiter), in reverse acquisition order for symmetry.
    private sealed class Handle : IDisposable
    {
        private readonly PathLockManager _owner;
        private readonly IReadOnlyList<string> _keys;
        private bool _disposed;

        public Handle(PathLockManager owner, IReadOnlyList<string> keys)
        {
            _owner = owner;
            _keys = keys;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            for (int i = _keys.Count - 1; i >= 0; i--)
                _owner.ReleaseKey(_keys[i]);
        }
    }
}
