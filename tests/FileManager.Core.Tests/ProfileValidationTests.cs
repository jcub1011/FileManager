using System.Text.Json.Nodes;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class ProfileValidationTests
{
    [Fact]
    public void Sample_IsValid()
    {
        Profile profile = ProfileSerializer.Deserialize(TestSamples.ReadProfileSampleJson())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void MissingRequiredField_FailsAtDeserialization()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        doc.Remove("Name");

        bool ok = ProfileSerializer.TryDeserializeProfile(doc.ToJsonString(), out Profile? profile, out string? error);

        Assert.False(ok);
        Assert.Null(profile);
        Assert.Contains("Name", error);
    }

    [Fact]
    public void UnknownEnumValue_FailsAtDeserialization()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        doc["SyncMode"] = "TotallyNotARealMode";

        bool ok = ProfileSerializer.TryDeserializeProfile(doc.ToJsonString(), out Profile? profile, out string? error);

        Assert.False(ok);
        Assert.Null(profile);
        Assert.NotNull(error);
    }

    [Fact]
    public void MoveToArchive_WithoutArchiveFolder_IsInvalid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        var policies = (JsonObject)doc["Policies"]!;
        policies["OnSuccess"] = "MoveToArchive";
        policies["ArchiveFolder"] = null;

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("ArchiveFolder"));
    }

    [Fact]
    public void NewFileStep_WithoutExpectedExtension_IsInvalid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        var step1 = (JsonObject)((JsonArray)doc["Transformers"]!)[0]!;
        Assert.Equal("NewFile", (string?)step1["OutputMode"]); // precondition
        step1.Remove("ExpectedOutputExtension");

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("ExpectedOutputExtension"));
    }

    [Fact]
    public void WrongSchemaVersion_IsInvalid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        doc["SchemaVersion"] = 1;

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == nameof(Profile.SchemaVersion));
    }

    [Fact]
    public void EnabledSchedule_WithoutCron_IsInvalid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        var schedule = (JsonObject)((JsonObject)doc["Triggers"]!)["Schedule"]!;
        schedule["Enabled"] = true;
        schedule["Cron"] = null;

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("Cron"));
    }
}
