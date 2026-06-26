using System.IO;
using System.Text;
using FileManager.Core.Configuration;

namespace FileManager.Core.Logging;

/// <summary>
/// The persistent rotating application log (§7 bullet 1): an <see cref="ILogSink"/> that appends each
/// <see cref="JobLogEntry"/> as a single line to a file under <see cref="ServiceConfig.LogDirectory"/>,
/// rotating to a timestamped backup once the file reaches <see cref="ServiceConfig.LogRotationSizeBytes"/>.
/// Dependency-free and reflection-free.
/// </summary>
/// <remarks>
/// Verbosity is NOT re-applied here: the <see cref="FileManager.Core.Jobs.JobEngine"/> already filters
/// entries through <see cref="VerbosityFilter"/> before calling <see cref="ILogSink.Log"/>, so this
/// sink persists exactly the entries it is handed (double-filtering would silently drop lines).
/// Unlike the journal/audit trail this writer does not need <c>fsync</c> per line — a lost tail line
/// after a crash is acceptable for an operational log (the journal carries the durable facts).
/// </remarks>
public sealed class RotatingLogWriter : ILogSink, IDisposable
{
    /// <summary>Default log sub-folder under the config directory.</summary>
    public const string DefaultLogDirName = "logs";

    /// <summary>The active log file name within the log directory.</summary>
    public const string DefaultLogFileName = "filemanager.log";

    private readonly string _path;
    private readonly long _rotationSizeBytes;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private FileStream _stream;

    /// <summary>The resolved active log file path.</summary>
    public string Path => _path;

    /// <summary>
    /// Creates a writer at <paramref name="path"/>, rotating once it reaches
    /// <paramref name="rotationSizeBytes"/> bytes. <paramref name="clock"/> stamps backup names.
    /// </summary>
    public RotatingLogWriter(
        string path,
        long rotationSizeBytes = ServiceConfig.DefaultLogRotationSizeBytes,
        Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _rotationSizeBytes = rotationSizeBytes > 0 ? rotationSizeBytes : ServiceConfig.DefaultLogRotationSizeBytes;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        string? dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _stream = Open();
    }

    /// <summary>
    /// Resolves a <see cref="RotatingLogWriter"/> from <paramref name="config"/>: the file lives under
    /// <see cref="ServiceConfig.LogDirectory"/> (or the default config-dir location) and uses the config
    /// rotation size. <paramref name="configDirectory"/> overrides the base config dir (tests).
    /// </summary>
    public static RotatingLogWriter FromConfig(ServiceConfig config, string? configDirectory = null)
    {
        string dir = config.LogDirectory
            ?? System.IO.Path.Combine(configDirectory ?? ConfigPaths.GetConfigDirectory(), DefaultLogDirName);
        string path = System.IO.Path.Combine(dir, DefaultLogFileName);
        return new RotatingLogWriter(path, config.LogRotationSizeBytes);
    }

    public void Log(JobLogEntry entry)
    {
        string line = $"{_clock().UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}\t{entry.Severity}\t{entry.Code}\t{entry.JobId}\t{entry.Message}\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);

        lock (_gate)
        {
            RotateIfOversized(bytes.Length);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }
    }

    private void RotateIfOversized(int incomingBytes)
    {
        if (_stream.Length + incomingBytes < _rotationSizeBytes)
            return;

        _stream.Dispose();
        try
        {
            string stamp = _clock().UtcDateTime.ToString("yyyyMMdd-HHmmssfff");
            string backup = _path + "." + stamp;
            if (File.Exists(_path))
                File.Move(_path, backup, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort rotation: on failure, keep appending to the existing file.
        }
        finally
        {
            _stream = Open();
        }
    }

    private FileStream Open() =>
        new(_path, FileMode.Append, FileAccess.Write, FileShare.Read);

    public void Dispose()
    {
        lock (_gate)
            _stream.Dispose();
    }
}
