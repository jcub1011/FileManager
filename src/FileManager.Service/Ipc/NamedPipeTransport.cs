using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FileManager.Contracts;

namespace FileManager.Service.Ipc;

/// <summary>
/// The Windows IPC transport over a <see cref="NamedPipeServerStream"/> (spec §2.1). The pipe is scoped
/// to the current user by a <see cref="PipeSecurity"/> ACL that grants full control only to the process
/// owner (and SYSTEM), denying everyone else — the §9 least-privilege requirement, verified on creation.
/// Each <see cref="AcceptAsync"/> hands out a fresh server pipe instance so concurrent clients connect
/// simultaneously (the pipe is created with a multi-instance max).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeServerTransport : IIpcServerTransport
{
    private readonly string _pipeName;

    /// <summary>Creates a server transport bound to <paramref name="pipeName"/> (the bare pipe name).</summary>
    public NamedPipeServerTransport(string pipeName) => _pipeName = pipeName;

    /// <summary>Creates a server transport on the current user's pipe (<see cref="IpcNames.GetWindowsPipeName()"/>).</summary>
    public static NamedPipeServerTransport ForCurrentUser() => new(IpcNames.GetWindowsPipeName());

    /// <inheritdoc/>
    public string Endpoint => $@"\\.\pipe\{_pipeName}";

    /// <inheritdoc/>
    public async Task<IIpcConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        // A fresh server instance per accepted connection: while this one services its client the next
        // AcceptAsync creates another, so up to the OS max instances handle concurrent clients.
        NamedPipeServerStream server = CreateRestrictedServer();
        try
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return new PipeConnection(server);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Builds the ACL granting only the current user (and SYSTEM) full control, then creates the pipe
    // with it. NamedPipeServerStreamAcl.Create applies the security descriptor atomically at creation.
    private NamedPipeServerStream CreateRestrictedServer()
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User
            ?? throw new InvalidOperationException("Could not resolve the current Windows user SID for the pipe ACL.");

        // Grant the current user full control; nothing is granted to anyone else (least privilege, §9).
        security.AddAccessRule(new PipeAccessRule(
            owner, PipeAccessRights.FullControl, AccessControlType.Allow));

        // Allow the local SYSTEM account so a future Windows service host can manage the pipe.
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(
            systemSid, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class PipeConnection(NamedPipeServerStream pipe) : IIpcConnection
    {
        public Stream Stream => pipe;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (pipe.IsConnected)
                    pipe.Disconnect();
            }
            catch (IOException)
            {
                // The client may already be gone; disconnect is best-effort.
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The Windows IPC client transport over a <see cref="NamedPipeClientStream"/>. Connects to the
/// current-user pipe, retrying within the supplied timeout so a just-started service is reached.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeClientTransport : IIpcClientTransport
{
    private readonly string _pipeName;

    /// <summary>Creates a client transport for <paramref name="pipeName"/> (the bare pipe name).</summary>
    public NamedPipeClientTransport(string pipeName) => _pipeName = pipeName;

    /// <summary>Creates a client transport for the current user's pipe.</summary>
    public static NamedPipeClientTransport ForCurrentUser() => new(IpcNames.GetWindowsPipeName());

    /// <inheritdoc/>
    public async Task<IIpcConnection> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        try
        {
            // NamedPipeClientStream.ConnectAsync(timeout) throws TimeoutException if the server pipe
            // does not exist within the window — exactly the "service not up yet" signal the launcher
            // retries/handles.
            await client.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            return new ClientConnection(client);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class ClientConnection(NamedPipeClientStream pipe) : IIpcConnection
    {
        public Stream Stream => pipe;

        public ValueTask DisposeAsync() => pipe.DisposeAsync();
    }
}
