using FileManager.Core.Filtering;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("*.wav", "song.wav", true)]
    [InlineData("*.wav", "song.flac", false)]
    [InlineData("*.WAV", "song.wav", true)]      // case-insensitive
    [InlineData("Thumbs.db", "Thumbs.db", true)]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file12.txt", false)]
    public void IsMatch(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, name));
    }

    [Fact]
    public void MatchesAny_TrueWhenAnyPatternMatches()
    {
        string[] patterns = { "*.wav", "*.flac" };
        Assert.True(GlobMatcher.MatchesAny(patterns, "a.flac"));
        Assert.False(GlobMatcher.MatchesAny(patterns, "a.mp3"));
    }
}
