using System.ComponentModel;
using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Transformers;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class TransformerRunnerTests : IDisposable
{
    private readonly TempDir _temp = new("xform");
    private readonly SystemFileOperations _files = new();

    public void Dispose() => _temp.Dispose();

    private TempWorkspace NewWorkspace() => TempWorkspace.Create(_files, _temp.Path("pipe"), "job1");

    // Mirrors the stub, but in-process: routes on the (expanded) mode token so the runner's NewFile
    // output-existence check sees a genuinely produced file.
    private static ProcessRunResult Simulate(ProcessLaunchSpec spec)
    {
        IReadOnlyList<string> a = spec.Arguments;
        switch (a[0])
        {
            case "copy":
                File.Copy(a[1], a[2], overwrite: true);
                return FakeProcessRunner.Ok(stdout: "copied");
            case "tag":
                File.AppendAllText(a[1], "[tagged]");
                return FakeProcessRunner.Ok(stderr: "tagged");
            default:
                return FakeProcessRunner.Ok();
        }
    }

    [Fact]
    public void NewFileThenInPlace_ProducesTaggedOutput_AndFreesIntermediate()
    {
        string s = _temp.MakeDir("S");
        string src = _temp.WriteFile("S/track.wav", "audio");
        TransformerStep[] steps =
        {
            TestTransformers.NewFile(1, StubExecutable.Path, "copy $step_input_path $step_output_path", ".mp3"),
            TestTransformers.InPlace(2, StubExecutable.Path, "tag $step_input_path"),
        };

        using TempWorkspace ws = NewWorkspace();
        var runner = new TransformerRunner(_files, new FakeProcessRunner(Simulate));
        TransformerChainResult result = runner.Run(ws, steps, src, s);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotNull(result.FinalWorkingFile);
        Assert.EndsWith(".mp3", result.FinalWorkingFile);
        Assert.Equal("audio[tagged]", File.ReadAllText(result.FinalWorkingFile!));

        // The step-1 input (the working copy of the source) is freed once its NewFile output exists.
        Assert.False(File.Exists(Path.Combine(ws.Root, "track.wav")));
        // The original source is never mutated.
        Assert.Equal("audio", File.ReadAllText(src));
    }

    [Fact]
    public void NonZeroExit_AbortsChain()
    {
        string s = _temp.MakeDir("S");
        string src = _temp.WriteFile("S/a.txt", "x");
        TransformerStep[] steps = { TestTransformers.InPlace(1, StubExecutable.Path, "exit 3") };

        using TempWorkspace ws = NewWorkspace();
        var runner = new TransformerRunner(_files, new FakeProcessRunner(_ => FakeProcessRunner.Exit(3)));
        TransformerChainResult result = runner.Run(ws, steps, src, s);

        Assert.False(result.Succeeded);
        Assert.Contains("code 3", result.FailureReason);
        Assert.Single(result.Steps);
        Assert.False(result.Steps[0].Succeeded);
    }

    [Fact]
    public void Timeout_AbortsChain()
    {
        string s = _temp.MakeDir("S");
        string src = _temp.WriteFile("S/a.txt", "x");
        TransformerStep[] steps = { TestTransformers.InPlace(1, StubExecutable.Path, "sleep 99", timeoutSeconds: 1) };

        using TempWorkspace ws = NewWorkspace();
        var runner = new TransformerRunner(_files, new FakeProcessRunner(_ => FakeProcessRunner.TimedOut()));
        TransformerChainResult result = runner.Run(ws, steps, src, s);

        Assert.False(result.Succeeded);
        Assert.Contains("timed out", result.FailureReason);
        Assert.True(result.Steps[0].TimedOut);
    }

    [Fact]
    public void SuccessExitCodes_AllowConfiguredNonZeroExit()
    {
        string s = _temp.MakeDir("S");
        string src = _temp.WriteFile("S/a.txt", "x");
        TransformerStep[] steps =
        {
            TestTransformers.InPlace(1, StubExecutable.Path, "exit 2", successCodes: new[] { 0, 2 }),
        };

        using TempWorkspace ws = NewWorkspace();
        var runner = new TransformerRunner(_files, new FakeProcessRunner(_ => FakeProcessRunner.Exit(2)));
        TransformerChainResult result = runner.Run(ws, steps, src, s);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(result.Steps[0].Succeeded);
    }

    [Fact]
    public void MissingExecutable_AbortsBeforeLaunch()
    {
        string s = _temp.MakeDir("S");
        string src = _temp.WriteFile("S/a.txt", "x");
        TransformerStep[] steps =
        {
            TestTransformers.InPlace(1, _temp.Path("does-not-exist.exe"), "tag $step_input_path"),
        };

        using TempWorkspace ws = NewWorkspace();
        var fake = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        TransformerChainResult result = new TransformerRunner(_files, fake).Run(ws, steps, src, s);

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.FailureReason);
        Assert.Empty(fake.Calls); // never launched
    }

    [Fact]
    public void LaunchFailure_AbortsChain_WithoutThrowing()
    {
        // The executable exists (passes the existence gate) but the runner cannot launch it — e.g. a
        // non-executable file. The thrown launch fault must resolve to a graceful chain abort.
        string s = _temp.MakeDir("S");
        string src = _temp.WriteFile("S/a.txt", "x");
        TransformerStep[] steps = { TestTransformers.InPlace(1, StubExecutable.Path, "tag $step_input_path") };

        using TempWorkspace ws = NewWorkspace();
        var runner = new TransformerRunner(
            _files, new FakeProcessRunner(_ => throw new Win32Exception("cannot launch")));
        TransformerChainResult result = runner.Run(ws, steps, src, s);

        Assert.False(result.Succeeded);
        Assert.Contains("could not launch", result.FailureReason);
    }
}
