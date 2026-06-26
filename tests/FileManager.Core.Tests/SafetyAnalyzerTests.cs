using FileManager.Core.Profiles;
using FileManager.Core.Safety;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class SafetyAnalyzerTests
{
    private static Profile WithPolicies(VerificationMethod verify, OnSuccess onSuccess)
    {
        PolicySet policies = TestProfiles.DefaultPolicies with
        {
            VerificationMethod = verify,
            OnSuccess = onSuccess,
        };
        return TestProfiles.Build(new[] { @"C:\S" }, new[] { @"C:\T" }, policies: policies);
    }

    [Fact]
    public void None_With_PermanentDelete_IsBlocking()
    {
        SafetyAssessment a = SafetyAnalyzer.Evaluate(
            WithPolicies(VerificationMethod.None, OnSuccess.PermanentDelete));

        Assert.Equal(SafetyLevel.Blocking, a.Level);
        Assert.NotNull(a.Reason);
    }

    [Fact]
    public void None_With_MoveToTrash_IsWarning()
    {
        SafetyAssessment a = SafetyAnalyzer.Evaluate(
            WithPolicies(VerificationMethod.None, OnSuccess.MoveToTrash));

        Assert.Equal(SafetyLevel.Warning, a.Level);
        Assert.NotNull(a.Reason);
    }

    [Theory]
    [InlineData(VerificationMethod.None, OnSuccess.KeepSource)]
    [InlineData(VerificationMethod.None, OnSuccess.MoveToArchive)]
    [InlineData(VerificationMethod.SHA256, OnSuccess.PermanentDelete)]
    [InlineData(VerificationMethod.SizeTimestamp, OnSuccess.MoveToTrash)]
    [InlineData(VerificationMethod.SHA256, OnSuccess.KeepSource)]
    public void OtherCombinations_AreSafe(VerificationMethod verify, OnSuccess onSuccess)
    {
        SafetyAssessment a = SafetyAnalyzer.Evaluate(WithPolicies(verify, onSuccess));

        Assert.Equal(SafetyLevel.Safe, a.Level);
        Assert.Null(a.Reason);
    }
}
