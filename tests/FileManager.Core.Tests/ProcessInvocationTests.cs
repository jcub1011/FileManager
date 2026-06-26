using System.Diagnostics;
using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Tokens;
using FileManager.Core.Transformers;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Exercises the real <see cref="SystemProcessRunner"/> against the genuine stub executable — the
/// honest end-to-end / timeout / injection-immunity coverage the §12 criteria call for.
/// </summary>
public sealed class ProcessInvocationTests : IDisposable
{
    private readonly TempDir _temp = new("proc");
    private readonly SystemFileOperations _files = new();
    private readonly SystemProcessRunner _runner = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TwoStepChain_RunsEndToEnd_OnStub()
    {
        string s = _temp.MakeDir("S");
        string src = _temp.WriteFile("S/track.wav", "audio");
        TransformerStep[] steps =
        {
            TestTransformers.NewFile(1, StubExecutable.Path, "copy $step_input_path $step_output_path", ".mp3"),
            TestTransformers.InPlace(2, StubExecutable.Path, "tag $step_input_path"),
        };

        using TempWorkspace ws = TempWorkspace.Create(_files, _temp.Path("pipe"), "job");
        TransformerChainResult result = new TransformerRunner(_files, _runner).Run(ws, steps, src, s);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.EndsWith(".mp3", result.FinalWorkingFile);
        Assert.Equal("audio[tagged]", File.ReadAllText(result.FinalWorkingFile!));

        // stdout from the copy step and stderr from the tag step are both captured.
        Assert.Contains(result.Steps, st => st.StandardOutput.Contains("copied"));
        Assert.Contains(result.Steps, st => st.StandardError.Contains("tagged"));
    }

    [Fact]
    public void Step_ExceedingTimeout_IsKilled()
    {
        string s = _temp.MakeDir("S");
        string src = _temp.WriteFile("S/a.txt", "x");
        TransformerStep[] steps = { TestTransformers.InPlace(1, StubExecutable.Path, "sleep 30", timeoutSeconds: 1) };

        using TempWorkspace ws = TempWorkspace.Create(_files, _temp.Path("pipe"), "job");
        var sw = Stopwatch.StartNew();
        TransformerChainResult result = new TransformerRunner(_files, _runner).Run(ws, steps, src, s);
        sw.Stop();

        Assert.False(result.Succeeded);
        Assert.True(result.Steps[0].TimedOut);
        Assert.Contains("timed out", result.FailureReason);
        // The 30s sleep was cut short by the 1s timeout, proving the kill fired.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public void NonZeroExit_RealProcess_AbortsChain()
    {
        string s = _temp.MakeDir("S");
        string src = _temp.WriteFile("S/a.txt", "x");
        TransformerStep[] steps = { TestTransformers.InPlace(1, StubExecutable.Path, "exit 7") };

        using TempWorkspace ws = TempWorkspace.Create(_files, _temp.Path("pipe"), "job");
        TransformerChainResult result = new TransformerRunner(_files, _runner).Run(ws, steps, src, s);

        Assert.False(result.Succeeded);
        Assert.Contains("code 7", result.FailureReason);
    }

    [Fact]
    public void LiteralMode_InjectionAttempt_ArrivesAsSingleArgument()
    {
        // A source file whose name carries shell-injection payload (Windows-legal: no " < > | * ? : / \).
        const string hostileName = "evil; rm -rf $(pwd) && echo pwned.txt";
        string s = _temp.MakeDir("S");
        string input = _temp.WriteFile($"S/{hostileName}", "data");
        string dumpFile = _temp.Path("argv.txt");

        TokenContext ctx = TokenContext.ForFile(Path.GetFileName(input), s) with { StepInputPath = input };
        // The dump-file path is quoted in the template (it may contain spaces); the hostile input rides
        // a token, so it is substituted as one element regardless of its contents.
        IReadOnlyList<string> argv = ArgumentParser.Parse($"dumpargs \"{dumpFile}\" $step_input_path", ctx);

        ProcessRunResult run = _runner.Run(new ProcessLaunchSpec
        {
            ExecutablePath = StubExecutable.Path,
            Arguments = argv,
            Timeout = TimeSpan.FromSeconds(30),
        });

        Assert.Equal(0, run.ExitCode);
        string[] received = File.ReadAllLines(dumpFile);
        Assert.Single(received);              // exactly one trailing argument reached the process
        Assert.Equal(input, received[0]);     // intact and unmodified — no re-splitting, no expansion
    }
}
