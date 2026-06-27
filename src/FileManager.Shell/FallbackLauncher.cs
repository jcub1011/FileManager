using FileManager.Contracts.Messages;

namespace FileManager.Shell;

/// <summary>
/// The shell→service handoff (spec §2): given a Payload, connect to the running service and submit it;
/// if the connection fails (the service is down), start the service, wait (bounded retry) for the
/// endpoint to come up, then submit. IPC is the canonical path — the launch is only a fallback.
/// </summary>
/// <remarks>
/// The two side-effecting steps are injectable seams so tests can drive connect-fail → start → retry →
/// submit against a real in-process IPC server without spawning real processes:
/// <list type="bullet">
/// <item><b>connect</b> — a delegate that returns an open framed <see cref="Stream"/> to the service
/// (defaults to <see cref="ShellIpcClient.ConnectAsync"/>).</item>
/// <item><b>launchService</b> — a delegate that starts the service (defaults to spawning the
/// <c>FileManager.Service</c> process; a test substitutes an in-process server start).</item>
/// </list>
/// </remarks>
public sealed class FallbackLauncher
{
    private readonly Func<TimeSpan, CancellationToken, Task<Stream>> _connect;
    private readonly Func<CancellationToken, Task> _launchService;
    private readonly TimeSpan _initialConnectTimeout;
    private readonly TimeSpan _postLaunchTimeout;

    /// <summary>
    /// Creates a launcher. <paramref name="connect"/> / <paramref name="launchService"/> default to the
    /// real IPC client and the real service-process launcher; tests inject in-process seams. The two
    /// timeouts bound the pre-launch connect attempt and the post-launch wait for the endpoint.
    /// </summary>
    public FallbackLauncher(
        Func<TimeSpan, CancellationToken, Task<Stream>>? connect = null,
        Func<CancellationToken, Task>? launchService = null,
        TimeSpan? initialConnectTimeout = null,
        TimeSpan? postLaunchTimeout = null)
    {
        _connect = connect ?? ShellIpcClient.ConnectAsync;
        _launchService = launchService ?? DefaultLaunchServiceAsync;
        _initialConnectTimeout = initialConnectTimeout ?? TimeSpan.FromMilliseconds(500);
        _postLaunchTimeout = postLaunchTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Submits <paramref name="payload"/>, starting the service first if it is not already up. Returns
    /// the service's <see cref="SubmitPayloadResult"/>.
    /// </summary>
    public async Task<SubmitPayloadResult> SubmitAsync(
        SubmitPayload payload, CancellationToken cancellationToken = default)
    {
        Stream? stream = await TryConnectAsync(_initialConnectTimeout, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            // Service is down: start it, then wait (bounded) for the endpoint to accept connections.
            await _launchService(cancellationToken).ConfigureAwait(false);
            stream = await TryConnectAsync(_postLaunchTimeout, cancellationToken).ConfigureAwait(false);
            if (stream is null)
                return SubmitPayloadResult.Rejected("Service did not become reachable after launch.");
        }

        await using (stream.ConfigureAwait(false))
        {
            return await ExchangeAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    // Attempts one connect within the timeout; returns null (rather than throwing) when the service is
    // simply not up, so the caller can fall back to launching it.
    private async Task<Stream?> TryConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _connect(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException
                                    or System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    // Writes the Submit frame and reads the SubmitResult frame back.
    private static async Task<SubmitPayloadResult> ExchangeAsync(
        Stream stream, SubmitPayload payload, CancellationToken cancellationToken)
    {
        byte[] request = ContractsSerializer.SerializeToUtf8Bytes(IpcMessage.ForSubmit(payload));
        await Framing.WriteMessageAsync(stream, request, cancellationToken).ConfigureAwait(false);

        byte[]? response = await Framing.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
        if (response is null)
            return SubmitPayloadResult.Rejected("Service closed the connection without responding.");

        if (!ContractsSerializer.TryDeserialize(response, out IpcMessage? message, out string? error)
            || message?.SubmitResult is null)
        {
            return SubmitPayloadResult.Rejected($"Malformed service response: {error}");
        }

        return message.SubmitResult;
    }

    // Default real launch: start the sibling FileManager.Service executable, detached. Located next to
    // the shell binary so a deployed bundle finds it without configuration.
    private static Task DefaultLaunchServiceAsync(CancellationToken cancellationToken)
    {
        string dir = AppContext.BaseDirectory;
        string exeName = OperatingSystem.IsWindows() ? "FileManager.Service.exe" : "FileManager.Service";
        string exePath = Path.Combine(dir, exeName);

        var psi = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        System.Diagnostics.Process.Start(psi);
        return Task.CompletedTask;
    }
}
