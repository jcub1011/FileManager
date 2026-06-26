using System.Text.Json.Nodes;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class ProfileSerializationTests
{
    [Fact]
    public void Deserialize_Sample_PopulatesEveryField()
    {
        Profile profile = ProfileSerializer.Deserialize(TestSamples.ReadProfileSampleJson())!;

        Assert.NotNull(profile);
        Assert.Equal(2, profile.SchemaVersion);
        Assert.Equal("e2a3c4b5-1234-4a5b-8c9d-0123456789ab", profile.ProfileId);
        Assert.Equal("Chained Audio Optimization Pipeline", profile.Name);
        Assert.True(profile.Active);
        Assert.Equal(SyncMode.AdditiveArchive, profile.SyncMode);
        Assert.Equal(TargetLayout.PreserveStructure, profile.TargetLayout);

        // Triggers + nested schedule
        Assert.True(profile.Triggers.ManualShell);
        Assert.True(profile.Triggers.Watcher);
        Assert.NotNull(profile.Triggers.Schedule);
        Assert.False(profile.Triggers.Schedule!.Enabled);
        Assert.Equal("0 */6 * * *", profile.Triggers.Schedule.Cron);
        Assert.Equal("America/Chicago", profile.Triggers.Schedule.Timezone);
        Assert.Equal(MissedRunPolicy.CatchUpOnce, profile.Triggers.Schedule.MissedRunPolicy);

        // Sources
        Assert.Single(profile.Sources);
        Assert.Equal(@"C:\dropzone\raw", profile.Sources[0].Path);
        Assert.Equal(2, profile.Sources[0].SettleDelaySeconds);
        Assert.Equal(500, profile.Sources[0].StabilityIntervalMs);
        Assert.Null(profile.Sources[0].Filters);

        // Transformers
        Assert.Equal(2, profile.Transformers!.Count);
        TransformerStep step1 = profile.Transformers[0];
        Assert.Equal(1, step1.Step);
        Assert.Equal("FFMPEG Audio Transcoder", step1.Name);
        Assert.Equal(@"C:\tools\ffmpeg.exe", step1.ExecutablePath);
        Assert.Equal(ArgumentMode.Literal, step1.ArgumentMode);
        Assert.Equal("-i $step_input_path -b:a 320k $step_output_path", step1.Arguments);
        Assert.Equal(OutputMode.NewFile, step1.OutputMode);
        Assert.Equal(".mp3", step1.ExpectedOutputExtension);
        Assert.Equal(new[] { 0 }, step1.SuccessExitCodes);
        Assert.Equal(120, step1.TimeoutSeconds);

        TransformerStep step2 = profile.Transformers[1];
        Assert.Equal(OutputMode.InPlace, step2.OutputMode);
        Assert.Null(step2.ExpectedOutputExtension);
        Assert.Null(step2.SuccessExitCodes);

        // Targets
        Assert.Equal(2, profile.Targets.Count);
        Assert.Equal(@"C:\archive\local", profile.Targets[0].Path);
        Assert.Equal(@"Z:\vault", profile.Targets[1].Path);

        // Policies
        Assert.Equal(ConflictResolution.RenameSuffix, profile.Policies.ConflictResolution);
        Assert.Equal(OverwriteHandling.StageOverwrites, profile.Policies.OverwriteHandling);
        Assert.Equal(VerificationMethod.SHA256, profile.Policies.VerificationMethod);
        Assert.Equal(OnSuccess.MoveToTrash, profile.Policies.OnSuccess);
        Assert.Null(profile.Policies.ArchiveFolder);
        Assert.Equal(OnFailure.AbortRestoreAndClean, profile.Policies.OnFailure);
        Assert.Equal(MetadataOnConflict.WarnAndContinue, profile.Policies.MetadataOnConflict);

        // Filters
        Assert.Equal(new[] { "*.wav", "*.flac" }, profile.Filters.Include);
        Assert.Equal(new[] { ".DS_Store", "Thumbs.db" }, profile.Filters.ExcludeGlob);
        Assert.Null(profile.Filters.IncludeRegex);
        Assert.Null(profile.Filters.MinSizeBytes);
        Assert.NotNull(profile.Filters.Attributes);
        Assert.False(profile.Filters.Attributes!.IncludeHidden);
        Assert.False(profile.Filters.Attributes.FollowSymlinks);
        Assert.Null(profile.Filters.MaxDepth);
        Assert.False(profile.Filters.ContentHashDedupe);

        // Logging
        Assert.Equal(Verbosity.FailuresAndSkips, profile.Logging.Verbosity);
        Assert.True(profile.Logging.NotifyOnFailure);
    }

    [Fact]
    public void RoundTrip_PreservesSampleContent()
    {
        // Sample -> Profile -> JSON should be semantically equal to the authored sample,
        // treating explicit nulls and omitted optional properties as equivalent.
        string sampleJson = TestSamples.ReadProfileSampleJson();
        Profile profile = ProfileSerializer.Deserialize(sampleJson)!;
        string reserialized = ProfileSerializer.Serialize(profile);

        JsonNode? original = TestSamples.NormalizeDroppingNulls(sampleJson);
        JsonNode? actual = TestSamples.NormalizeDroppingNulls(reserialized);

        Assert.True(
            JsonNode.DeepEquals(original, actual),
            $"Round-trip differed.\nExpected:\n{original}\nActual:\n{actual}");
    }

    [Fact]
    public void RoundTrip_IsStableFixpoint()
    {
        // Serializing twice must be byte-identical: the serializer is a stable fixpoint.
        Profile first = ProfileSerializer.Deserialize(TestSamples.ReadProfileSampleJson())!;
        string json1 = ProfileSerializer.Serialize(first);
        Profile second = ProfileSerializer.Deserialize(json1)!;
        string json2 = ProfileSerializer.Serialize(second);

        Assert.Equal(json1, json2);
    }
}
