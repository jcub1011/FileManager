using System.Threading.Channels;
using FileManager.Contracts.Messages;

namespace FileManager.Service.Ipc;

/// <summary>
/// A thread-safe fan-out of <see cref="JobEvent"/>s to the set of currently-subscribed connections. A
/// connection that issues <c>Subscribe</c> registers a <see cref="Subscription"/>; the engine calls
/// <see cref="Publish"/> for each event and every live subscriber's bounded channel receives a copy. A
/// slow/dead subscriber never blocks the publisher: its channel drops the oldest event under
/// backpressure (event delivery is best-effort telemetry, not durable state — the journal owns
/// durability).
/// </summary>
public sealed class EventBroadcaster
{
    private readonly HashSet<Subscription> _subscribers = new();
    private readonly Lock _gate = new();

    /// <summary>The number of currently-registered subscribers (diagnostic/tests).</summary>
    public int SubscriberCount
    {
        get { lock (_gate) return _subscribers.Count; }
    }

    /// <summary>
    /// Registers a new subscriber and returns its <see cref="Subscription"/>. The caller reads events
    /// from <see cref="Subscription.Reader"/> and disposes it to unregister when the connection closes.
    /// </summary>
    public Subscription Subscribe()
    {
        var subscription = new Subscription(this);
        lock (_gate)
            _subscribers.Add(subscription);
        return subscription;
    }

    /// <summary>Fans <paramref name="jobEvent"/> out to every live subscriber (best-effort, non-blocking).</summary>
    public void Publish(JobEvent jobEvent)
    {
        Subscription[] snapshot;
        lock (_gate)
            snapshot = _subscribers.ToArray();

        foreach (Subscription subscription in snapshot)
            subscription.Offer(jobEvent);
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
            _subscribers.Remove(subscription);
    }

    /// <summary>
    /// One connection's event subscription: a bounded channel whose <see cref="Reader"/> the dispatcher
    /// drains onto the wire. Dropping the oldest under backpressure keeps a slow client from stalling
    /// the publisher.
    /// </summary>
    public sealed class Subscription : IDisposable
    {
        private readonly EventBroadcaster _owner;
        private readonly Channel<JobEvent> _channel;
        private int _disposed;

        internal Subscription(EventBroadcaster owner)
        {
            _owner = owner;
            _channel = Channel.CreateBounded<JobEvent>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        /// <summary>The reader the dispatcher awaits to stream events to this connection.</summary>
        public ChannelReader<JobEvent> Reader => _channel.Reader;

        internal void Offer(JobEvent jobEvent) => _channel.Writer.TryWrite(jobEvent);

        /// <summary>Unregisters this subscription and completes its reader so the streaming loop ends.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            _owner.Remove(this);
            _channel.Writer.TryComplete();
        }
    }
}
