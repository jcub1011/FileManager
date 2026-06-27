using System.Runtime.InteropServices;
using FileManager.Contracts.Messages;
using FileManager.Gui.Ipc;
using FileManager.Gui.Services;

namespace FileManager.Gui.Tests;

/// <summary>A synchronous <see cref="IUiDispatcher"/>: runs the action inline so VM tests need no UI thread.</summary>
internal sealed class SyncDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>A fake <see cref="IFolderBrowser"/>: returns a canned path and records the start hint.</summary>
internal sealed class FakeFolderBrowser : IFolderBrowser
{
    public string? Result { get; set; }
    public string? LastStartingDirectory { get; private set; }
    public int Calls { get; private set; }

    public Task<string?> PickFolderAsync(string? startingDirectory)
    {
        Calls++;
        LastStartingDirectory = startingDirectory;
        return Task.FromResult(Result);
    }
}

/// <summary>A capturing <see cref="INotificationService"/>: records every notification for assertions.</summary>
internal sealed class CapturingNotifier : INotificationService
{
    private readonly List<(string Title, string Message)> _notifications = new();

    public IReadOnlyList<(string Title, string Message)> Notifications => _notifications;

    public void Notify(string title, string message) => _notifications.Add((title, message));
}

/// <summary>
/// A fake <see cref="IServiceClient"/> for view-model tests: canned responses plus a manual event feed
/// (<see cref="PushEvent"/>) so a test can drive the activity view deterministically without IPC.
/// </summary>
internal sealed class FakeServiceClient : IServiceClient
{
    private Action<JobEvent>? _onEvent;
    private Action<ManualInvocationPending>? _onManualPending;
    private TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EngineState? State { get; set; } = new(0, 0, 1, 0, 0, 0, 0);
    public ProfileList? Profiles { get; set; } = new(Array.Empty<ProfileSummary>());
    public ReloadResult? Reload { get; set; } = new(1, Array.Empty<string>());
    public DryRunReport? Report { get; set; }

    /// <summary>The manual-invocation resolutions this client was asked to send (for assertions).</summary>
    public List<ResolveManualInvocation> Resolutions { get; } = new();

    /// <summary>Completes once <see cref="SubscribeAsync"/> has registered the callbacks.</summary>
    public Task Subscribed => _subscribed.Task;

    public Task<EngineState?> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

    public Task<ProfileList?> ListProfilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Profiles);

    public Task<ReloadResult?> ReloadProfilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Reload);

    public Task<DryRunReport?> DryRunAsync(DryRunRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Report);

    public Task<SubmitPayloadResult?> ResolveManualInvocationAsync(
        ResolveManualInvocation resolution, CancellationToken cancellationToken = default)
    {
        Resolutions.Add(resolution);
        return Task.FromResult<SubmitPayloadResult?>(SubmitPayloadResult.Ok(Array.Empty<string>()));
    }

    public Task SubscribeAsync(
        Action<JobEvent> onEvent,
        Action<ManualInvocationPending> onManualPending,
        CancellationToken cancellationToken)
    {
        _onEvent = onEvent;
        _onManualPending = onManualPending;
        _subscribed.TrySetResult();
        var tcs = new TaskCompletionSource();
        cancellationToken.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }

    /// <summary>Pushes a Job event to the subscribed callback (drives <see cref="ViewModels.ActivityViewModel"/>).</summary>
    public void PushEvent(JobEvent jobEvent) => _onEvent?.Invoke(jobEvent);

    /// <summary>Pushes a manual-invocation-pending notification to the subscribed callback.</summary>
    public void PushManualPending(ManualInvocationPending pending) => _onManualPending?.Invoke(pending);
}

/// <summary>Per-OS unique endpoint + cleanup for the in-process IpcClient round-trip tests.</summary>
internal static class TestEndpoints
{
    public static string Unique()
    {
        string id = Guid.NewGuid().ToString("N");
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"filemanager-guitest-{id}"
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fmgui-{id}.sock");
    }

    public static void Cleanup(string endpoint)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            if (System.IO.File.Exists(endpoint))
                System.IO.File.Delete(endpoint);
        }
        catch (System.IO.IOException)
        {
        }
    }
}
