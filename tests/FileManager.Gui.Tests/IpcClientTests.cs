using FileManager.Contracts.Messages;
using FileManager.Core.Logging;
using FileManager.Gui.Ipc;
using FileManager.Service.Ipc;
using Xunit;

namespace FileManager.Gui.Tests;

/// <summary>
/// Exercises the GUI <see cref="IpcClient"/> against a REAL in-process <see cref="IpcServer"/> over the
/// per-OS transport on a unique endpoint (named pipe / Unix socket) — no separate process, no display
/// server, deterministic via signal-based waits. Covers request/response (state, dry-run) and the
/// event-subscription stream, mirroring the M6 Service test style.
/// </summary>
public sealed class IpcClientTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    // A no-op log sink so the server can be constructed without the durable logging stack.
    private sealed class NullLog : ILogSink
    {
        public void Log(JobLogEntry entry) { }
    }

    // A minimal IEngineFacade returning canned responses for the round-trip assertions.
    private sealed class StubEngine : IEngineFacade
    {
        public EngineState StateValue { get; set; } = new(2, 1, 4, 3, 5, 6, 7);
        public DryRunReport ReportValue { get; set; } = DryRunReport.NotImplemented();

        public SubmitPayloadResult Submit(SubmitPayload payload) => SubmitPayloadResult.Ok(new[] { "j1" });
        public EngineState GetState() => StateValue;
        public ProfileList ListProfiles() => new(Array.Empty<ProfileSummary>());
        public ReloadResult ReloadProfiles() => new(0, Array.Empty<string>());
        public DryRunReport DryRun(DryRunRequest request) => ReportValue;
    }

    private static (IpcServer Server, EventBroadcaster Events) NewServer(string endpoint, IEngineFacade engine)
    {
        IIpcServerTransport transport = IpcServer.CreateTransportForCurrentOS(endpoint);
        var events = new EventBroadcaster();
        var dispatcher = new ConnectionDispatcher(engine, events, new NullLog());
        var server = new IpcServer(transport, dispatcher, new NullLog());
        server.Start();
        return (server, events);
    }

    [Fact]
    public async Task GetState_RoundTrips_OverRealTransport()
    {
        string endpoint = TestEndpoints.Unique();
        var engine = new StubEngine { StateValue = new EngineState(9, 8, 7, 6, 5, 4, 3) };
        (IpcServer server, _) = NewServer(endpoint, engine);
        try
        {
            var client = new IpcClient(new OsIpcClientTransport(endpoint), Generous);
            using var cts = new CancellationTokenSource(Generous);

            EngineState? state = await client.GetStateAsync(cts.Token);

            Assert.NotNull(state);
            Assert.Equal(9, state!.QueuedCount);
            Assert.Equal(8, state.InFlightCount);
        }
        finally
        {
            await server.DisposeAsync();
            TestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task DryRun_RoundTrips_ReturningPopulatedReport()
    {
        string endpoint = TestEndpoints.Unique();
        var report = new DryRunReport
        {
            Implemented = true,
            ProfileId = "p1",
            Matches = new[] { new DryRunMatchDto("/src/a.txt", "a.txt", "Pass") },
            ScreenedOut = Array.Empty<DryRunScreenedOutDto>(),
            Commands = Array.Empty<DryRunCommandDto>(),
            TargetWrites = new[] { new DryRunTargetWriteDto("/src/a.txt", "/dst", "/dst/a.txt", "Written") },
            Deletions = Array.Empty<DryRunDeletionDto>(),
            Dispositions = new[] { new DryRunDispositionDto("/src/a.txt", "KeepSource", null) },
        };
        var engine = new StubEngine { ReportValue = report };
        (IpcServer server, _) = NewServer(endpoint, engine);
        try
        {
            var client = new IpcClient(new OsIpcClientTransport(endpoint), Generous);
            using var cts = new CancellationTokenSource(Generous);

            DryRunReport? received = await client.DryRunAsync(
                new DryRunRequest("/src", "p1", Recursive: true), cts.Token);

            Assert.NotNull(received);
            Assert.True(received!.Implemented);
            Assert.Equal("p1", received.ProfileId);
            Assert.Equal("a.txt", Assert.Single(received.Matches).RelativePath);
            Assert.Equal("Written", Assert.Single(received.TargetWrites).Action);
        }
        finally
        {
            await server.DisposeAsync();
            TestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task Subscribe_ReceivesPushedJobEvent()
    {
        string endpoint = TestEndpoints.Unique();
        var engine = new StubEngine();
        (IpcServer server, EventBroadcaster events) = NewServer(endpoint, engine);
        var received = new TaskCompletionSource<JobEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(Generous);
        try
        {
            var client = new IpcClient(new OsIpcClientTransport(endpoint), Generous, TimeSpan.FromMilliseconds(50));
            Task subscription = Task.Run(
                () => client.SubscribeAsync(e => received.TrySetResult(e), cts.Token), cts.Token);

            // Wait (signal-based) for the subscriber to register on the server, then publish.
            while (events.SubscriberCount == 0)
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, cts.Token);
            }

            var pushed = new JobEvent("j", "p", "Failed", "FAILED", "boom", DateTimeOffset.UnixEpoch);
            events.Publish(pushed);

            JobEvent got = await received.Task.WaitAsync(Generous, cts.Token);
            Assert.Equal(pushed, got);

            cts.Cancel();
            await subscription;
        }
        finally
        {
            await server.DisposeAsync();
            TestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task GetState_WhenServiceUnreachable_ReturnsNull()
    {
        // No server on this endpoint: the client must degrade to null rather than throwing.
        string endpoint = TestEndpoints.Unique();
        var client = new IpcClient(new OsIpcClientTransport(endpoint), TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource(Generous);

        EngineState? state = await client.GetStateAsync(cts.Token);

        Assert.Null(state);
    }
}
