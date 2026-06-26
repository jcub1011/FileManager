using System.IO;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _dir;

    public ProfileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fp-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void LoadAllFrom_MissingDirectory_ReturnsEmpty()
    {
        IReadOnlyList<ProfileLoadResult> results =
            ProfileStore.LoadAllFrom(Path.Combine(_dir, "does-not-exist"));

        Assert.Empty(results);
    }

    [Fact]
    public void LoadAllFrom_ReportsValidAndInvalidPerFile()
    {
        // A valid profile (the §5.1 sample).
        File.WriteAllText(Path.Combine(_dir, "01-valid.json"), TestSamples.ReadProfileSampleJson());

        // A malformed-JSON profile.
        File.WriteAllText(Path.Combine(_dir, "02-malformed.json"), "{ not valid json ");

        // A well-formed but schema-invalid profile (MoveToArchive without ArchiveFolder).
        var bad = TestSamples.ParseProfileSample();
        ((System.Text.Json.Nodes.JsonObject)bad["Policies"]!)["OnSuccess"] = "MoveToArchive";
        File.WriteAllText(Path.Combine(_dir, "03-schema-invalid.json"), bad.ToJsonString());

        IReadOnlyList<ProfileLoadResult> results = ProfileStore.LoadAllFrom(_dir);

        Assert.Equal(3, results.Count);

        // Deterministic ordinal ordering by file name.
        Assert.True(results[0].IsValid);
        Assert.NotNull(results[0].Profile);

        Assert.False(results[1].IsValid);
        Assert.Null(results[1].Profile);

        Assert.False(results[2].IsValid);
        Assert.NotNull(results[2].Profile); // parsed, but failed validation
        Assert.Contains(results[2].Validation.Errors, e => e.Path.Contains("ArchiveFolder"));
    }
}
