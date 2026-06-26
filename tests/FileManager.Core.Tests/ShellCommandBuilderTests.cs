using FileManager.Core.Tokens;
using FileManager.Core.Transformers;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Unit-level coverage of the Shell-mode escaping. Building the string is deterministic and
/// platform-specific; we assert the platform's quoting rather than launching a live shell (cmd.exe
/// <c>/c</c> quoting is the best-effort area hardened in M9).
/// </summary>
public sealed class ShellCommandBuilderTests
{
    [Fact]
    public void Build_QuotesExecutable_AndSubstitutesTokenValue()
    {
        TokenContext ctx = TokenContext.ForFile("track.wav", "/root") with { StepInputPath = "/ws/track.wav" };

        string command = ShellCommandBuilder.Build("/usr/bin/tool", "--in $step_input_path", ctx);

        string expected = OperatingSystem.IsWindows()
            ? "\"/usr/bin/tool\" --in \"/ws/track.wav\""
            : "'/usr/bin/tool' --in '/ws/track.wav'";
        Assert.Equal(expected, command);
    }

    [Fact]
    public void Build_EscapesQuoteCharacterInsideTokenValue()
    {
        // A path carrying the platform's own quote character must be neutralized, not closed early.
        string hostile = OperatingSystem.IsWindows()
            ? "/ws/a\"b.wav"   // embedded double-quote
            : "/ws/a'b.wav";   // embedded single-quote
        TokenContext ctx = TokenContext.ForFile("track.wav", "/root") with { StepInputPath = hostile };

        string command = ShellCommandBuilder.Build("/usr/bin/tool", "$step_input_path", ctx);

        string expectedValue = OperatingSystem.IsWindows()
            ? "\"/ws/a\"\"b.wav\""        // " doubled, then wrapped
            : "'/ws/a'\\''b.wav'";        // ' closed, escaped, reopened
        Assert.EndsWith(expectedValue, command);
    }
}
