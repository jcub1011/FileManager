using FileManager.Contracts.Messages;
using FileManager.Service.Ipc;
using FileManager.Shell;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>
/// Verifies the shell→service handoff: with the service "down", the launcher's connect fails, it invokes
/// its launch seam (which starts a real in-process IPC server — no spawned process), then retries the
/// connect and submits successfully. The launch seam and connect seam are injected so the whole
/// connect-fail → start → retry → submit path runs deterministically in-process.
/// </summary>
public sealed class FallbackLauncherTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ServiceDown_LaunchesThenSubmits()
    {
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        var engine = new FakeEngine { OnSubmit = _ => SubmitPayloadResult.Ok(new[] { "p:queued" }) };
        var events = new EventBroadcaster();

        IpcServer? server = null;
        int launchCount = 0;

        // The launch seam stands up the in-process server (the moral equivalent of starting the process).
        Task Launch(CancellationToken ct)
        {
            Interlocked.Increment(ref launchCount);
            IIpcServerTransport transport = IpcServer.CreateTransportForCurrentOS(endpoint);
            var dispatcher = new ConnectionDispatcher(engine, events, new CapturingLog());
            server = new IpcServer(transport, dispatcher, new CapturingLog());
            server.Start();
            return Task.CompletedTask;
        }

        // The connect seam uses the real per-OS client transport against our unique endpoint.
        Task<Stream> Connect(TimeSpan timeout, CancellationToken ct) =>
            IpcTestEndpoints.ConnectAsync(endpoint, timeout, ct);

        try
        {
            var launcher = new FallbackLauncher(
                connect: Connect,
                launchService: Launch,
                // A short initial timeout so the first (pre-launch) connect fails fast, proving the
                // fallback path; a generous post-launch timeout so the retry reliably succeeds.
                initialConnectTimeout: TimeSpan.FromMilliseconds(150),
                postLaunchTimeout: Generous);

            SubmitPayloadResult result = await launcher.SubmitAsync(
                new SubmitPayload("/src/a.txt", null, false));

            Assert.True(result.Accepted);
            Assert.Equal(1, result.QueuedCount);
            Assert.Equal(1, launchCount); // the service was started exactly once.
            Assert.Equal("/src/a.txt", engine.LastSubmit!.Path);
        }
        finally
        {
            if (server is not null)
                await server.DisposeAsync();
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task ServiceUp_SubmitsWithoutLaunching()
    {
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        var engine = new FakeEngine { OnSubmit = _ => SubmitPayloadResult.Ok(new[] { "p:queued" }) };
        var events = new EventBroadcaster();

        IIpcServerTransport transport = IpcServer.CreateTransportForCurrentOS(endpoint);
        var dispatcher = new ConnectionDispatcher(engine, events, new CapturingLog());
        var server = new IpcServer(transport, dispatcher, new CapturingLog());
        server.Start();

        int launchCount = 0;
        Task Launch(CancellationToken ct)
        {
            Interlocked.Increment(ref launchCount);
            return Task.CompletedTask;
        }

        try
        {
            var launcher = new FallbackLauncher(
                connect: (timeout, ct) => IpcTestEndpoints.ConnectAsync(endpoint, timeout, ct),
                launchService: Launch,
                initialConnectTimeout: Generous,
                postLaunchTimeout: Generous);

            SubmitPayloadResult result = await launcher.SubmitAsync(new SubmitPayload("/src/b.txt", "p1", true));

            Assert.True(result.Accepted);
            Assert.Equal(0, launchCount); // already up: never launched.
        }
        finally
        {
            await server.DisposeAsync();
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }
}
