using System.IO;
using FileManager.Core.FileSystem;
using FileManager.Core.Profiles;
using FileManager.Core.Safety;
using FileManager.Gui.Services;
using FileManager.Gui.ViewModels;
using Xunit;

namespace FileManager.Gui.Tests;

/// <summary>
/// Verifies the Profile editor's §6.1 safety gating and save behavior: a BLOCKING state for
/// <c>None</c>+<c>PermanentDelete</c> (Save disabled until acknowledged), a non-blocking banner for
/// <c>None</c>+<c>MoveToTrash</c>, a visible destructive flag for <see cref="SyncMode.Mirror"/>, and a
/// Save that writes the Profile JSON and triggers a reload. All exercised on the plain POCO view-model —
/// no Avalonia app, no UI thread.
/// </summary>
public sealed class ProfileEditorViewModelTests
{
    private static ProfileEditorViewModel NewEditor(string profilesDir, FakeServiceClient? client = null)
    {
        var vm = new ProfileEditorViewModel(client, profilesDir);
        vm.Sources[0].Path = Path.Combine(profilesDir, "src");
        vm.Targets[0].Path = Path.Combine(profilesDir, "dst");
        return vm;
    }

    [Fact]
    public void NoneAndPermanentDelete_IsBlocking_AndSaveDisabledUntilAcknowledged()
    {
        using var temp = new TempDir();
        ProfileEditorViewModel vm = NewEditor(temp.Root);

        vm.VerificationMethod = VerificationMethod.None;
        vm.OnSuccess = OnSuccess.PermanentDelete;

        Assert.Equal(SafetyLevel.Blocking, vm.Safety.Level);
        Assert.True(vm.IsBlocking);
        Assert.True(vm.IsBlocked);
        Assert.False(vm.CanSave);
        Assert.False(vm.SaveCommand.CanExecute(null));

        // Acknowledging the risk unblocks Save.
        vm.BlockingRiskAcknowledged = true;
        Assert.False(vm.IsBlocked);
        Assert.True(vm.CanSave);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void NoneAndMoveToTrash_IsWarning_AndDoesNotBlockSave()
    {
        using var temp = new TempDir();
        ProfileEditorViewModel vm = NewEditor(temp.Root);

        vm.VerificationMethod = VerificationMethod.None;
        vm.OnSuccess = OnSuccess.MoveToTrash;

        Assert.Equal(SafetyLevel.Warning, vm.Safety.Level);
        Assert.True(vm.IsWarning);
        Assert.False(vm.IsBlocking);
        Assert.False(vm.IsBlocked);
        Assert.True(vm.CanSave);
        Assert.NotNull(vm.SafetyMessage);
    }

    [Fact]
    public void MirrorMode_IsFlaggedDestructive()
    {
        using var temp = new TempDir();
        ProfileEditorViewModel vm = NewEditor(temp.Root);

        Assert.False(vm.IsDestructiveMirror);
        vm.SyncMode = SyncMode.Mirror;
        Assert.True(vm.IsMirror);
        Assert.True(vm.IsDestructiveMirror);
    }

    [Fact]
    public void SafeConfiguration_IsNotBlockedAndSaveEnabled()
    {
        using var temp = new TempDir();
        ProfileEditorViewModel vm = NewEditor(temp.Root);

        vm.VerificationMethod = VerificationMethod.SHA256;
        vm.OnSuccess = OnSuccess.PermanentDelete; // safe because verification is on

        Assert.Equal(SafetyLevel.Safe, vm.Safety.Level);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task Save_WritesProfileJson_AndTriggersReload()
    {
        using var temp = new TempDir();
        var client = new FakeServiceClient { Reload = new(3, Array.Empty<string>()) };
        ProfileEditorViewModel vm = NewEditor(temp.Root, client);
        vm.Name = "My Profile";

        await vm.SaveCommand.ExecuteAsync(null);

        string expected = Path.Combine(temp.Root, vm.ProfileId + ".json");
        Assert.True(File.Exists(expected));

        // The file round-trips to a valid Profile.
        Profile? loaded = ProfileSerializer.Deserialize(File.ReadAllText(expected));
        Assert.NotNull(loaded);
        Assert.Equal("My Profile", loaded!.Name);

        Assert.Contains("reloaded 3", vm.SaveStatus);
    }

    [Fact]
    public void SeededWithPath_SeedsFirstSourceAndTarget()
    {
        using var temp = new TempDir();
        string seed = Path.Combine(temp.Root, "invoked");

        ProfileEditorViewModel vm = ProfileEditorViewModel.SeededWithPath(seed, null, temp.Root);

        Assert.Equal(seed, Assert.Single(vm.Sources).Path);
        Assert.Equal(seed, Assert.Single(vm.Targets).Path);
    }

    [Fact]
    public async Task PathRow_Browse_UsesFolderBrowser_AndSetsPath()
    {
        // FIX 5: the path picker / folder browser is wired into the editor (not discarded). The row's
        // Browse command opens the (fake) browser and applies its result — testable with no display.
        using var temp = new TempDir();
        var browser = new FakeFolderBrowser { Result = Path.Combine(temp.Root, "chosen") };
        var picker = new PathPickerService(new FileSystemService());
        var vm = new ProfileEditorViewModel(client: null, profilesDirectory: temp.Root, pathPicker: picker, browser: browser);

        // Confirm the picker is exposed (not discarded) and the row carries a working Browse command.
        Assert.NotNull(vm.PathPicker);
        PathRowViewModel row = vm.Sources[0];
        row.Path = temp.Root; // becomes the start hint

        await row.BrowseCommand.ExecuteAsync(null);

        Assert.Equal(1, browser.Calls);
        Assert.Equal(temp.Root, browser.LastStartingDirectory);
        Assert.Equal(Path.Combine(temp.Root, "chosen"), row.Path);
    }

    [Fact]
    public async Task PathRow_Browse_WhenCancelled_LeavesPathUnchanged()
    {
        using var temp = new TempDir();
        var browser = new FakeFolderBrowser { Result = null }; // user cancelled
        var vm = new ProfileEditorViewModel(client: null, profilesDirectory: temp.Root, browser: browser);
        PathRowViewModel row = vm.Sources[0];
        row.Path = "/keep/me";

        await row.BrowseCommand.ExecuteAsync(null);

        Assert.Equal("/keep/me", row.Path);
    }
}
