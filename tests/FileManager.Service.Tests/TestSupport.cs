using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FileManager.Contracts.Messages;
using FileManager.Core.Logging;
using FileManager.Service.Ipc;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>A capturing <see cref="ILogSink"/> for asserting service log output deterministically.</summary>
internal sealed class CapturingLog : ILogSink
{
    private readonly List<JobLogEntry> _entries = new();
    private readonly Lock _gate = new();

    public void Log(JobLogEntry entry)
    {
        lock (_gate)
            _entries.Add(entry);
    }

    public IReadOnlyList<JobLogEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }
}

/// <summary>A settable <see cref="TimeProvider"/> for deterministic host ticks (no real wall clock).</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}

/// <summary>
/// A configurable <see cref="IEngineFacade"/> for IPC tests: records the last submit, lets the test
/// drive the returned state, and exposes a <see cref="EventBroadcaster"/> the test publishes through.
/// </summary>
internal sealed class FakeEngine : IEngineFacade
{
    public SubmitPayload? LastSubmit { get; private set; }
    public Func<SubmitPayload, SubmitPayloadResult> OnSubmit { get; set; } =
        _ => SubmitPayloadResult.Ok(new[] { "job-1" });
    public EngineState State { get; set; } = new(0, 0, 1, 0, 0, 0, 0);
    public ProfileList Profiles { get; set; } = new(Array.Empty<ProfileSummary>());
    public ReloadResult Reload { get; set; } = new(0, Array.Empty<string>());

    public SubmitPayloadResult Submit(SubmitPayload payload)
    {
        LastSubmit = payload;
        return OnSubmit(payload);
    }

    public EngineState GetState() => State;

    public ProfileList ListProfiles() => Profiles;

    public ReloadResult ReloadProfiles() => Reload;

    public DryRunReport DryRun(DryRunRequest request) => DryRunReport.NotImplemented();
}

/// <summary>
/// Per-OS endpoint helpers for the IPC tests: a unique endpoint name (so concurrent tests never collide)
/// plus a client connect that uses the real transport for the current OS.
/// </summary>
internal static class IpcTestEndpoints
{
    /// <summary>A unique endpoint name (pipe name on Windows, socket path on Linux) for one test.</summary>
    public static string UniqueEndpoint()
    {
        string id = Guid.NewGuid().ToString("N");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"filemanager-test-{id}";

        // Keep the socket path short (UDS paths are length-limited) and under the temp dir.
        return Path.Combine(Path.GetTempPath(), $"fm-{id}.sock");
    }

    /// <summary>Removes a Unix socket file left behind by a test (no-op on Windows).</summary>
    public static void Cleanup(string endpoint)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            if (File.Exists(endpoint))
                File.Delete(endpoint);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Connects a client over the real per-OS transport to <paramref name="endpoint"/>.</summary>
    public static async Task<Stream> ConnectAsync(
        string endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pipe = new NamedPipeClientStream(
                ".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return pipe;
        }

        var ep = new UnixDomainSocketEndPoint(endpoint);
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(ep, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"socket '{endpoint}' not reachable within {timeout}.");
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Writes a request frame and reads the single response frame back.</summary>
    public static async Task<IpcMessage> RequestAsync(
        Stream stream, IpcMessage request, CancellationToken cancellationToken)
    {
        byte[] payload = ContractsSerializer.SerializeToUtf8Bytes(request);
        await Framing.WriteMessageAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        byte[]? response = await Framing.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
        Assert.NotNull(response);
        Assert.True(ContractsSerializer.TryDeserialize(response, out IpcMessage? message, out string? error), error);
        return message!;
    }
}
