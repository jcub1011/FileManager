using System.IO;
using FileManager.Core.Logging;
using FileManager.Core.Profiles;
using FileManager.Core.State;
using FileManager.Core.Triggers.Schedule;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Covers the §3.2.2 missed-run policy (CatchUpOnce / Skip), interval cadence, timezone fallback, and
/// the scheduler start-up sweep — all clock-injected (no real sleeps).
/// </summary>
public sealed class SchedulerTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static ScheduleDefinition Cron(string expr)
    {
        Assert.True(ScheduleDefinition.TryCreate(
            new ScheduleTrigger { Enabled = true, Cron = expr }, out ScheduleDefinition? s, out string? err), err);
        return s!;
    }

    private static ScheduleDefinition Interval(int seconds)
    {
        Assert.True(ScheduleDefinition.TryCreate(
            new ScheduleTrigger { Enabled = true, IntervalSeconds = seconds }, out ScheduleDefinition? s, out _));
        return s!;
    }

    [Fact]
    public void CatchUpOnce_CoalescesMultipleMissedWindows_IntoOneRun()
    {
        // Hourly schedule; engine was down for ~5 windows.
        ScheduleDefinition schedule = Cron("0 * * * *");
        var lastRun = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 1, 1, 5, 30, 0, TimeSpan.Zero);

        MissedRunDecision decision = MissedRunEvaluator.Evaluate(
            schedule, lastRun, now, MissedRunPolicy.CatchUpOnce, Utc);

        Assert.True(decision.RunNow);                 // exactly one coalesced run
        Assert.True(decision.MissedWindowCount >= 5); // several windows elapsed
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero), decision.NextDue);
    }

    [Fact]
    public void Skip_ProducesNoCatchUpRun()
    {
        ScheduleDefinition schedule = Cron("0 * * * *");
        var lastRun = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 1, 1, 5, 30, 0, TimeSpan.Zero);

        MissedRunDecision decision = MissedRunEvaluator.Evaluate(
            schedule, lastRun, now, MissedRunPolicy.Skip, Utc);

        Assert.False(decision.RunNow);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero), decision.NextDue);
    }

    [Fact]
    public void CatchUpOnce_NoMissedWindows_DoesNotRun()
    {
        ScheduleDefinition schedule = Cron("0 * * * *");
        var lastRun = new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 1, 1, 5, 30, 0, TimeSpan.Zero); // no full window since lastRun

        MissedRunDecision decision = MissedRunEvaluator.Evaluate(
            schedule, lastRun, now, MissedRunPolicy.CatchUpOnce, Utc);

        Assert.False(decision.RunNow);
        Assert.Equal(0, decision.MissedWindowCount);
    }

    [Fact]
    public void FirstEverStart_NeverCatchesUp()
    {
        ScheduleDefinition schedule = Cron("0 * * * *");
        var now = new DateTimeOffset(2026, 1, 1, 5, 30, 0, TimeSpan.Zero);

        MissedRunDecision decision = MissedRunEvaluator.Evaluate(
            schedule, lastRun: null, now, MissedRunPolicy.CatchUpOnce, Utc);

        Assert.False(decision.RunNow);
        Assert.Equal(0, decision.MissedWindowCount);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero), decision.NextDue);
    }

    [Fact]
    public void Interval_FiresAtFixedCadence()
    {
        ScheduleDefinition schedule = Interval(300); // every 5 minutes
        var after = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        DateTimeOffset? next = schedule.NextOccurrence(after, Utc);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero), next);

        // CatchUpOnce over a 32-minute downtime ⇒ several windows but one coalesced run.
        var lastRun = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 1, 1, 0, 32, 0, TimeSpan.Zero);
        MissedRunDecision decision = MissedRunEvaluator.Evaluate(
            schedule, lastRun, now, MissedRunPolicy.CatchUpOnce, Utc);
        Assert.True(decision.RunNow);
        Assert.Equal(6, decision.MissedWindowCount); // 5,10,15,20,25,30 min
    }

    [Fact]
    public void ResolveTimeZone_UnknownName_FallsBackToLocal_AndLogs()
    {
        var log = new InMemoryLogSink();
        TimeZoneInfo tz = Scheduler.ResolveTimeZone("Not/AZone", log);

        Assert.Equal(TimeZoneInfo.Local, tz);
        Assert.Contains(log.Entries, e => e.Code == "SCHEDULE_TZ_FALLBACK");
    }

    [Fact]
    public void ResolveTimeZone_NullName_UsesLocal_NoLog()
    {
        var log = new InMemoryLogSink();
        TimeZoneInfo tz = Scheduler.ResolveTimeZone(null, log);

        Assert.Equal(TimeZoneInfo.Local, tz);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void StartupSweep_CatchUpOnce_FiresOnceAndPersistsLastRun()
    {
        using var dir = new TempDir("sched");
        var store = new LastRunStore(Path.Combine(dir.Root, "state.json"));
        var now = new DateTimeOffset(2026, 1, 1, 5, 30, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        var log = new InMemoryLogSink();

        // Seed a stale last-run so several hourly windows are missed.
        Profile profile = BuildScheduled("0 * * * *", MissedRunPolicy.CatchUpOnce);
        store.SetLastRun(profile.ProfileId, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var scheduler = new Scheduler(store, log, clock);
        int fired = 0;
        var results = scheduler.RunStartupSweep(new[] { profile }, (_, _) => fired++);

        Assert.Equal(1, fired);                                   // exactly one coalesced run
        Assert.True(results[0].Decision.RunNow);
        Assert.Equal(now, store.GetLastRun(profile.ProfileId));   // last-run advanced to the fire time
    }

    [Fact]
    public void StartupSweep_Skip_DoesNotFire()
    {
        using var dir = new TempDir("sched");
        var store = new LastRunStore(Path.Combine(dir.Root, "state.json"));
        var now = new DateTimeOffset(2026, 1, 1, 5, 30, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);

        Profile profile = BuildScheduled("0 * * * *", MissedRunPolicy.Skip);
        store.SetLastRun(profile.ProfileId, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var scheduler = new Scheduler(store, new InMemoryLogSink(), clock);
        int fired = 0;
        scheduler.RunStartupSweep(new[] { profile }, (_, _) => fired++);

        Assert.Equal(0, fired);
    }

    private static Profile BuildScheduled(string cron, MissedRunPolicy policy)
    {
        Profile p = TestProfiles.Build(new[] { @"C:\src" }, new[] { @"C:\dst" });
        return p with
        {
            Triggers = new TriggerSet
            {
                ManualShell = false,
                Watcher = false,
                Schedule = new ScheduleTrigger
                {
                    Enabled = true,
                    Cron = cron,
                    Timezone = "UTC",
                    MissedRunPolicy = policy,
                },
            },
        };
    }
}
