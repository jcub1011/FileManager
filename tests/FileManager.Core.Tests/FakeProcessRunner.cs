using FileManager.Core.Transformers;

namespace FileManager.Core.Tests;

/// <summary>
/// A deterministic <see cref="IProcessRunner"/> for orchestration tests: it records every launch and
/// delegates the outcome to a supplied behavior, so a test can simulate exits, timeouts, and output
/// without spawning a real process.
/// </summary>
internal sealed class FakeProcessRunner(Func<ProcessLaunchSpec, ProcessRunResult> behavior) : IProcessRunner
{
    public List<ProcessLaunchSpec> Calls { get; } = new();

    public ProcessRunResult Run(ProcessLaunchSpec spec)
    {
        Calls.Add(spec);
        return behavior(spec);
    }

    public static ProcessRunResult Ok(string stdout = "", string stderr = "") =>
        new() { ExitCode = 0, TimedOut = false, StandardOutput = stdout, StandardError = stderr };

    public static ProcessRunResult Exit(int code) =>
        new() { ExitCode = code, TimedOut = false, StandardOutput = string.Empty, StandardError = string.Empty };

    public static ProcessRunResult TimedOut() =>
        new() { ExitCode = -1, TimedOut = true, StandardOutput = string.Empty, StandardError = string.Empty };
}
