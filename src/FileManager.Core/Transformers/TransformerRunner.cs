using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Tokens;

namespace FileManager.Core.Transformers;

/// <summary>
/// Drives a Profile's ordered transformer chain (spec §4 Phase 3). On entry it makes a working copy
/// of the Source inside the per-Job <see cref="TempWorkspace"/> and then threads that working file
/// through each step in <see cref="TransformerStep.Step"/> order: a <see cref="OutputMode.NewFile"/>
/// step writes a fresh file that becomes the next step's input (the prior intermediate is freed), an
/// <see cref="OutputMode.InPlace"/> step mutates the current file and carries it forward. A non-zero
/// exit (outside <see cref="TransformerStep.SuccessExitCodes"/>) or a timeout aborts the chain; the
/// original Source is never touched and the caller tears the workspace down.
/// </summary>
public sealed class TransformerRunner(IFileOperations files, IProcessRunner processRunner)
{
    private static readonly IReadOnlyList<int> DefaultSuccessCodes = new[] { 0 };

    /// <summary>
    /// Runs <paramref name="steps"/> against <paramref name="sourcePath"/> inside
    /// <paramref name="workspace"/>. <paramref name="sourceRoot"/> is the owning Source root used for
    /// the <c>$source_root_path</c> token.
    /// </summary>
    public TransformerChainResult Run(
        TempWorkspace workspace,
        IReadOnlyList<TransformerStep> steps,
        string sourcePath,
        string sourceRoot)
    {
        var stepResults = new List<StepResult>();

        // Working copy on entry — every step operates on this, never the original Source.
        string fileName = Path.GetFileName(sourcePath);
        string currentInput = workspace.PathFor(fileName);
        try
        {
            AtomicFileWriter.Write(files, sourcePath, currentInput, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Abort(stepResults, $"Could not stage working copy: {ex.Message}");
        }

        TransformerStep[] ordered = steps.OrderBy(s => s.Step).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            TransformerStep step = ordered[i];

            // Basic "executable exists" gate; full path validation / allowlist lands in M9.
            if (!files.FileExists(step.ExecutablePath))
                return Abort(stepResults, $"Step {step.Step} ({step.Name}): executable not found: {step.ExecutablePath}");

            bool isNewFile = step.OutputMode == OutputMode.NewFile;
            string? outputPath = null;
            if (isNewFile)
            {
                if (string.IsNullOrWhiteSpace(step.ExpectedOutputExtension))
                    return Abort(stepResults, $"Step {step.Step} ({step.Name}): NewFile step has no ExpectedOutputExtension.");

                (string stem, _) = TokenExpander.SplitName(Path.GetFileName(currentInput));
                string outDir = workspace.PathFor($"step{step.Step}");
                files.CreateDirectory(outDir);
                outputPath = Path.Combine(outDir, stem + step.ExpectedOutputExtension);
            }

            TokenContext context = BuildContext(currentInput, sourceRoot, outputPath);
            ProcessLaunchSpec spec = BuildLaunchSpec(step, context, workspace.Root);

            ProcessRunResult run = processRunner.Run(spec);
            bool succeeded = !run.TimedOut && IsSuccessCode(run.ExitCode, step.SuccessExitCodes);

            stepResults.Add(new StepResult
            {
                Step = step.Step,
                Name = step.Name,
                Shell = step.ArgumentMode == ArgumentMode.Shell,
                ExitCode = run.ExitCode,
                TimedOut = run.TimedOut,
                Succeeded = succeeded,
                StandardOutput = run.StandardOutput,
                StandardError = run.StandardError,
            });

            if (!succeeded)
            {
                string why = run.TimedOut
                    ? $"timed out after {step.TimeoutSeconds}s"
                    : $"exited with code {run.ExitCode}";
                return Abort(stepResults, $"Step {step.Step} ({step.Name}) {why}.");
            }

            if (isNewFile)
            {
                if (!files.FileExists(outputPath!))
                    return Abort(stepResults, $"Step {step.Step} ({step.Name}) succeeded but produced no output file.");

                // The output becomes the next input; free the now-spent intermediate.
                TryDelete(currentInput);
                currentInput = outputPath!;
            }
            // InPlace: the same working file carries forward unchanged.
        }

        return new TransformerChainResult
        {
            Succeeded = true,
            FinalWorkingFile = currentInput,
            Steps = stepResults,
        };
    }

    private static TokenContext BuildContext(string currentInput, string sourceRoot, string? outputPath)
    {
        // Filename tokens reflect the CURRENT working file so an extension-changing step still expands
        // $filename_stem / $extension correctly for the next step.
        return TokenContext.ForFile(Path.GetFileName(currentInput), sourceRoot) with
        {
            StepInputPath = currentInput,
            StepOutputPath = outputPath,
        };
    }

    private static ProcessLaunchSpec BuildLaunchSpec(TransformerStep step, TokenContext context, string workingDir)
    {
        TimeSpan timeout = step.TimeoutSeconds > 0
            ? TimeSpan.FromSeconds(step.TimeoutSeconds)
            : System.Threading.Timeout.InfiniteTimeSpan;

        if (step.ArgumentMode == ArgumentMode.Shell)
        {
            string command = ShellCommandBuilder.Build(step.ExecutablePath, step.Arguments, context);
            return new ProcessLaunchSpec
            {
                ExecutablePath = ShellCommandBuilder.ShellPath,
                Arguments = new[] { ShellCommandBuilder.ShellCommandFlag, command },
                WorkingDirectory = workingDir,
                Timeout = timeout,
            };
        }

        return new ProcessLaunchSpec
        {
            ExecutablePath = step.ExecutablePath,
            Arguments = ArgumentParser.Parse(step.Arguments, context),
            WorkingDirectory = workingDir,
            Timeout = timeout,
        };
    }

    private static bool IsSuccessCode(int exitCode, IReadOnlyList<int>? successCodes)
    {
        IReadOnlyList<int> codes = successCodes is { Count: > 0 } ? successCodes : DefaultSuccessCodes;
        return codes.Contains(exitCode);
    }

    private void TryDelete(string path)
    {
        try
        {
            files.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The workspace is torn down wholesale anyway; a stuck intermediate is not fatal.
        }
    }

    private static TransformerChainResult Abort(IReadOnlyList<StepResult> steps, string reason) => new()
    {
        Succeeded = false,
        FailureReason = reason,
        Steps = steps,
    };
}
