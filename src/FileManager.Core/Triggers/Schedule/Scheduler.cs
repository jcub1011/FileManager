using FileManager.Core.Logging;
using FileManager.Core.Profiles;
using FileManager.Core.State;

namespace FileManager.Core.Triggers.Schedule;

/// <summary>
/// Drives scheduled Profiles (§3.2 / §3.2.2): resolves each Profile's timezone, computes due times,
/// applies the <see cref="MissedRunPolicy"/> at start using persisted last-run timestamps
/// (<see cref="LastRunStore"/>), and fires runs. A run is delivered to a host-supplied
/// <c>onDue</c> callback (the M6 service host scans the Profile's Sources and enqueues per-file Jobs);
/// the scheduler itself stays decoupled from the Job queue and is fully clock-injected for tests.
/// </summary>
/// <remarks>
/// <para>The clock is a <see cref="TimeProvider"/> (built-in, AOT-safe). Tests pass a fake provider so
/// due-time evaluation and the tick loop are deterministic with no wall-clock sleeps; production passes
/// <see cref="TimeProvider.System"/>.</para>
/// <para>The start-time decision (catch-up vs. wait) is the pure
/// <see cref="MissedRunEvaluator"/>; this class adds the timezone resolution, last-run persistence, and
/// the firing loop on top.</para>
/// </remarks>
public sealed class Scheduler
{
    private readonly TimeProvider _clock;
    private readonly LastRunStore _lastRuns;
    private readonly ILogSink _log;

    /// <summary>
    /// Creates a scheduler over <paramref name="lastRuns"/> (last-run persistence) and
    /// <paramref name="clock"/> (defaults to <see cref="TimeProvider.System"/>). Timezone-resolution
    /// fallbacks and per-Job network caveats are logged to <paramref name="log"/>.
    /// </summary>
    public Scheduler(LastRunStore lastRuns, ILogSink log, TimeProvider? clock = null)
    {
        _lastRuns = lastRuns;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Resolves an IANA timezone name to a <see cref="TimeZoneInfo"/>. A null/blank name defaults to
    /// the system local zone; an <em>unknown</em> name is a logged fallback to local (per the milestone:
    /// "handle the lookup failure as a logged fallback, not a crash"), never an exception.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? ianaName, ILogSink? log)
    {
        if (string.IsNullOrWhiteSpace(ianaName))
            return TimeZoneInfo.Local;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaName);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            log?.Log(new JobLogEntry
            {
                Severity = LogSeverity.Failure,
                Code = "SCHEDULE_TZ_FALLBACK",
                JobId = string.Empty,
                Message = $"Unknown timezone '{ianaName}'; falling back to system local. ({ex.Message})",
            });
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>
    /// Evaluates a scheduled Profile at service start: resolves its schedule + timezone and applies the
    /// missed-run policy against the persisted last-run timestamp and "now". Returns the
    /// <see cref="MissedRunDecision"/>, or null when the Profile has no enabled/valid schedule (the
    /// caller skips it). Does not fire anything — see <see cref="FireDue"/>.
    /// </summary>
    public MissedRunDecision? EvaluateStart(Profile profile)
    {
        if (profile.Triggers.Schedule is not { Enabled: true } trigger)
            return null;
        if (!ScheduleDefinition.TryCreate(trigger, out ScheduleDefinition? schedule, out _))
            return null;

        TimeZoneInfo tz = ResolveTimeZone(trigger.Timezone, _log);
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset? lastRun = _lastRuns.GetLastRun(profile.ProfileId);
        return MissedRunEvaluator.Evaluate(schedule!, lastRun, now, trigger.MissedRunPolicy, tz);
    }

    /// <summary>
    /// Records that <paramref name="profile"/> fired at <paramref name="firedAt"/> (persisting the
    /// last-run timestamp) and invokes <paramref name="onDue"/> so the host enqueues the actual Jobs.
    /// Centralizing this keeps the last-run bookkeeping and the run dispatch atomic from the caller's
    /// view.
    /// </summary>
    public void FireDue(Profile profile, DateTimeOffset firedAt, Action<Profile, DateTimeOffset> onDue)
    {
        _lastRuns.SetLastRun(profile.ProfileId, firedAt);
        onDue(profile, firedAt);
    }

    /// <summary>
    /// Convenience start-up sweep over <paramref name="profiles"/>: for each scheduled Profile, applies
    /// the missed-run policy and, when a coalesced catch-up run is due (<see cref="MissedRunDecision.RunNow"/>),
    /// fires it immediately via <paramref name="onDue"/>. Returns the per-Profile decisions so the host
    /// can arm timers for the <see cref="MissedRunDecision.NextDue"/> times. Pure with respect to the
    /// injected clock — deterministic in tests.
    /// </summary>
    public IReadOnlyList<(Profile Profile, MissedRunDecision Decision)> RunStartupSweep(
        IEnumerable<Profile> profiles, Action<Profile, DateTimeOffset> onDue)
    {
        var results = new List<(Profile, MissedRunDecision)>();
        foreach (Profile profile in profiles)
        {
            MissedRunDecision? decision = EvaluateStart(profile);
            if (decision is null)
                continue;

            if (decision.RunNow)
                FireDue(profile, _clock.GetUtcNow(), onDue);

            results.Add((profile, decision));
        }

        return results;
    }
}
