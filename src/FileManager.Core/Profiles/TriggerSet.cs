namespace FileManager.Core.Profiles;

/// <summary>Which triggers may launch Jobs for a Profile (spec §5.1 <c>Triggers</c>).</summary>
public sealed record TriggerSet
{
    /// <summary>Allow manual invocation from the shell / GUI.</summary>
    public required bool ManualShell { get; init; }

    /// <summary>Allow the filesystem watcher to trigger Jobs.</summary>
    public required bool Watcher { get; init; }

    /// <summary>Optional schedule-based trigger. Null when no schedule is configured.</summary>
    public ScheduleTrigger? Schedule { get; init; }
}

/// <summary>A schedule-based trigger (spec §5.1 <c>Triggers.Schedule</c>, §3.2 / §3.2.2).</summary>
/// <remarks>
/// <b>Schema extension (M5).</b> §5.1 originally modeled only a <see cref="Cron"/> string. The spec
/// text (§3.2) also allows a <em>fixed interval</em>, so M5 adds the optional
/// <see cref="IntervalSeconds"/>. Exactly one of <see cref="Cron"/> / <see cref="IntervalSeconds"/>
/// must be set when <see cref="Enabled"/> is true (enforced by <c>ProfileValidator</c>). The field is
/// part of <see cref="Profile"/>, which is already source-gen registered, so no JSON-context change is
/// needed; it round-trips as the PascalCase property name <c>IntervalSeconds</c>.
/// </remarks>
public sealed record ScheduleTrigger
{
    /// <summary>Whether the schedule is active.</summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// Standard 5-field cron expression (see <see cref="FileManager.Core.Triggers.Schedule.CronExpression"/>
    /// for the supported subset). Mutually exclusive with <see cref="IntervalSeconds"/>; exactly one is
    /// required when <see cref="Enabled"/> is true.
    /// </summary>
    public string? Cron { get; init; }

    /// <summary>
    /// Fixed interval, in seconds, between runs (the alternative to <see cref="Cron"/>). Must be a
    /// positive integer. Mutually exclusive with <see cref="Cron"/>; exactly one is required when
    /// <see cref="Enabled"/> is true.
    /// </summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>IANA timezone name the cron expression is evaluated in.</summary>
    public string? Timezone { get; init; }

    /// <summary>What to do about runs missed while the engine was down.</summary>
    public MissedRunPolicy MissedRunPolicy { get; init; }
}
