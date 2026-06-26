namespace FileManager.Core.Transformers;

/// <summary>Diagnostics for one executed transformer step, surfaced to the Job log.</summary>
public sealed record StepResult
{
    /// <summary>The step's 1-based position (from <see cref="Profiles.TransformerStep.Step"/>).</summary>
    public required int Step { get; init; }

    /// <summary>The step's display name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the step used the higher-risk <see cref="Profiles.ArgumentMode.Shell"/> path.</summary>
    public required bool Shell { get; init; }

    /// <summary>Process exit code (<c>-1</c> when timed out).</summary>
    public required int ExitCode { get; init; }

    /// <summary>True when the step was killed for exceeding its timeout.</summary>
    public required bool TimedOut { get; init; }

    /// <summary>Whether the step counted as success (exit code in the allowed set, no timeout).</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Captured stdout (size-capped by the runner).</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Captured stderr (size-capped by the runner).</summary>
    public required string StandardError { get; init; }
}

/// <summary>
/// The outcome of running a Profile's whole transformer chain: success plus the final working file,
/// or an abort reason — always with the per-step diagnostics gathered so far.
/// </summary>
public sealed record TransformerChainResult
{
    /// <summary>True when every step succeeded and a final working file was produced.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Absolute path of the file to distribute (present only when <see cref="Succeeded"/>).</summary>
    public string? FinalWorkingFile { get; init; }

    /// <summary>Human-readable abort reason (present only when not <see cref="Succeeded"/>).</summary>
    public string? FailureReason { get; init; }

    /// <summary>Per-step diagnostics in execution order, for logging.</summary>
    public required IReadOnlyList<StepResult> Steps { get; init; }
}
