using FileManager.Core.Profiles;

namespace FileManager.Core.Tests;

/// <summary>Builds <see cref="TransformerStep"/> instances for Phase-3 tests with terse defaults.</summary>
internal static class TestTransformers
{
    public static TransformerStep NewFile(
        int step,
        string executablePath,
        string arguments,
        string expectedExtension,
        ArgumentMode mode = ArgumentMode.Literal,
        IReadOnlyList<int>? successCodes = null,
        int timeoutSeconds = 30) => new()
        {
            Step = step,
            Name = $"step{step}",
            ExecutablePath = executablePath,
            ArgumentMode = mode,
            Arguments = arguments,
            OutputMode = OutputMode.NewFile,
            ExpectedOutputExtension = expectedExtension,
            SuccessExitCodes = successCodes,
            TimeoutSeconds = timeoutSeconds,
        };

    public static TransformerStep InPlace(
        int step,
        string executablePath,
        string arguments,
        ArgumentMode mode = ArgumentMode.Literal,
        IReadOnlyList<int>? successCodes = null,
        int timeoutSeconds = 30) => new()
        {
            Step = step,
            Name = $"step{step}",
            ExecutablePath = executablePath,
            ArgumentMode = mode,
            Arguments = arguments,
            OutputMode = OutputMode.InPlace,
            ExpectedOutputExtension = null,
            SuccessExitCodes = successCodes,
            TimeoutSeconds = timeoutSeconds,
        };
}
