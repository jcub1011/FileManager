namespace FileManager.Core.Transformers;

/// <summary>
/// How a transformer step's child process should be launched. The runner is mode-agnostic: it always
/// receives a concrete executable plus an already-built <see cref="Arguments"/> argv list. Literal
/// steps put the parsed step argv here directly; Shell steps put the OS shell as
/// <see cref="ExecutablePath"/> and <c>["/c", command]</c> (Windows) / <c>["-c", command]</c> (Unix)
/// as the arguments. This keeps the security-sensitive argv/shell decision in the caller, not the
/// process layer.
/// </summary>
public sealed record ProcessLaunchSpec
{
    /// <summary>The executable to launch (the step's executable, or the OS shell for Shell mode).</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The argument vector, each element passed as a single, un-split argument.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Working directory for the child (the per-Job temp workspace), or null for inherited.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Wall-clock limit before the child (and its whole process tree) is killed. Use
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for no limit.
    /// </summary>
    public required TimeSpan Timeout { get; init; }
}

/// <summary>The outcome of one child-process run: exit code, timeout flag, and captured output.</summary>
public sealed record ProcessRunResult
{
    /// <summary>Process exit code. Undefined (typically non-zero) when <see cref="TimedOut"/> is true.</summary>
    public required int ExitCode { get; init; }

    /// <summary>True when the child exceeded its timeout and was killed.</summary>
    public required bool TimedOut { get; init; }

    /// <summary>Captured stdout, truncated to the runner's size cap.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Captured stderr, truncated to the runner's size cap.</summary>
    public required string StandardError { get; init; }
}

/// <summary>
/// Launches a transformer step as a child process and waits for it under a timeout. The seam lets the
/// engine drive deterministic orchestration tests with a fake while production uses
/// <see cref="SystemProcessRunner"/>.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="spec"/> to completion (or timeout) and returns the result.</summary>
    public ProcessRunResult Run(ProcessLaunchSpec spec);
}
