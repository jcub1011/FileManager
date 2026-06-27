using FileManager.Contracts.Messages;
using FileManager.Service.Ipc;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>Covers the event fan-out: subscribers receive published events; dispose unregisters.</summary>
public sealed class EventBroadcasterTests
{
    private static JobEvent Evt(string id) =>
        new(id, "p", "Closed", "COMPLETED", "ok", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Publish_DeliversToAllSubscribers()
    {
        var broadcaster = new EventBroadcaster();
        using EventBroadcaster.Subscription s1 = broadcaster.Subscribe();
        using EventBroadcaster.Subscription s2 = broadcaster.Subscribe();
        Assert.Equal(2, broadcaster.SubscriberCount);

        broadcaster.Publish(Evt("a"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IpcMessage r1 = await s1.Reader.ReadAsync(cts.Token);
        IpcMessage r2 = await s2.Reader.ReadAsync(cts.Token);
        Assert.Equal("a", r1.Event!.JobId);
        Assert.Equal("a", r2.Event!.JobId);
    }

    [Fact]
    public void Dispose_UnregistersSubscriber()
    {
        var broadcaster = new EventBroadcaster();
        EventBroadcaster.Subscription s = broadcaster.Subscribe();
        Assert.Equal(1, broadcaster.SubscriberCount);

        s.Dispose();
        Assert.Equal(0, broadcaster.SubscriberCount);

        // Publishing after all subscribers left is a harmless no-op.
        broadcaster.Publish(Evt("b"));
    }
}
