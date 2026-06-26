using FileManager.Core.Profiles;

namespace FileManager.Core.Logging;

/// <summary>Severity of a Job log entry, mapped to <see cref="Verbosity"/> for filtering.</summary>
public enum LogSeverity
{
    /// <summary>Routine progress (placements, dispositions). Emitted only at <see cref="Verbosity.All"/>.</summary>
    Info,

    /// <summary>A file was screened out or a write was skipped. Emitted at <see cref="Verbosity.FailuresAndSkips"/> and above.</summary>
    Skip,

    /// <summary>A Job failed. Always emitted.</summary>
    Failure,
}

/// <summary>One structured engine log record for a Job.</summary>
public sealed record JobLogEntry
{
    public required LogSeverity Severity { get; init; }

    /// <summary>Short stable code (e.g. <c>SKIPPED</c>, <c>PLACED</c>, <c>FAILED</c>, <c>DISPOSED</c>).</summary>
    public required string Code { get; init; }

    public required string JobId { get; init; }

    public required string Message { get; init; }
}

/// <summary>Maps <see cref="Verbosity"/> to the set of severities that should be emitted.</summary>
public static class VerbosityFilter
{
    /// <summary>Whether an entry of <paramref name="severity"/> is emitted at <paramref name="verbosity"/>.</summary>
    public static bool ShouldEmit(Verbosity verbosity, LogSeverity severity) => verbosity switch
    {
        Verbosity.FailuresOnly => severity == LogSeverity.Failure,
        Verbosity.FailuresAndSkips => severity is LogSeverity.Failure or LogSeverity.Skip,
        Verbosity.All => true,
        _ => true,
    };
}
