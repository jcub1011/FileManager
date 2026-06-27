using FileManager.Contracts.Messages;
using FileManager.Gui.ViewModels;
using Xunit;

namespace FileManager.Gui.Tests;

/// <summary>
/// Verifies the dry-run view-model issues the request and renders every report section, computing the
/// overwrite/deletion counts. POCO view-model; no UI thread.
/// </summary>
public sealed class DryRunViewModelTests
{
    [Fact]
    public async Task Run_PopulatesAllSections_AndCounts()
    {
        var report = new DryRunReport
        {
            Implemented = true,
            ProfileId = "p",
            Matches = new[] { new DryRunMatchDto("/s/a", "a", "Pass"), new DryRunMatchDto("/s/b", "b", "Pass") },
            ScreenedOut = new[] { new DryRunScreenedOutDto("/s/c", "Include", null) },
            Commands = new[] { new DryRunCommandDto("/s/a", 1, "step", "/bin/x", true, new[] { "-i", "a" }) },
            TargetWrites = new[]
            {
                new DryRunTargetWriteDto("/s/a", "/t", "/t/a", "Written"),
                new DryRunTargetWriteDto("/s/b", "/t", "/t/b", "Overwritten"),
            },
            Deletions = new[] { new DryRunDeletionDto("/t", "/t/surplus", "surplus") },
            Dispositions = new[] { new DryRunDispositionDto("/s/a", "MoveToTrash", "/trash") },
        };
        var client = new FakeServiceClient { Report = report };
        var vm = new DryRunViewModel(client) { Path = "/s", ProfileId = "p" };

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Matches.Count);
        Assert.Single(vm.ScreenedOut);
        Assert.Single(vm.Commands);
        Assert.Equal(2, vm.TargetWrites.Count);
        Assert.Single(vm.Deletions);
        Assert.Single(vm.Dispositions);
        Assert.Equal(1, vm.OverwriteCount);
        Assert.Equal(1, vm.DeletionCount);
        Assert.False(vm.IsRunning);
        Assert.Contains("match", vm.Status);
    }

    [Fact]
    public async Task Run_WhenServiceUnreachable_SetsStatus()
    {
        var client = new FakeServiceClient { Report = null };
        var vm = new DryRunViewModel(client) { Path = "/s" };

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Service unreachable.", vm.Status);
        Assert.Empty(vm.Matches);
    }
}
