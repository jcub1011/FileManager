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
    public void TransformerStep_WithNonPositiveTimeout_IsInvalid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        var step1 = (JsonObject)((JsonArray)doc["Transformers"]!)[0]!;
        step1["TimeoutSeconds"] = 0;

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("TimeoutSeconds"));
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
    public void EnabledSchedule_WithoutCronOrInterval_IsInvalid()
    {
        // M5: an enabled schedule must set exactly one of Cron / IntervalSeconds; neither is invalid.
        JsonObject doc = TestSamples.ParseProfileSample();
        var schedule = (JsonObject)((JsonObject)doc["Triggers"]!)["Schedule"]!;
        schedule["Enabled"] = true;
        schedule["Cron"] = null;

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("Schedule"));
    }

    [Fact]
    public void EnabledSchedule_WithBothCronAndInterval_IsInvalid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        var schedule = (JsonObject)((JsonObject)doc["Triggers"]!)["Schedule"]!;
        schedule["Enabled"] = true;
        schedule["Cron"] = "0 * * * *";
        schedule["IntervalSeconds"] = 60;

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("Schedule"));
    }

    [Fact]
    public void EnabledSchedule_WithValidInterval_IsValid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        var schedule = (JsonObject)((JsonObject)doc["Triggers"]!)["Schedule"]!;
        schedule["Enabled"] = true;
        schedule["Cron"] = null;
        schedule["IntervalSeconds"] = 300;

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.True(result.IsValid, string.Join("|", result.Errors));
    }

    [Fact]
    public void EnabledSchedule_WithNonPositiveInterval_IsInvalid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        var schedule = (JsonObject)((JsonObject)doc["Triggers"]!)["Schedule"]!;
        schedule["Enabled"] = true;
        schedule["Cron"] = null;
        schedule["IntervalSeconds"] = 0;

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("IntervalSeconds"));
    }

    [Fact]
    public void EnabledSchedule_WithMalformedCron_IsInvalid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        var schedule = (JsonObject)((JsonObject)doc["Triggers"]!)["Schedule"]!;
        schedule["Enabled"] = true;
        schedule["Cron"] = "this is not cron";

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("Cron"));
    }

    [Fact]
    public void InvalidExcludeRegex_IsInvalid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        var filters = (JsonObject)doc["Filters"]!;
        filters["ExcludeRegex"] = new JsonArray("*.secret"); // leading quantifier → malformed regex

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("ExcludeRegex"));
    }

    [Fact]
    public void InvalidAgeDuration_IsInvalid()
    {
        JsonObject doc = TestSamples.ParseProfileSample();
        var filters = (JsonObject)doc["Filters"]!;
        filters["ModifiedWithin"] = "3M"; // uppercase unit is not accepted (ambiguous months/minutes)

        Profile profile = ProfileSerializer.Deserialize(doc.ToJsonString())!;
        ValidationResult result = ProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("ModifiedWithin"));
    }
}
