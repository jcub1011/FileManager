using FileManager.Contracts.Messages;
using FileManager.Core.Profiles;
using FileManager.Gui.ViewModels;
using Xunit;

namespace FileManager.Gui.Tests;

/// <summary>
/// Verifies the activity view-model reflects pushed <see cref="JobEvent"/>s (list + per-Job grouping +
/// drill-down log) and applies the §7 notification rules: a failure notifies when the Profile opts in; a
/// skip notifies only when verbosity is raised; a success never notifies. Exercised on the POCO
/// view-model with a synchronous dispatcher and a capturing notifier — no UI thread.
/// </summary>
public sealed class ActivityViewModelTests
{
    private static JobEvent Event(string jobId, string profileId, string state, string code, string msg = "m") =>
        new(jobId, profileId, state, code, msg, DateTimeOffset.UnixEpoch);

    [Fact]
    public void OnJobEvent_AddsAndGroupsByJobId_NewestFirst()
    {
        var notifier = new CapturingNotifier();
        var vm = new ActivityViewModel(notifier, new SyncDispatcher());

        vm.OnJobEvent(Event("j1", "p", "Running", "PROGRESS"));
        vm.OnJobEvent(Event("j2", "p", "Closed", "COMPLETED"));
        // A second event for j1 must UPDATE the existing row, not add a new one.
        vm.OnJobEvent(Event("j1", "p", "Closed", "COMPLETED", "done"));

        Assert.Equal(2, vm.Jobs.Count);
        // Drill-down: j1's log accumulated both of its events.
        ActivityItemViewModel j1 = Assert.Single(vm.Jobs, j => j.JobId == "j1");
        Assert.Equal("Closed", j1.State);
        Assert.Equal(2, j1.Log.Count);
    }

    [Fact]
    public void FailureEvent_WithNotifyOnFailure_RaisesNotification()
    {
        var notifier = new CapturingNotifier();
        var vm = new ActivityViewModel(
            notifier, new SyncDispatcher(),
            policyFor: _ => new JobNotificationPolicy(NotifyOnFailure: true, Verbosity.FailuresOnly));

        vm.OnJobEvent(Event("j1", "p", "Failed", "FAILED", "boom"));

        (string Title, string Message) note = Assert.Single(notifier.Notifications);
        Assert.Contains("p", note.Title);
        Assert.Equal("boom", note.Message);
    }

    [Fact]
    public void FailureEvent_WithoutNotifyOnFailure_DoesNotNotify()
    {
        var notifier = new CapturingNotifier();
        var vm = new ActivityViewModel(
            notifier, new SyncDispatcher(),
            policyFor: _ => new JobNotificationPolicy(NotifyOnFailure: false, Verbosity.All));

        vm.OnJobEvent(Event("j1", "p", "Failed", "FAILED"));

        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public void SkipEvent_AtFailuresOnly_DoesNotNotify()
    {
        var notifier = new CapturingNotifier();
        var vm = new ActivityViewModel(
            notifier, new SyncDispatcher(),
            policyFor: _ => new JobNotificationPolicy(NotifyOnFailure: true, Verbosity.FailuresOnly));

        vm.OnJobEvent(Event("j1", "p", "Skipped", "SKIPPED"));

        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public void SkipEvent_AtRaisedVerbosity_Notifies()
    {
        var notifier = new CapturingNotifier();
        var vm = new ActivityViewModel(
            notifier, new SyncDispatcher(),
            policyFor: _ => new JobNotificationPolicy(NotifyOnFailure: true, Verbosity.FailuresAndSkips));

        vm.OnJobEvent(Event("j1", "p", "Skipped", "SKIPPED"));

        Assert.Single(notifier.Notifications);
    }

    [Fact]
    public void SuccessEvent_NeverNotifies()
    {
        var notifier = new CapturingNotifier();
        var vm = new ActivityViewModel(notifier, new SyncDispatcher());

        vm.OnJobEvent(Event("j1", "p", "Closed", "COMPLETED"));

        Assert.Empty(notifier.Notifications);
    }
}
