using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.Messages;
using FileManager.Core.FileSystem;
using FileManager.Gui.Ipc;
using FileManager.Gui.Services;

namespace FileManager.Gui.ViewModels;

/// <summary>
/// The shell view-model hosting the three feature views (Profile editor, Activity, Dry-run) as tabs,
/// plus a small engine-state header. It owns the background <see cref="IServiceClient.SubscribeAsync"/>
/// loop that feeds <see cref="ActivityViewModel"/>, and a state-refresh command. A plain POCO: it depends
/// only on the injected client/services and marshals event delivery through <see cref="IUiDispatcher"/>,
/// so it is constructable and exercisable in tests without a UI thread.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceClient _client;
    private readonly IUiDispatcher _dispatcher;
    private readonly IManualInvocationPresenter? _chooserPresenter;
    private CancellationTokenSource? _subscriptionCts;

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _connectionStatus = "Connecting…";
    [ObservableProperty] private EngineState? _state;

    /// <summary>The Profile editor tab.</summary>
    public ProfileEditorViewModel Editor { get; }

    /// <summary>The live activity/error tab.</summary>
    public ActivityViewModel Activity { get; }

    /// <summary>The dry-run preview tab.</summary>
    public DryRunViewModel DryRun { get; }

    /// <summary>Creates the shell over the injected collaborators.</summary>
    public MainWindowViewModel(
        IServiceClient client,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        PathPickerService? pathPicker = null,
        IFolderBrowser? folderBrowser = null,
        IManualInvocationPresenter? chooserPresenter = null)
    {
        _client = client;
        _dispatcher = dispatcher;
        _chooserPresenter = chooserPresenter;
        // The path picker (built on IFileSystemService) is threaded into the editor so Source/Target
        // rows can browse the filesystem — not discarded (FIX 5).
        PathPickerService picker = pathPicker ?? new PathPickerService(new FileSystemService());
        Editor = new ProfileEditorViewModel(client, pathPicker: picker, browser: folderBrowser);
        Activity = new ActivityViewModel(notifications, dispatcher);
        DryRun = new DryRunViewModel(client);
    }

    /// <summary>
    /// Starts the background event subscription (feeding <see cref="Activity"/>) and refreshes the engine
    /// state once. Idempotent — a second call is a no-op while a subscription is active.
    /// </summary>
    public void Start()
    {
        if (_subscriptionCts is not null)
            return;

        _subscriptionCts = new CancellationTokenSource();
        CancellationToken token = _subscriptionCts.Token;
        _ = Task.Run(() => _client.SubscribeAsync(Activity.OnJobEvent, OnManualInvocationPending, token), token);
        _ = RefreshStateAsync();
    }

    /// <summary>
    /// Handles a pushed <see cref="ManualInvocationPending"/> (spec §3.2): raises the always-prompt
    /// chooser (via the injected presenter, marshalled onto the UI thread), awaits the user's pick, and
    /// sends the <see cref="ResolveManualInvocation"/> back — the chosen Profile id, or null on cancel.
    /// No presenter (headless) still answers with a cancel so the service's pending never leaks. Public
    /// so a test can drive it without a real subscription.
    /// </summary>
    public void OnManualInvocationPending(ManualInvocationPending pending) =>
        _dispatcher.Post(() => _ = HandleManualInvocationAsync(pending));

    private async Task HandleManualInvocationAsync(ManualInvocationPending pending)
    {
        string? chosenProfileId = _chooserPresenter is null
            ? null // headless/no chooser: cancel so the pending is discarded, never silently run.
            : await _chooserPresenter.ChooseAsync(pending).ConfigureAwait(true);

        await _client
            .ResolveManualInvocationAsync(new ResolveManualInvocation(pending.InvocationId, chosenProfileId))
            .ConfigureAwait(true);
    }

    /// <summary>Stops the background subscription loop.</summary>
    public void Stop()
    {
        _subscriptionCts?.Cancel();
        _subscriptionCts?.Dispose();
        _subscriptionCts = null;
    }

    /// <summary>Refreshes the engine-state header (queue depth, in-flight, tallies).</summary>
    [RelayCommand]
    public async Task RefreshStateAsync()
    {
        EngineState? state = await _client.GetStateAsync().ConfigureAwait(true);
        State = state;
        ConnectionStatus = state is null ? "Service unreachable" : "Connected";
    }
}
