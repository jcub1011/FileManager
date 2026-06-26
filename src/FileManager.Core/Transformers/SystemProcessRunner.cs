using System.Diagnostics;
using System.Text;
using System.Threading;

namespace FileManager.Core.Transformers;

/// <summary>
/// The production <see cref="IProcessRunner"/>: launches the child with redirected stdout/stderr,
/// drains both pipes asynchronously (so a chatty child can never deadlock on a full pipe), enforces
/// the timeout, and on overrun kills the <b>entire process tree</b> so grandchildren are not orphaned
/// (§9 risk note). Reflection-free and AOT-clean.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>Per-stream capture cap. Output beyond this is dropped (the head is kept for diagnostics).</summary>
    public const int OutputCapBytes = 64 * 1024;

    public ProcessRunResult Run(ProcessLaunchSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
        };

        foreach (string arg in spec.Arguments)
            startInfo.ArgumentList.Add(arg);

        var stdout = new CappedBuffer(OutputCapBytes);
        var stderr = new CappedBuffer(OutputCapBytes);

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        int timeoutMs = ToWaitMilliseconds(spec.Timeout);
        bool exited = process.WaitForExit(timeoutMs);

        if (!exited)
        {
            TryKillTree(process);
            // Reap the killed process and let the async readers flush what they captured.
            process.WaitForExit();
            return new ProcessRunResult
            {
                ExitCode = -1,
                TimedOut = true,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString(),
            };
        }

        // WaitForExit(timeout) can return before the async output handlers have flushed; the
        // argument-less overload blocks until the redirected streams reach EOF.
        process.WaitForExit();

        return new ProcessRunResult
        {
            ExitCode = process.ExitCode,
            TimedOut = false,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
        };
    }

    private static int ToWaitMilliseconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return Timeout.Infinite;

        double ms = timeout.TotalMilliseconds;
        if (ms <= 0)
            return 0;
        return ms >= int.MaxValue ? int.MaxValue : (int)ms;
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process may have exited between the timeout check and the kill; nothing to do.
        }
    }

    /// <summary>A thread-safe line accumulator that stops growing once the byte cap is reached.</summary>
    private sealed class CappedBuffer(int capBytes)
    {
        private readonly StringBuilder _sb = new();
        private readonly object _lock = new();
        private bool _capped;

        public void AppendLine(string? data)
        {
            if (data is null)
                return; // null signals end-of-stream for the redirected reader.

            lock (_lock)
            {
                if (_capped)
                    return;

                _sb.Append(data).Append('\n');
                if (_sb.Length >= capBytes)
                {
                    _sb.Append("[output truncated]\n");
                    _capped = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_lock)
                return _sb.ToString();
        }
    }
}
