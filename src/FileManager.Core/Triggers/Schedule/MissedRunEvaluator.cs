using FileManager.Core.Profiles;

namespace FileManager.Core.Triggers.Schedule;

/// <summary>
/// The decision for a Profile's schedule at service start, per <see cref="MissedRunPolicy"/> (§3.2.2).
/// </summary>
/// <param name="RunNow">
/// True when a coalesced catch-up run is due immediately because one or more scheduled windows elapsed
/// while the engine was down (only ever set under <see cref="MissedRunPolicy.CatchUpOnce"/>).
/// </param>
/// <param name="MissedWindowCount">
/// How many scheduled windows fell in <c>(lastRun, now]</c> — diagnostic; the catch-up coalesces them
/// into the single run signalled by <see cref="RunNow"/>.
/// </param>
/// <param name="NextDue">
/// The next scheduled fire time strictly after <c>now</c> (the future tick to wait for), or null when
/// the schedule has no upcoming occurrence within the cron search horizon.
/// </param>
public sealed record MissedRunDecision(bool RunNow, int MissedWindowCount, DateTimeOffset? NextDue);

/// <summary>
/// Pure, clock-injected evaluator of the §3.2.2 missed-run policy. Given a Profile's last-run
/// timestamp, the current time, the resolved schedule, and its <see cref="MissedRunPolicy"/>, it
/// decides whether a single coalesced catch-up run is due and when the next future run is. No state,
/// no I/O — deterministic for tests.
/// </summary>
public static class MissedRunEvaluator
{
    /// <summary>
    /// Evaluates the policy.
    /// <list type="bullet">
    /// <item><see cref="MissedRunPolicy.CatchUpOnce"/>: if at least one window elapsed in
    /// <c>(lastRun, now]</c>, exactly one run is due now (windows are coalesced); otherwise none.</item>
    /// <item><see cref="MissedRunPolicy.Skip"/>: never a catch-up run — just compute the next due time
    /// after <c>now</c>.</item>
    /// </list>
    /// When <paramref name="lastRun"/> is null (the Profile has never run) no downtime windows are
    /// counted — a first-ever start simply waits for the next scheduled time, under either policy.
    /// </summary>
    public static MissedRunDecision Evaluate(
        ScheduleDefinition schedule,
        DateTimeOffset? lastRun,
        DateTimeOffset now,
        MissedRunPolicy policy,
        TimeZoneInfo timeZone)
    {
        int missed = lastRun is { } last ? CountWindows(schedule, last, now, timeZone) : 0;
        DateTimeOffset? nextDue = schedule.NextOccurrence(now, timeZone);

        bool runNow = policy == MissedRunPolicy.CatchUpOnce && missed > 0;
        return new MissedRunDecision(runNow, missed, nextDue);
    }

    // Counts scheduled fire times in (lastRun, now]. Bounded so a pathological interval/cron over a
    // long downtime cannot spin unboundedly — once we know "at least one" we don't need an exact huge
    // count for the coalesced decision, but we report up to the cap for diagnostics.
    private static int CountWindows(
        ScheduleDefinition schedule, DateTimeOffset lastRun, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        const int cap = 100_000;
        int count = 0;
        DateTimeOffset cursor = lastRun;
        while (count < cap)
        {
            DateTimeOffset? next = schedule.NextOccurrence(cursor, timeZone);
            if (next is not { } due || due > now)
                break;
            count++;
            cursor = due;
        }

        return count;
    }
}
