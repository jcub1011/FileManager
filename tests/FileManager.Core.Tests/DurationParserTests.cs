using FileManager.Core.Filtering;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class DurationParserTests
{
    [Theory]
    [InlineData("7d", 7 * 24 * 60 * 60)]
    [InlineData("24h", 24 * 60 * 60)]
    [InlineData("30m", 30 * 60)]
    [InlineData("45s", 45)]
    [InlineData(" 2d ", 2 * 24 * 60 * 60)]
    public void TryParse_ValidUnits_ReturnsExpectedSeconds(string text, double expectedSeconds)
    {
        Assert.True(DurationParser.TryParse(text, out TimeSpan duration));
        Assert.Equal(expectedSeconds, duration.TotalSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("7")]      // no unit
    [InlineData("d")]      // no magnitude
    [InlineData("-3d")]    // negative
    [InlineData("3w")]     // unsupported unit
    [InlineData("3M")]     // uppercase: ambiguous (months?), not minutes
    [InlineData("7D")]     // uppercase units rejected (lowercase only, per spec)
    [InlineData("24H")]
    [InlineData("45S")]
    [InlineData("abc")]
    public void TryParse_Invalid_ReturnsFalse(string? text)
    {
        Assert.False(DurationParser.TryParse(text, out _));
    }
}
