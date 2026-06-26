using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileManager.Core.Profiles;

namespace FileManager.Core.Configuration;

/// <summary>Outcome of loading the global <see cref="ServiceConfig"/>.</summary>
/// <param name="Config">
/// The loaded config, or a defaults instance when the file is absent or could not be parsed.
/// </param>
/// <param name="Validation">Validation problems found in a present file (empty when absent/valid).</param>
/// <param name="FileExisted">Whether a <c>config.json</c> file was found.</param>
public sealed record ServiceConfigLoadResult(
    ServiceConfig Config,
    ValidationResult Validation,
    bool FileExisted)
{
    /// <summary>True when the resulting config is safe to use (defaults are always valid).</summary>
    public bool IsValid => Validation.IsValid;
}

/// <summary>
/// Loads the global <see cref="ServiceConfig"/> from <c>config.json</c>, supplying a defaults
/// instance when the file is absent. Validated like Profiles; never throws on a bad file.
/// </summary>
public static class ServiceConfigStore
{
    /// <summary>Loads the config from the default config-dir location.</summary>
    public static ServiceConfigLoadResult Load() => LoadFrom(ConfigPaths.GetConfigFilePath());

    /// <summary>Loads the config from a specific file path.</summary>
    public static ServiceConfigLoadResult LoadFrom(string configFilePath)
    {
        if (!File.Exists(configFilePath))
            return new ServiceConfigLoadResult(new ServiceConfig(), ValidationResult.Success, FileExisted: false);

        string json;
        try
        {
            json = File.ReadAllText(configFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ServiceConfigLoadResult(
                new ServiceConfig(),
                ValidationResult.Fail(configFilePath, $"Could not read config file: {ex.Message}"),
                FileExisted: true);
        }

        if (!ProfileSerializer.TryDeserializeServiceConfig(json, out var config, out string? error))
        {
            return new ServiceConfigLoadResult(
                new ServiceConfig(),
                ValidationResult.Fail(configFilePath, $"Invalid config JSON: {error}"),
                FileExisted: true);
        }

        ServiceConfig resolved = ApplyDefaultsForAbsentKeys(config!, json);
        return new ServiceConfigLoadResult(resolved, Validate(resolved), FileExisted: true);
    }

    /// <summary>
    /// Fills documented defaults for the value-type fields whose JSON keys were omitted. STJ
    /// deserialization does not run the record's property initializers, so an absent numeric key
    /// would otherwise come back as 0. Checking key presence distinguishes "omitted" (use default)
    /// from "explicitly set to an invalid value" (left for <see cref="Validate"/> to flag).
    /// </summary>
    private static ServiceConfig ApplyDefaultsForAbsentKeys(ServiceConfig config, string json)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return config;
        }

        if (obj is null)
            return config;

        var defaults = new ServiceConfig();
        return config with
        {
            MaxWorkers = obj.ContainsKey(nameof(ServiceConfig.MaxWorkers))
                ? config.MaxWorkers : defaults.MaxWorkers,
            LogRotationSizeBytes = obj.ContainsKey(nameof(ServiceConfig.LogRotationSizeBytes))
                ? config.LogRotationSizeBytes : defaults.LogRotationSizeBytes,
            AuditRotationSizeBytes = obj.ContainsKey(nameof(ServiceConfig.AuditRotationSizeBytes))
                ? config.AuditRotationSizeBytes : defaults.AuditRotationSizeBytes,
            JournalRotationSizeBytes = obj.ContainsKey(nameof(ServiceConfig.JournalRotationSizeBytes))
                ? config.JournalRotationSizeBytes : defaults.JournalRotationSizeBytes,
            MinFreeSpaceMarginBytes = obj.ContainsKey(nameof(ServiceConfig.MinFreeSpaceMarginBytes))
                ? config.MinFreeSpaceMarginBytes : defaults.MinFreeSpaceMarginBytes,
        };
    }

    /// <summary>Validates a loaded config's value-level rules.</summary>
    public static ValidationResult Validate(ServiceConfig config)
    {
        var errors = new List<ValidationError>();

        if (config.MaxWorkers < 1)
            errors.Add(new ValidationError(nameof(ServiceConfig.MaxWorkers), "MaxWorkers must be at least 1."));

        if (config.LogRotationSizeBytes <= 0)
            errors.Add(new ValidationError(nameof(ServiceConfig.LogRotationSizeBytes), "LogRotationSizeBytes must be positive."));

        if (config.AuditRotationSizeBytes <= 0)
            errors.Add(new ValidationError(nameof(ServiceConfig.AuditRotationSizeBytes), "AuditRotationSizeBytes must be positive."));

        if (config.JournalRotationSizeBytes <= 0)
            errors.Add(new ValidationError(nameof(ServiceConfig.JournalRotationSizeBytes), "JournalRotationSizeBytes must be positive."));

        if (config.MinFreeSpaceMarginBytes < 0)
            errors.Add(new ValidationError(nameof(ServiceConfig.MinFreeSpaceMarginBytes), "MinFreeSpaceMarginBytes must not be negative."));

        return errors.Count == 0 ? ValidationResult.Success : new ValidationResult(errors);
    }
}
