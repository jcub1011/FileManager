using FileManager.Core.Tokens;
using FileManager.Core.Transformers;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class ArgumentParserTests
{
    private static TokenContext Context(
        string fileName = "track.wav",
        string? stepInput = null,
        string? stepOutput = null) =>
        TokenContext.ForFile(fileName, "/root") with { StepInputPath = stepInput, StepOutputPath = stepOutput };

    [Fact]
    public void Parse_SplitsOnWhitespace_AndExpandsTokens()
    {
        TokenContext ctx = Context(stepInput: "/ws/in/track.wav", stepOutput: "/ws/out/track.mp3");

        IReadOnlyList<string> argv = ArgumentParser.Parse(
            "-i $step_input_path -b:a 320k $step_output_path", ctx);

        Assert.Equal(
            new[] { "-i", "/ws/in/track.wav", "-b:a", "320k", "/ws/out/track.mp3" },
            argv);
    }

    [Fact]
    public void Parse_TokenValueWithSpacesAndMetacharacters_StaysSingleArgument()
    {
        // The precursor to the §12 injection-immunity criterion: a hostile path expands into exactly
        // one argv element and is never re-split on its spaces, quotes, ';', or '$(...)'.
        string hostile = "/ws/evil; rm -rf $(pwd) && echo pwned.wav";
        TokenContext ctx = Context(stepInput: hostile);

        IReadOnlyList<string> argv = ArgumentParser.Parse("tag $step_input_path", ctx);

        Assert.Equal(2, argv.Count);
        Assert.Equal("tag", argv[0]);
        Assert.Equal(hostile, argv[1]);
    }

    [Fact]
    public void Parse_DoubleDollar_ProducesLiteralDollar()
    {
        IReadOnlyList<string> argv = ArgumentParser.Parse("a$$b", Context());

        Assert.Single(argv);
        Assert.Equal("a$b", argv[0]);
    }

    [Fact]
    public void Parse_QuotedRun_GroupsIntoOneElement()
    {
        TokenContext ctx = Context(fileName: "my track.wav");

        IReadOnlyList<string> argv = ArgumentParser.Parse("--name=\"$filename_current\"", ctx);

        Assert.Single(argv);
        Assert.Equal("--name=my track.wav", argv[0]);
    }

    [Fact]
    public void Parse_ExtensionChangingStep_KeepsOutputTokenCorrect()
    {
        // The working file is still track.wav entering the step; $step_output_path carries the .mp3
        // target the engine computed from ExpectedOutputExtension.
        TokenContext ctx = Context(stepInput: "/ws/track.wav", stepOutput: "/ws/step1/track.mp3");

        IReadOnlyList<string> argv = ArgumentParser.Parse("copy $step_input_path $step_output_path", ctx);

        Assert.Equal(new[] { "copy", "/ws/track.wav", "/ws/step1/track.mp3" }, argv);
    }
}
