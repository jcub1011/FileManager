using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Verification;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class VerifierTests : IDisposable
{
    private readonly TempDir _temp = new("verify");
    private readonly SystemFileOperations _files = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void None_AlwaysPasses_EvenForDifferentFiles()
    {
        string a = _temp.WriteFile("a.txt", "one");
        string b = _temp.WriteFile("b.txt", "completely different");

        VerificationResult r = new NoneVerifier().Verify(a, b);

        Assert.True(r.Ok);
    }

    [Fact]
    public void Sha256_PassesForIdenticalContent()
    {
        string a = _temp.WriteFile("a.txt", "identical bytes");
        string b = _temp.WriteFile("b.txt", "identical bytes");

        VerificationResult r = new Sha256Verifier(_files).Verify(a, b);

        Assert.True(r.Ok);
    }

    [Fact]
    public void Sha256_FailsForCorruptedCopy()
    {
        string good = _temp.WriteFile("good.txt", "the original content");
        string corrupt = _temp.WriteFile("corrupt.txt", "the corrupted conten!");

        VerificationResult r = new Sha256Verifier(_files).Verify(good, corrupt);

        Assert.False(r.Ok);
        Assert.Contains("SHA256 mismatch", r.Reason);
    }

    [Fact]
    public void SizeTimestamp_FailsOnSizeMismatch()
    {
        string a = _temp.WriteFile("a.txt", "short");
        string b = _temp.WriteFile("b.txt", "much longer content here");

        VerificationResult r = new SizeTimestampVerifier(_files).Verify(a, b);

        Assert.False(r.Ok);
        Assert.Contains("size mismatch", r.Reason);
    }

    [Fact]
    public void SizeTimestamp_PassesWhenWithinToleranceWindow()
    {
        // Same size; timestamps 1 second apart with a 2-second tolerance ⇒ within window.
        string a = _temp.WriteFile("a.txt", "12345");
        string b = _temp.WriteFile("b.txt", "67890");
        var baseTime = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, baseTime);
        File.SetLastWriteTimeUtc(b, baseTime.AddSeconds(1));

        VerificationResult r = new SizeTimestampVerifier(_files, TimeSpan.FromSeconds(2)).Verify(a, b);

        Assert.True(r.Ok);
    }

    [Fact]
    public void SizeTimestamp_FailsWhenOutsideToleranceWindow()
    {
        // Same size; timestamps 5 seconds apart with a 2-second tolerance ⇒ outside window.
        string a = _temp.WriteFile("a.txt", "12345");
        string b = _temp.WriteFile("b.txt", "67890");
        var baseTime = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, baseTime);
        File.SetLastWriteTimeUtc(b, baseTime.AddSeconds(5));

        VerificationResult r = new SizeTimestampVerifier(_files, TimeSpan.FromSeconds(2)).Verify(a, b);

        Assert.False(r.Ok);
        Assert.Contains("last-write", r.Reason);
    }

    [Fact]
    public void Factory_MapsMethodToImplementation()
    {
        Assert.IsType<SizeTimestampVerifier>(
            VerifierFactory.Create(Profiles.VerificationMethod.SizeTimestamp, _files));
        Assert.IsType<Sha256Verifier>(
            VerifierFactory.Create(Profiles.VerificationMethod.SHA256, _files));
        Assert.IsType<NoneVerifier>(
            VerifierFactory.Create(Profiles.VerificationMethod.None, _files));
    }
}
