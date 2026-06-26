using System.Text.RegularExpressions;
using FileManager.Core.Filtering;

namespace FileManager.Core.Profiles;

/// <summary>
/// Validates an already-deserialized <see cref="Profile"/> against the spec §5.1 rules,
/// returning a structured <see cref="ValidationResult"/> rather than throwing. Enum-membership
/// and "required property absent" errors are caught earlier, at the JSON boundary
/// (<see cref="ProfileSerializer.TryDeserializeProfile"/>); this validator covers value-level
/// and cross-field rules on a successfully-parsed document.
/// </summary>
public static class ProfileValidator
{
    /// <summary>The schema version this build understands.</summary>
    public const int SupportedSchemaVersion = 2;

    /// <summary>Validates <paramref name="profile"/> and returns all problems found.</summary>
    public static ValidationResult Validate(Profile profile)
    {
        var errors = new List<ValidationError>();

        if (profile.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add(new ValidationError(
                nameof(Profile.SchemaVersion),
                $"Unsupported SchemaVersion {profile.SchemaVersion}; this build supports {SupportedSchemaVersion}."));
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileId))
            errors.Add(new ValidationError(nameof(Profile.ProfileId), "ProfileId is required and must be non-empty."));

        if (string.IsNullOrWhiteSpace(profile.Name))
            errors.Add(new ValidationError(nameof(Profile.Name), "Name is required and must be non-empty."));

        ValidateSources(profile, errors);
        ValidateTargets(profile, errors);
        ValidatePolicies(profile, errors);
        ValidateTransformers(profile, errors);
        ValidateSchedule(profile, errors);
        ValidateFilters(profile, errors);

        return errors.Count == 0 ? ValidationResult.Success : new ValidationResult(errors);
    }

    private static void ValidateSources(Profile profile, List<ValidationError> errors)
    {
        if (profile.Sources.Count == 0)
        {
            errors.Add(new ValidationError(nameof(Profile.Sources), "At least one Source is required."));
            return;
        }

        for (int i = 0; i < profile.Sources.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(profile.Sources[i].Path))
                errors.Add(new ValidationError($"Sources[{i}].Path", "Source Path must be non-empty."));
        }
    }

    private static void ValidateTargets(Profile profile, List<ValidationError> errors)
    {
        if (profile.Targets.Count == 0)
        {
            errors.Add(new ValidationError(nameof(Profile.Targets), "At least one Target is required."));
            return;
        }

        for (int i = 0; i < profile.Targets.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(profile.Targets[i].Path))
                errors.Add(new ValidationError($"Targets[{i}].Path", "Target Path must be non-empty."));
        }
    }

    private static void ValidatePolicies(Profile profile, List<ValidationError> errors)
    {
        // Cross-field: MoveToArchive requires an ArchiveFolder.
        if (profile.Policies.OnSuccess == OnSuccess.MoveToArchive
            && string.IsNullOrWhiteSpace(profile.Policies.ArchiveFolder))
        {
            errors.Add(new ValidationError(
                $"{nameof(Profile.Policies)}.{nameof(PolicySet.ArchiveFolder)}",
                "ArchiveFolder is required when OnSuccess is MoveToArchive."));
        }
    }

    private static void ValidateTransformers(Profile profile, List<ValidationError> errors)
    {
        if (profile.Transformers is null)
            return;

        for (int i = 0; i < profile.Transformers.Count; i++)
        {
            var step = profile.Transformers[i];
            string prefix = $"Transformers[{i}]";

            if (string.IsNullOrWhiteSpace(step.Name))
                errors.Add(new ValidationError($"{prefix}.Name", "Transformer Name must be non-empty."));

            if (string.IsNullOrWhiteSpace(step.ExecutablePath))
                errors.Add(new ValidationError($"{prefix}.ExecutablePath", "Transformer ExecutablePath must be non-empty."));

            // Cross-field: a NewFile step must declare its output extension.
            if (step.OutputMode == OutputMode.NewFile && string.IsNullOrWhiteSpace(step.ExpectedOutputExtension))
            {
                errors.Add(new ValidationError(
                    $"{prefix}.ExpectedOutputExtension",
                    "ExpectedOutputExtension is required when OutputMode is NewFile."));
            }

            // A non-positive timeout would disable the spec-mandated forced timeout (§4 Phase 3),
            // letting a hung transformer hang the Job forever; require an explicit positive value.
            if (step.TimeoutSeconds <= 0)
            {
                errors.Add(new ValidationError(
                    $"{prefix}.TimeoutSeconds",
                    "TimeoutSeconds must be greater than zero."));
            }
        }
    }

    private static void ValidateSchedule(Profile profile, List<ValidationError> errors)
    {
        var schedule = profile.Triggers.Schedule;
        if (schedule is { Enabled: true } && string.IsNullOrWhiteSpace(schedule.Cron))
        {
            errors.Add(new ValidationError(
                $"{nameof(Profile.Triggers)}.{nameof(TriggerSet.Schedule)}.{nameof(ScheduleTrigger.Cron)}",
                "Cron is required when the Schedule trigger is enabled."));
        }
    }

    /// <summary>
    /// Validates regex patterns and age-duration strings in the global and per-Source
    /// <see cref="FilterSet"/>s. A malformed regex or duration would otherwise be swallowed at
    /// evaluation time (an excluded file leaking through, or an age filter silently disabled), so
    /// per the "validation, not exceptions" rule we surface it here at load time instead.
    /// </summary>
    private static void ValidateFilters(Profile profile, List<ValidationError> errors)
    {
        ValidateFilterSet(profile.Filters, nameof(Profile.Filters), errors);

        for (int i = 0; i < profile.Sources.Count; i++)
        {
            FilterSet? perSource = profile.Sources[i].Filters;
            if (perSource is not null)
                ValidateFilterSet(perSource, $"Sources[{i}].Filters", errors);
        }
    }

    private static void ValidateFilterSet(FilterSet filters, string prefix, List<ValidationError> errors)
    {
        ValidateRegexList(filters.IncludeRegex, $"{prefix}.{nameof(FilterSet.IncludeRegex)}", errors);
        ValidateRegexList(filters.ExcludeRegex, $"{prefix}.{nameof(FilterSet.ExcludeRegex)}", errors);

        ValidateDuration(filters.ModifiedWithin, $"{prefix}.{nameof(FilterSet.ModifiedWithin)}", errors);
        ValidateDuration(filters.ModifiedOlderThan, $"{prefix}.{nameof(FilterSet.ModifiedOlderThan)}", errors);
        ValidateDuration(filters.CreatedWithin, $"{prefix}.{nameof(FilterSet.CreatedWithin)}", errors);
    }

    private static void ValidateRegexList(IReadOnlyList<string>? patterns, string prefix, List<ValidationError> errors)
    {
        if (patterns is null)
            return;

        for (int i = 0; i < patterns.Count; i++)
        {
            try
            {
                _ = Regex.Match(string.Empty, patterns[i]);
            }
            catch (RegexParseException ex)
            {
                errors.Add(new ValidationError($"{prefix}[{i}]", $"Invalid regex pattern: {ex.Message}"));
            }
        }
    }

    private static void ValidateDuration(string? value, string path, List<ValidationError> errors)
    {
        if (value is not null && !DurationParser.TryParse(value, out _))
            errors.Add(new ValidationError(path, $"Invalid duration '{value}'; expected e.g. \"7d\", \"24h\", \"30m\", \"45s\"."));
    }
}
