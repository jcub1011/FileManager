using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FileManager.Contracts.Messages;
using FileManager.Core.Profiles;
using FileManager.Gui.Services;

namespace FileManager.Gui.ViewModels;

/// <summary>
/// The per-Profile notification policy the activity view consults to decide whether a Job event warrants
/// an OS notification (§7): notify on failure when <see cref="NotifyOnFailure"/>; surface skips only when
/// <see cref="Verbosity"/> is raised beyond <see cref="Verbosity.FailuresOnly"/>.
/// </summary>
/// <param name="NotifyOnFailure">Whether the Profile requests a failure notification.</param>
/// <param name="Verbosity">The Profile's logging verbosity (gates whether skips notify).</param>
public sealed record JobNotificationPolicy(bool NotifyOnFailure, Verbosity Verbosity)
{
    /// <summary>The conservative default: notify on failure, do not notify on skips.</summary>
    public static JobNotificationPolicy Default { get; } = new(true, Verbosity.FailuresAndSkips);
}

/// <summary>
/// One Job shown in the activity list, with its per-Job log entries for drill-down (§7). A plain
/// observable POCO (no Avalonia dependency).
/// </summary>
public sealed partial class ActivityItemViewModel : ObservableObject
{
    /// <summary>The submission/Job id.</summary>
    public required string JobId { get; init; }

    /// <summary>The Profile the Job ran under.</summary>
    public required string ProfileId { get; init; }

    [ObservableProperty] private string _state = string.Empty;
    [ObservableProperty] private string _code = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private DateTimeOffset _timestamp;

    /// <summary>The Job's accumulated log lines (drill-down).</summary>
    public ObservableCollection<string> Log { get; } = new();
}

/// <summary>
/// The live activity/error view (§7): a most-recent-first list of Jobs (success / skip / failure) fed by
/// the <see cref="JobEvent"/> subscription, with per-Job drill-down to that Job's log entries, and the
/// failure-notification logic. The view-model is a plain POCO: it never touches Avalonia and marshals
/// list mutations through the injected <see cref="IUiDispatcher"/> (a synchronous fake in tests), so it
/// is fully unit-testable without a UI thread.
/// </summary>
public sealed partial class ActivityViewModel : ViewModelBase
{
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<string, JobNotificationPolicy> _policyFor;
    private readonly int _maxItems;
    private readonly Dictionary<string, ActivityItemViewModel> _byJobId = new(StringComparer.Ordinal);

    [ObservableProperty] private ActivityItemViewModel? _selectedJob;

    /// <summary>Recent Jobs, most-recent first.</summary>
    public ObservableCollection<ActivityItemViewModel> Jobs { get; } = new();

    /// <summary>
    /// Creates the activity view-model.
    /// </summary>
    /// <param name="notifications">The notifier invoked for qualifying failure (and raised-verbosity skip) events.</param>
    /// <param name="dispatcher">Marshals list mutations onto the UI thread (synchronous fake in tests).</param>
    /// <param name="policyFor">
    /// Resolves the per-Profile <see cref="JobNotificationPolicy"/> by ProfileId; defaults to
    /// <see cref="JobNotificationPolicy.Default"/> for every Profile when omitted.
    /// </param>
    /// <param name="maxItems">The most-recent cap on the list (older Jobs drop off).</param>
    public ActivityViewModel(
        INotificationService notifications,
        IUiDispatcher dispatcher,
        Func<string, JobNotificationPolicy>? policyFor = null,
        int maxItems = 200)
    {
        _notifications = notifications;
        _dispatcher = dispatcher;
        _policyFor = policyFor ?? (_ => JobNotificationPolicy.Default);
        _maxItems = maxItems;
    }

    /// <summary>
    /// Feeds one pushed <see cref="JobEvent"/> into the view: it updates (or inserts) the Job's row,
    /// appends the event message to that Job's log, and raises an OS notification when the §7 rules say
    /// so. Safe to call from any thread — all view-state mutation is marshalled through the dispatcher.
    /// </summary>
    public void OnJobEvent(JobEvent jobEvent)
    {
        _dispatcher.Post(() =>
        {
            if (!_byJobId.TryGetValue(jobEvent.JobId, out ActivityItemViewModel? item))
            {
                item = new ActivityItemViewModel
                {
                    JobId = jobEvent.JobId,
                    ProfileId = jobEvent.ProfileId,
                };
                _byJobId[jobEvent.JobId] = item;
                Jobs.Insert(0, item);
                TrimOldest();
            }

            item.State = jobEvent.State;
            item.Code = jobEvent.Code;
            item.Message = jobEvent.Message;
            item.Timestamp = jobEvent.Timestamp;
            item.Log.Add($"{jobEvent.Timestamp:HH:mm:ss} [{jobEvent.Code}] {jobEvent.Message}");

            MaybeNotify(jobEvent);
        });
    }

    /// <summary>True for a terminal failure event.</summary>
    private static bool IsFailure(JobEvent e) =>
        e.Code == "FAILED" || string.Equals(e.State, nameof(JobStateName.Failed), StringComparison.Ordinal);

    /// <summary>True for a terminal skip event.</summary>
    private static bool IsSkip(JobEvent e) =>
        e.Code == "SKIPPED" || string.Equals(e.State, nameof(JobStateName.Skipped), StringComparison.Ordinal);

    // The §7 notification rules: failures notify when the Profile opts in; skips notify only when its
    // verbosity is raised above FailuresOnly. Successes never notify.
    private void MaybeNotify(JobEvent jobEvent)
    {
        JobNotificationPolicy policy = _policyFor(jobEvent.ProfileId);

        if (IsFailure(jobEvent) && policy.NotifyOnFailure)
        {
            _notifications.Notify($"Job failed: {jobEvent.ProfileId}", jobEvent.Message);
            return;
        }

        if (IsSkip(jobEvent) && policy.Verbosity != Verbosity.FailuresOnly)
            _notifications.Notify($"Job skipped: {jobEvent.ProfileId}", jobEvent.Message);
    }

    private void TrimOldest()
    {
        while (Jobs.Count > _maxItems)
        {
            ActivityItemViewModel removed = Jobs[^1];
            Jobs.RemoveAt(Jobs.Count - 1);
            _byJobId.Remove(removed.JobId);
        }
    }

    // Mirrors the engine's JobState names without taking a Core dependency on the enum at the wire layer.
    private enum JobStateName
    {
        Closed,
        Skipped,
        Failed,
    }
}
