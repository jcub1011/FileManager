namespace FileManager.Core.Profiles;

/// <summary>
/// One external CLI invocation in a Profile's ordered processing chain
/// (spec §5.1 <c>Transformers[]</c>, §4 Phase 3).
/// </summary>
public sealed record TransformerStep
{
    /// <summary>1-based position of this step in the chain.</summary>
    public required int Step { get; init; }

    /// <summary>Human-friendly name for logs/UI.</summary>
    public required string Name { get; init; }

    /// <summary>Path to the executable to invoke. Must resolve to an existing file (validated in M9).</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>How <see cref="Arguments"/> is interpreted (literal argv vs shell).</summary>
    public required ArgumentMode ArgumentMode { get; init; }

    /// <summary>Argument string, with <c>$step_input_path</c> / <c>$step_output_path</c> tokens.</summary>
    public required string Arguments { get; init; }

    /// <summary>Whether this step writes a new file or edits its input in place.</summary>
    public required OutputMode OutputMode { get; init; }

    /// <summary>
    /// Expected output extension. Required when <see cref="OutputMode"/> is
    /// <see cref="Profiles.OutputMode.NewFile"/>; null/omitted for in-place steps.
    /// </summary>
    public string? ExpectedOutputExtension { get; init; }

    /// <summary>Exit codes treated as success. Null/empty defaults to <c>{0}</c> (applied in M2).</summary>
    public IReadOnlyList<int>? SuccessExitCodes { get; init; }

    /// <summary>Maximum wall-clock seconds before the step is killed.</summary>
    public int TimeoutSeconds { get; init; }
}
