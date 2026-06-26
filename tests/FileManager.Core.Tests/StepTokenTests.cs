using FileManager.Core.Tokens;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class StepTokenTests
{
    private static TokenContext StepContext(string input, string? output = null) =>
        TokenContext.ForFile("track.wav", "/root") with { StepInputPath = input, StepOutputPath = output };

    [Fact]
    public void StepInputPath_ResolvesToAbsolutePath()
    {
        TokenContext ctx = StepContext("/ws/track.wav");
        Assert.Equal("/ws/track.wav", TokenExpander.Expand("$step_input_path", ctx));
    }

    [Fact]
    public void StepOutputPath_ResolvesToAbsolutePath()
    {
        TokenContext ctx = StepContext("/ws/track.wav", "/ws/out/track.mp3");
        Assert.Equal("/ws/out/track.mp3", TokenExpander.Expand("$step_output_path", ctx));
    }

    [Fact]
    public void StepTokens_AreCaseSensitive()
    {
        TokenContext ctx = StepContext("/ws/track.wav");
        // Wrong casing is not a known token, so it is left verbatim (§5.2).
        Assert.Equal("$Step_Input_Path", TokenExpander.Expand("$Step_Input_Path", ctx));
    }

    [Fact]
    public void StepTokens_OutsideStepContext_AreLeftVerbatim()
    {
        // A plain filename context (M1 distribution) has no step paths; the token must not expand.
        TokenContext ctx = TokenContext.ForFile("track.wav", "/root");
        Assert.Equal("$step_input_path", TokenExpander.Expand("$step_input_path", ctx));
        Assert.Equal("$step_output_path", TokenExpander.Expand("$step_output_path", ctx));
    }

    [Fact]
    public void FilenameAndSourceRootTokens_StillResolveInStepContext()
    {
        TokenContext ctx = StepContext("/ws/track.wav");
        Assert.Equal("track", TokenExpander.Expand("$filename_stem", ctx));
        Assert.Equal(".wav", TokenExpander.Expand("$extension", ctx));
        Assert.Equal("/root", TokenExpander.Expand("$source_root_path", ctx));
    }
}
