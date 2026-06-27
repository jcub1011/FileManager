using System.Net;
using System.Net.NetworkInformation;
using FileManager.Contracts.Messages;
using FileManager.Service.Ipc;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>
/// Exercises the IPC server over the REAL per-OS transport (named pipe on Windows, Unix socket on
/// Linux): request/response round-trips, the subscribe → event push stream, and the no-network-listener
/// guarantee. All synchronization is signal-based (no fixed sleeps); each test uses a unique endpoint
/// and cleans up its socket so tests never collide.
/// </summary>
public sealed class IpcServerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(15);

    private static IpcServer NewServer(IEngineFacade engine, EventBroadcaster events, string endpoint)
    {
        IIpcServerTransport transport = IpcServer.CreateTransportForCurrentOS(endpoint);
        var dispatcher = new ConnectionDispatcher(engine, events, new CapturingLog());
        var server = new IpcServer(transport, dispatcher, new CapturingLog());
        server.Start();
        return server;
    }

    [Fact]
    public async Task Submit_RoundTrips_AndStateReflectsQueuedJob()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        var engine = new FakeEngine
        {
            OnSubmit = _ => SubmitPayloadResult.Ok(new[] { "p:one" }),
            State = new EngineState(QueuedCount: 1, InFlightCount: 0, WorkerCount: 4, ProfileCount: 1, 0, 0, 0),
        };
        var events = new EventBroadcaster();

        await using IpcServer server = NewServer(engine, events, endpoint);
        try
        {
            await using Stream client = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);

            IpcMessage submitResponse = await IpcTestEndpoints.RequestAsync(
                client, IpcMessage.ForSubmit(new SubmitPayload("/src/a.txt", null, false)), cts.Token);
            Assert.Equal(MessageKind.SubmitResult, submitResponse.Kind);
            Assert.True(submitResponse.SubmitResult!.Accepted);
            Assert.Equal(1, submitResponse.SubmitResult.QueuedCount);
            Assert.Equal("/src/a.txt", engine.LastSubmit!.Path);

            IpcMessage stateResponse = await IpcTestEndpoints.RequestAsync(
                client, IpcMessage.ForStateQuery(), cts.Token);
            Assert.Equal(MessageKind.State, stateResponse.Kind);
            Assert.Equal(1, stateResponse.State!.QueuedCount);
            Assert.Equal(4, stateResponse.State.WorkerCount);
        }
        finally
        {
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task Subscribe_ReceivesPushedJobEvent()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        var engine = new FakeEngine();
        var events = new EventBroadcaster();

        await using IpcServer server = NewServer(engine, events, endpoint);
        try
        {
            await using Stream client = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);

            // Send the Subscribe frame; the connection becomes an event stream.
            byte[] subscribe = ContractsSerializer.SerializeToUtf8Bytes(IpcMessage.ForSubscribe());
            await Framing.WriteMessageAsync(client, subscribe, cts.Token);

            // The server registers the subscription asynchronously; poll the broadcaster (signal-based,
            // no fixed sleep — a short await loop bounded by the generous cancellation token).
            while (events.SubscriberCount == 0)
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, cts.Token);
            }

            var pushed = new JobEvent("p:a", "p", "Closed", "COMPLETED", "done", DateTimeOffset.UtcNow);
            events.Publish(pushed);

            byte[]? frame = await Framing.ReadMessageAsync(client, cts.Token);
            Assert.NotNull(frame);
            Assert.True(ContractsSerializer.TryDeserialize(frame, out IpcMessage? message, out _));
            Assert.Equal(MessageKind.Event, message!.Kind);
            Assert.Equal(pushed, message.Event);
        }
        finally
        {
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task Server_OpensNoNetworkListener()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();

        IPEndPoint[] Before() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        IPEndPoint[] before = Before();

        var engine = new FakeEngine();
        var events = new EventBroadcaster();
        await using IpcServer server = NewServer(engine, events, endpoint);
        try
        {
            // Connect + exchange so the server is fully active before we re-snapshot listeners.
            await using Stream client = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);
            await IpcTestEndpoints.RequestAsync(client, IpcMessage.ForStateQuery(), cts.Token);

            IPEndPoint[] after = Before();
            // No NEW TCP listener was opened by standing up the IPC server (pipe/socket only).
            var newListeners = after.Where(a => !before.Contains(a)).ToArray();
            Assert.Empty(newListeners);
        }
        finally
        {
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task MalformedFrame_ClosesConnection_ServerKeepsRunning()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        var engine = new FakeEngine();
        var events = new EventBroadcaster();

        await using IpcServer server = NewServer(engine, events, endpoint);
        try
        {
            // First connection sends garbage JSON: the server closes only that connection.
            await using (Stream bad = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token))
            {
                await Framing.WriteMessageAsync(bad, "not json"u8.ToArray(), cts.Token);
                // The server closes the connection (read returns null at EOF).
                byte[]? eof = await Framing.ReadMessageAsync(bad, cts.Token);
                Assert.Null(eof);
            }

            // A fresh connection still works — the server survived the bad client.
            await using Stream good = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);
            IpcMessage response = await IpcTestEndpoints.RequestAsync(good, IpcMessage.ForStateQuery(), cts.Token);
            Assert.Equal(MessageKind.State, response.Kind);
        }
        finally
        {
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }
}
