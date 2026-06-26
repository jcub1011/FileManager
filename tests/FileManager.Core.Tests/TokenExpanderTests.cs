using FileManager.Core.Tokens;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class TokenExpanderTests
{
    private static readonly TokenContext Ctx = TokenContext.ForFile("track.wav", @"C:\src\root");

    [Fact]
    public void Expand_KnownTokens()
    {
        Assert.Equal("track", TokenExpander.Expand("$filename_stem", Ctx));
        Assert.Equal(".wav", TokenExpander.Expand("$extension", Ctx));
        Assert.Equal("track.wav", TokenExpander.Expand("$filename_current", Ctx));
        Assert.Equal(@"C:\src\root", TokenExpander.Expand("$source_root_path", Ctx));
    }

    [Fact]
    public void Expand_CombinedAndSurroundingText()
    {
        Assert.Equal("pre-track.wav-post", TokenExpander.Expand("pre-$filename_stem$extension-post", Ctx));
    }

    [Fact]
    public void Expand_DollarEscape_ProducesLiteralDollar()
    {
        Assert.Equal("$filename_stem", TokenExpander.Expand("$$filename_stem", Ctx));
        Assert.Equal("a$b", TokenExpander.Expand("a$$b", Ctx));
    }

    [Fact]
    public void Expand_IsCaseSensitive_UnknownLeftVerbatim()
    {
        // Wrong case is not a known token, so it passes through unchanged.
        Assert.Equal("$FileName_Stem", TokenExpander.Expand("$FileName_Stem", Ctx));
    }

    [Fact]
    public void Expand_UnknownToken_LeftVerbatim()
    {
        Assert.Equal("$step_input_path", TokenExpander.Expand("$step_input_path", Ctx));
    }

    [Fact]
    public void Expand_BareDollar_LeftLiteral()
    {
        Assert.Equal("cost is $ 5", TokenExpander.Expand("cost is $ 5", Ctx));
    }

    [Fact]
    public void SplitName_HandlesNoExtension()
    {
        (string stem, string ext) = TokenExpander.SplitName("README");
        Assert.Equal("README", stem);
        Assert.Equal("", ext);
    }
}
