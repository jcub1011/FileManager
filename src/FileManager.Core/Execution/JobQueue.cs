using System.Threading.Channels;
using FileManager.Core.Jobs;
using FileManager.Core.Profiles;

namespace FileManager.Core.Execution;

/// <summary>
/// One unit of pending work for the <see cref="WorkerPool"/>: the Profile to run, the source file to
/// ingest, and the ambient <see cref="IngestionContext"/> (clock). Triggers (the watcher and the
/// scheduler) enqueue these; a worker dequeues one and calls <see cref="JobEngine.ProcessFile"/>.
/// </summary>
public sealed record JobRequest
{
    /// <summary>The Profile under which to process the file.</summary>
    public required Profile Profile { get; init; }

    /// <summary>The absolute source file path to ingest.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Ambient inputs (notably the clock) for the run.</summary>
    public required IngestionContext Context { get; init; }
}

/// <summary>
/// A bounded async FIFO queue of pending <see cref="JobRequest"/>s, built on
/// <see cref="System.Threading.Channels"/> (in the BCL, AOT-safe — no new dependency). The bound
/// provides backpressure: once <see cref="Capacity"/> items are queued, an
/// <see cref="EnqueueAsync"/> awaits until a worker drains one, so a fast trigger cannot grow the
/// queue without limit (§11 footprint). <see cref="Complete"/> seals the queue for graceful drain:
/// no further writes are accepted, and readers observe completion once the buffer empties.
/// </summary>
public sealed class JobQueue
{
    private readonly Channel<JobRequest> _channel;

    /// <summary>The maximum number of queued-but-not-yet-started requests.</summary>
    public int Capacity { get; }

    /// <summary>
    /// Creates a bounded queue holding at most <paramref name="capacity"/> pending requests. Writers
    /// past the bound wait (backpressure) rather than dropping work.
    /// </summary>
    public JobQueue(int capacity = 1024)
    {
        Capacity = capacity > 0 ? capacity : 1;
        _channel = Channel.CreateBounded<JobRequest>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Enqueues <paramref name="request"/>, awaiting if the queue is full (backpressure). Throws
    /// <see cref="System.Threading.Channels.ChannelClosedException"/> if the queue was completed.
    /// </summary>
    public ValueTask EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(request, cancellationToken);

    /// <summary>
    /// Tries to enqueue without waiting. Returns false when the queue is full (caller may retry /
    /// apply its own backpressure) or completed.
    /// </summary>
    public bool TryEnqueue(JobRequest request) => _channel.Writer.TryWrite(request);

    /// <summary>
    /// The reader the <see cref="WorkerPool"/> consumes. Exposed so workers can
    /// <see cref="ChannelReader{T}.ReadAllAsync"/> the queue until it is drained and completed.
    /// </summary>
    public ChannelReader<JobRequest> Reader => _channel.Reader;

    /// <summary>
    /// Seals the queue: no further <see cref="EnqueueAsync"/>/<see cref="TryEnqueue"/> succeed, and once
    /// the buffered items are read the reader completes — the signal the pool drains on.
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();
}
