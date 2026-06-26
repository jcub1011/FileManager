namespace FileManager.Core.Logging;

/// <summary>
/// Destination for engine log entries. The engine applies <see cref="VerbosityFilter"/> before
/// calling <see cref="Log"/>, so a sink receives only entries that passed the Profile's verbosity.
/// Real sinks (file/journal) arrive in M4.
/// </summary>
public interface ILogSink
{
    public void Log(JobLogEntry entry);
}
