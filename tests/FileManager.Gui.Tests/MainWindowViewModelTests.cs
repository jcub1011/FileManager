using FileManager.Contracts.Messages;
using FileManager.Gui.ViewModels;
using Xunit;

namespace FileManager.Gui.Tests;

/// <summary>
/// Verifies the shell view-model composes the three feature view-models, refreshes engine state, and
/// drives the activity view from the background subscription — all without an Avalonia app or UI thread
/// (a synchronous dispatcher + a fake client with a manual event feed).
/// </summary>
public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task RefreshState_ReflectsEngineState_AndConnectionStatus()
    {
        var client = new FakeServiceClient { State = new EngineState(4, 2, 8, 5, 0, 0, 0) };
        var vm = new MainWindowViewModel(client, new CapturingNotifier(), new SyncDispatcher());

        await vm.RefreshStateCommand.ExecuteAsync(null);

        Assert.NotNull(vm.State);
        Assert.Equal(4, vm.State!.QueuedCount);
        Assert.Equal("Connected", vm.ConnectionStatus);
    }

    [Fact]
    public async Task RefreshState_WhenUnreachable_ReportsDisconnected()
    {
        var client = new FakeServiceClient { State = null };
        var vm = new MainWindowViewModel(client, new CapturingNotifier(), new SyncDispatcher());

        await vm.RefreshStateCommand.ExecuteAsync(null);

        Assert.Null(vm.State);
        Assert.Equal("Service unreachable", vm.ConnectionStatus);
    }

    [Fact]
    public async Task Start_SubscribesAndFeedsActivityView()
    {
        var client = new FakeServiceClient();
        var vm = new MainWindowViewModel(client, new CapturingNotifier(), new SyncDispatcher());

        vm.Start();
        await client.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));

        client.PushEvent(new JobEvent("j", "p", "Closed", "COMPLETED", "done", DateTimeOffset.UnixEpoch));

        Assert.Single(vm.Activity.Jobs);
        vm.Stop();
    }
}
