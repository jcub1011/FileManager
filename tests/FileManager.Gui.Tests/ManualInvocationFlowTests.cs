using FileManager.Contracts.Messages;
using FileManager.Gui.Services;
using FileManager.Gui.ViewModels;
using Xunit;

namespace FileManager.Gui.Tests;

/// <summary>
/// Verifies the GUI's always-prompt handshake (§3.2) in <see cref="MainWindowViewModel"/>: a pushed
/// <see cref="ManualInvocationPending"/> raises the chooser (via the injected presenter) and the user's
/// choice is relayed back as a <see cref="ResolveManualInvocation"/>; cancel relays null; and the
/// no-presenter (headless) path resolves with a cancel so the service's pending never leaks. No display
/// server — a fake presenter stands in for the dialog.
/// </summary>
public sealed class ManualInvocationFlowTests
{
    private sealed class FakePresenter : IManualInvocationPresenter
    {
        public string? Choice { get; set; }
        public ManualInvocationPending? LastPending { get; private set; }

        public Task<string?> ChooseAsync(ManualInvocationPending pending)
        {
            LastPending = pending;
            return Task.FromResult(Choice);
        }
    }

    private static ManualInvocationPending Pending() =>
        new("inv-9", "/x", false, new[] { new ProfileSummary("p1", "One", true) });

    [Fact]
    public async Task Pending_RaisesChooser_AndRelaysChosenProfile()
    {
        var client = new FakeServiceClient();
        var presenter = new FakePresenter { Choice = "p1" };
        var vm = new MainWindowViewModel(
            client, new CapturingNotifier(), new SyncDispatcher(), chooserPresenter: presenter);

        vm.OnManualInvocationPending(Pending());
        await WaitForResolution(client);

        Assert.NotNull(presenter.LastPending);
        Assert.Single(client.Resolutions);
        Assert.Equal("inv-9", client.Resolutions[0].InvocationId);
        Assert.Equal("p1", client.Resolutions[0].ChosenProfileId);
    }

    [Fact]
    public async Task Cancel_RelaysNull()
    {
        var client = new FakeServiceClient();
        var presenter = new FakePresenter { Choice = null };
        var vm = new MainWindowViewModel(
            client, new CapturingNotifier(), new SyncDispatcher(), chooserPresenter: presenter);

        vm.OnManualInvocationPending(Pending());
        await WaitForResolution(client);

        Assert.Single(client.Resolutions);
        Assert.Null(client.Resolutions[0].ChosenProfileId);
    }

    [Fact]
    public async Task NoPresenter_ResolvesWithCancel_SoPendingNeverLeaks()
    {
        var client = new FakeServiceClient();
        var vm = new MainWindowViewModel(
            client, new CapturingNotifier(), new SyncDispatcher(), chooserPresenter: null);

        vm.OnManualInvocationPending(Pending());
        await WaitForResolution(client);

        Assert.Single(client.Resolutions);
        Assert.Null(client.Resolutions[0].ChosenProfileId);
    }

    // The handler posts via the (synchronous) dispatcher then awaits the presenter + resolve; spin briefly
    // for the async continuation to record the resolution (signal-based, no fixed sleep).
    private static async Task WaitForResolution(FakeServiceClient client)
    {
        for (int i = 0; i < 200 && client.Resolutions.Count == 0; i++)
            await Task.Delay(10);
    }
}
