using FileManager.Core.Profiles;

namespace FileManager.Core.Triggers.Schedule;

/// <summary>
/// A resolved, evaluable schedule — either a parsed <see cref="CronExpression"/> or a fixed interval —
/// built from a Profile's <see cref="ScheduleTrigger"/>. Wrapping both behind one type lets the
/// <see cref="MissedRunEvaluator"/> and <see cref="Scheduler"/> compute due times without caring which
/// kind they have. Construction goes through <see cref="TryCreate"/>, which mirrors the validator's
/// exactly-one-of rule and never throws on bad config.
/// </summary>
public sealed class ScheduleDefinition
{
    private readonly CronExpression? _cron;
    private readonly TimeSpan? _interval;

    private ScheduleDefinition(CronExpression? cron, TimeSpan? interval)
    {
        _cron = cron;
        _interval = interval;
    }

    /// <summary>Whether this is an interval (vs. cron) schedule.</summary>
    public bool IsInterval => _interval is not null;

    /// <summary>
    /// Builds a <see cref="ScheduleDefinition"/> from <paramref name="trigger"/>. Returns false (with a
    /// reason) when the trigger is disabled, sets neither/both of cron/interval, or has a malformed
    /// cron — matching <c>ProfileValidator</c> so a validated Profile always resolves successfully.
    /// </summary>
    public static bool TryCreate(ScheduleTrigger trigger, out ScheduleDefinition? schedule, out string? error)
    {
        schedule = null;
        error = null;

        if (!trigger.Enabled)
        {
            error = "Schedule trigger is not enabled.";
            return false;
        }

        bool hasCron = !string.IsNullOrWhiteSpace(trigger.Cron);
        bool hasInterval = trigger.IntervalSeconds is not null;
        if (hasCron == hasInterval)
        {
            error = "Exactly one of Cron or IntervalSeconds must be set.";
            return false;
        }

        if (hasCron)
        {
            if (!CronExpression.TryParse(trigger.Cron, out CronExpression? cron, out error))
                return false;
            schedule = new ScheduleDefinition(cron, interval: null);
            return true;
        }

        if (trigger.IntervalSeconds is not > 0)
        {
            error = "IntervalSeconds must be a positive integer.";
            return false;
        }

        schedule = new ScheduleDefinition(cron: null, interval: TimeSpan.FromSeconds(trigger.IntervalSeconds.Value));
        return true;
    }

    /// <summary>
    /// The next fire time strictly after <paramref name="after"/>. For a cron schedule this defers to
    /// <see cref="CronExpression.NextOccurrence"/> in <paramref name="timeZone"/>; for an interval it is
    /// simply <paramref name="after"/> + interval (timezone-independent — a fixed cadence ticks on
    /// absolute time, unaffected by DST). Returns null only when a cron expression has no match within
    /// its search horizon.
    /// </summary>
    public DateTimeOffset? NextOccurrence(DateTimeOffset after, TimeZoneInfo timeZone)
    {
        if (_interval is { } interval)
            return after + interval;
        return _cron!.NextOccurrence(after, timeZone);
    }
}
