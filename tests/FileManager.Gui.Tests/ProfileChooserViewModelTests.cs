using FileManager.Contracts.Messages;
using FileManager.Gui.ViewModels;
using Xunit;

namespace FileManager.Gui.Tests;

/// <summary>
/// Verifies the §3.2 always-prompt chooser view-model: it lists the matching Profiles, requires an
/// explicit pick (no auto-select), ALWAYS offers "Create Profile…" (even with no matches), and returns
/// the chosen id / null on cancel. All headless — POCO view-models with an injected editor factory, no
/// display server.
/// </summary>
public sealed class ProfileChooserViewModelTests
{
    private static ManualInvocationPending Pending(params (string Id, string Name)[] matches) =>
        new("inv-1", "/data/folder", true,
            matches.Select(m => new ProfileSummary(m.Id, m.Name, true)).ToList());

    private static ProfileEditorViewModel StubEditor(string seedPath) =>
        ProfileEditorViewModel.SeededWithPath(seedPath);

    [Fact]
    public void ListsMatches_FromPending()
    {
        var vm = new ProfileChooserViewModel(Pending(("p1", "One"), ("p2", "Two")), StubEditor);

        Assert.Equal(2, vm.Matches.Count);
        Assert.False(vm.HasNoMatches);
        Assert.Equal("inv-1", vm.InvocationId);
        Assert.Equal("/data/folder", vm.InvokedPath);
        Assert.True(vm.Recursive);
    }

    [Fact]
    public void RequiresExplicitPick_ChooseDisabledUntilSelected()
    {
        var vm = new ProfileChooserViewModel(Pending(("p1", "One")), StubEditor);

        Assert.Null(vm.Selected);                       // no auto-select
        Assert.False(vm.ChooseCommand.CanExecute(null)); // cannot run without a pick

        vm.Selected = vm.Matches[0];
        Assert.True(vm.ChooseCommand.CanExecute(null));
    }

    [Fact]
    public async Task Choose_CompletesWithChosenId()
    {
        var vm = new ProfileChooserViewModel(Pending(("p1", "One"), ("p2", "Two")), StubEditor);
        vm.Selected = vm.Matches[1];

        vm.ChooseCommand.Execute(null);

        Assert.Equal("p2", await vm.Completion);
    }

    [Fact]
    public async Task Cancel_CompletesWithNull()
    {
        var vm = new ProfileChooserViewModel(Pending(("p1", "One")), StubEditor);

        vm.CancelCommand.Execute(null);

        Assert.Null(await vm.Completion);
    }

    [Fact]
    public async Task WindowClose_ResolvesAsCancel()
    {
        // BLOCKER-2: the dialog's OnClosed calls CancelChoice() — dismissing via the title-bar X / Alt+F4
        // must complete the choice with null so the presenter still sends ResolveManualInvocation(id, null)
        // and the pending is discarded promptly (never silently leaked).
        var vm = new ProfileChooserViewModel(Pending(("p1", "One")), StubEditor);

        vm.CancelChoice(); // exactly what ProfileChooserDialog.OnClosed invokes

        Assert.Null(await vm.Completion);
    }

    [Fact]
    public async Task CancelChoice_IsIdempotent_AfterAChoice()
    {
        // A button choice completes first; a subsequent window-close (CancelChoice) must be a harmless
        // no-op rather than overwriting the choice or throwing.
        var vm = new ProfileChooserViewModel(Pending(("p1", "One")), StubEditor);
        vm.Selected = vm.Matches[0];
        vm.ChooseCommand.Execute(null);

        vm.CancelChoice(); // close after choosing

        Assert.Equal("p1", await vm.Completion); // still the chosen id
    }

    [Fact]
    public void NoMatches_StillOffersCreateProfile()
    {
        var vm = new ProfileChooserViewModel(Pending(), StubEditor);

        Assert.True(vm.HasNoMatches);
        Assert.Empty(vm.Matches);
        Assert.False(vm.ChooseCommand.CanExecute(null)); // nothing to pick

        // Create Profile… is always available and seeds the editor with the invoked path.
        vm.CreateProfileCommand.Execute(null);
        Assert.NotNull(vm.CreatedEditor);
        Assert.Equal("/data/folder", vm.CreatedEditor!.Sources[0].Path);
    }

    [Fact]
    public void CreateProfile_SeedsEditorWithInvokedPath_EvenWithMatches()
    {
        var vm = new ProfileChooserViewModel(Pending(("p1", "One")), StubEditor);

        vm.CreateProfileCommand.Execute(null);

        Assert.NotNull(vm.CreatedEditor);
        Assert.Equal("/data/folder", vm.CreatedEditor!.Sources[0].Path);
    }
}
