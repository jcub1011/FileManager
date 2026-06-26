using FileManager.Core.Profiles;

namespace FileManager.Core.Tests;

/// <summary>Builds <see cref="Profile"/> instances for engine/routing tests with sensible M1 defaults.</summary>
internal static class TestProfiles
{
    public static PolicySet DefaultPolicies { get; } = new()
    {
        ConflictResolution = ConflictResolution.Overwrite,
        OverwriteHandling = OverwriteHandling.DirectOverwrite,
        VerificationMethod = VerificationMethod.None,
        OnSuccess = OnSuccess.KeepSource,
        ArchiveFolder = null,
        OnFailure = OnFailure.AbortRestoreAndClean,
        MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
    };

    public static FilterSet EmptyFilters { get; } = new();

    public static Profile Build(
        IReadOnlyList<string> sources,
        IReadOnlyList<string> targets,
        TargetLayout layout = TargetLayout.PreserveStructure,
        PolicySet? policies = null,
        FilterSet? filters = null,
        IReadOnlyList<FilterSet?>? perSourceFilters = null,
        Verbosity verbosity = Verbosity.All)
    {
        var sourceSpecs = new List<SourceSpec>();
        for (int i = 0; i < sources.Count; i++)
        {
            sourceSpecs.Add(new SourceSpec
            {
                Path = sources[i],
                SettleDelaySeconds = 0,
                StabilityIntervalMs = 0,
                Filters = perSourceFilters is not null && i < perSourceFilters.Count ? perSourceFilters[i] : null,
            });
        }

        return new Profile
        {
            SchemaVersion = 2,
            ProfileId = "test-profile",
            Name = "Test Profile",
            Active = true,
            SyncMode = SyncMode.AdditiveArchive,
            TargetLayout = layout,
            Triggers = new TriggerSet { ManualShell = true, Watcher = false, Schedule = null },
            Sources = sourceSpecs,
            Transformers = null,
            Targets = targets.Select(t => new TargetSpec { Path = t }).ToList(),
            Policies = policies ?? DefaultPolicies,
            Filters = filters ?? EmptyFilters,
            Logging = new LoggingSpec { Verbosity = verbosity, NotifyOnFailure = false },
        };
    }
}
