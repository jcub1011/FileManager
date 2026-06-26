using FileManager.Core.Jobs;

namespace FileManager.Core.Execution;

/// <summary>
/// The single authoritative execution model (§5.4): a bounded async pool that consumes a
/// <see cref="JobQueue"/> and runs at most <c>MaxWorkers</c> Jobs concurrently, <em>regardless of
/// which Profile</em> they belong to. Each Job is handed to a handler — in production
/// <see cref="JobEngine.ProcessFile"/> — that runs the §4 lifecycle. The pool acquires the per-Job
/// source path lock (via the shared <see cref="PathLockManager"/>) before invoking the handler, so two
/// Jobs ingesting the same source serialize FIFO.
/// </summary>
/// <remarks>
/// <para><b>Concurrency bound.</b> Exactly <c>MaxWorkers</c> long-running consumer loops read from the
/// queue, so the number of in-flight handler invocations never exceeds <c>MaxWorkers</c> — the bound
/// is structural, not advisory.</para>
/// <para><b>Backpressure.</b> The bound lives in <see cref="JobQueue"/>: when the queue is full,
/// producers (triggers) await in <see cref="JobQueue.EnqueueAsync"/> until a worker drains one.</para>
/// <para><b>Graceful drain.</b> <see cref="DrainAsync"/> seals the queue (no new work accepted), lets
/// the in-flight and already-queued Jobs finish, and completes observably. <see cref="StopAsync"/>
/// additionally cancels, signalling handlers/lock waits to abandon promptly.</para>
/// <para>The pool owns no <see cref="JobEngine"/> itself — the host (M6) injects the handler and the
/// shared lock manager — keeping this class a pure scheduling primitive.</para>
/// </remarks>
public sealed class WorkerPool : IAsyncDisposable
{
    private readonly JobQueue _queue;
    private readonly PathLockManager _pathLocks;
    private readonly Func<JobRequest, CancellationToken, JobResult> _handler;
    private readonly Action<JobRequest, Exception>? _onError;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;

    /// <summary>The configured pool size: the maximum number of Jobs that may run concurrently.</summary>
    public int MaxWorkers { get; }

    /// <summary>
    /// Creates and immediately starts a pool of <paramref name="maxWorkers"/> consumer loops over
    /// <paramref name="queue"/>. Each dequeued <see cref="JobRequest"/> is run through
    /// <paramref name="handler"/> after acquiring its source path lock on <paramref name="pathLocks"/>.
    /// A handler exception is reported to <paramref name="onError"/> (if supplied) and otherwise
    /// swallowed so one bad Job never tears down the pool.
    /// </summary>
    public WorkerPool(
        JobQueue queue,
        int maxWorkers,
        Func<JobRequest, CancellationToken, JobResult> handler,
        PathLockManager? pathLocks = null,
        Action<JobRequest, Exception>? onError = null)
    {
        _queue = queue;
        _pathLocks = pathLocks ?? new PathLockManager();
        _handler = handler;
        _onError = onError;
        MaxWorkers = maxWorkers > 0 ? maxWorkers : 1;

        _workers = new Task[MaxWorkers];
        for (int i = 0; i < MaxWorkers; i++)
            _workers[i] = Task.Run(() => ConsumeAsync(_cts.Token));
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // ReadAllAsync yields each queued request and completes once the queue is sealed and empty,
            // which is what makes a graceful drain observable. With MaxWorkers readers, at most
            // MaxWorkers handler invocations are ever in flight.
            await foreach (JobRequest request in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await RunOneAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // StopAsync was requested: abandon the remaining queued work promptly.
        }
    }

    private async Task RunOneAsync(JobRequest request, CancellationToken cancellationToken)
    {
        // Acquire the source path lock (§5.4) so two Jobs ingesting the same source file serialize.
        // The engine itself locks each Target final path; together they cover source + Target paths.
        // NOTE: this source acquire and the engine's nested Target acquires are two separate Acquire
        // calls, so they are deadlock-free only under the Source/Target path-space disjointness
        // assumption documented on PathLockManager — an item the M6 host must enforce/revisit.
        IDisposable sourceLock;
        try
        {
            sourceLock = await _pathLocks.AcquireAsync(new[] { request.SourcePath }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // The handler is synchronous (JobEngine.ProcessFile); it already runs on a pool thread here.
            _handler(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(request, ex);
        }
        finally
        {
            sourceLock.Dispose();
        }
    }

    /// <summary>
    /// Graceful drain: seals the queue so no new work is accepted, then awaits the in-flight and
    /// already-queued Jobs to finish. After this completes the pool has processed every accepted Job.
    /// </summary>
    public async Task DrainAsync()
    {
        _queue.Complete();
        await Task.WhenAll(_workers).ConfigureAwait(false);
    }

    /// <summary>
    /// Hard-ish stop: seals the queue, cancels (so handlers/lock waits abandon promptly), and awaits
    /// the workers. In-flight synchronous handlers run to their next cancellation check; queued-but-not-
    /// started work is dropped.
    /// </summary>
    public async Task StopAsync()
    {
        _queue.Complete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Drains gracefully, then disposes the cancellation source.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await DrainAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
