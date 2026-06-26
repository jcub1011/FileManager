using System.IO;
using System.Text;
using FileManager.Core.Configuration;
using FileManager.Core.IO;
using FileManager.Core.Profiles;

namespace FileManager.Core.Audit;

/// <summary>
/// The file-backed durable <see cref="IAuditLog"/> (§7). Each <see cref="AuditEntry"/> is written as a
/// single UTF-8 JSON line through an <see cref="IDurableAppendWriter"/> and <c>fsync</c>'d, so a
/// recorded deletion is durable across a crash. When the file grows past the configured rotation size
/// it is renamed to a timestamped backup and a fresh file is started — the trail is append-only and
/// never rewritten in place (unlike the journal, audit rows are never dropped).
/// </summary>
public sealed class AuditLog : IAuditLog
{
    /// <summary>Default audit file name under the config directory.</summary>
    public const string DefaultAuditFileName = "deletions.audit";

    private readonly string _path;
    private readonly long _rotationSizeBytes;
    private readonly Func<string, IDurableAppendWriter> _writerFactory;
    private readonly Func<DateTimeOffset> _clock;

    // Serializes Record/RotateIfOversized so concurrent worker-pool Jobs (M5) cannot interleave two
    // entries' bytes or race rotation against an active append.
    private readonly Lock _gate = new();

    private IDurableAppendWriter _writer;

    /// <summary>The resolved audit file path.</summary>
    public string Path => _path;

    /// <summary>
    /// Creates an audit log at <paramref name="path"/>, rotating once the file exceeds
    /// <paramref name="rotationSizeBytes"/>. <paramref name="writerFactory"/> builds the durable append
    /// writer (defaults to <see cref="SystemDurableAppendWriter"/>); <paramref name="clock"/> stamps
    /// backup names (defaults to wall-clock UTC).
    /// </summary>
    public AuditLog(
        string path,
        long rotationSizeBytes = ServiceConfig.DefaultAuditRotationSizeBytes,
        Func<string, IDurableAppendWriter>? writerFactory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _rotationSizeBytes = rotationSizeBytes > 0 ? rotationSizeBytes : ServiceConfig.DefaultAuditRotationSizeBytes;
        _writerFactory = writerFactory ?? (p => new SystemDurableAppendWriter(p));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _writer = _writerFactory(_path);
    }

    /// <summary>
    /// Resolves an <see cref="AuditLog"/> from <paramref name="config"/>: the file is
    /// <see cref="ServiceConfig.AuditLogPath"/> (or the default config-dir location) and uses the
    /// config rotation size. <paramref name="configDirectory"/> overrides the base config dir (tests).
    /// </summary>
    public static AuditLog FromConfig(ServiceConfig config, string? configDirectory = null)
    {
        string path = config.AuditLogPath
            ?? System.IO.Path.Combine(configDirectory ?? ConfigPaths.GetConfigDirectory(), DefaultAuditFileName);
        return new AuditLog(path, config.AuditRotationSizeBytes);
    }

    public void Record(AuditEntry entry)
    {
        string json = ProfileSerializer.Serialize(entry);
        // One entry per line keeps the trail greppable; the line delimiter is part of the frame.
        byte[] line = Encoding.UTF8.GetBytes(json + "\n");
        lock (_gate)
        {
            RotateIfOversized();
            _writer.Append(line);
            _writer.Flush(); // fsync per entry — a recorded deletion survives a crash (§7).
        }
    }

    private void RotateIfOversized()
    {
        long size;
        try
        {
            size = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (size < _rotationSizeBytes)
            return;

        _writer.Dispose();
        try
        {
            string stamp = _clock().UtcDateTime.ToString("yyyyMMdd-HHmmssfff");
            string backup = _path + "." + stamp;
            if (File.Exists(_path))
                File.Move(_path, backup, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort rotation: if the rename fails, keep appending to the existing file.
        }
        finally
        {
            _writer = _writerFactory(_path);
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _writer.Dispose();
    }
}
