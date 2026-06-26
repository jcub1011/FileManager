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

/// <summary>A cron-based schedule trigger (spec §5.1 <c>Triggers.Schedule</c>, §3.2.2).</summary>
public sealed record ScheduleTrigger
{
    /// <summary>Whether the schedule is active.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Cron expression. Required when <see cref="Enabled"/> is true.</summary>
    public string? Cron { get; init; }

    /// <summary>IANA timezone name the cron expression is evaluated in.</summary>
    public string? Timezone { get; init; }

    /// <summary>What to do about runs missed while the engine was down.</summary>
    public MissedRunPolicy MissedRunPolicy { get; init; }
}
