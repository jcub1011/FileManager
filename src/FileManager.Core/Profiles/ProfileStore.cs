using System.IO;

namespace FileManager.Core.Profiles;

/// <summary>Outcome of loading a single Profile file.</summary>
/// <param name="FilePath">The file that was read.</param>
/// <param name="Profile">The deserialized Profile, or null when parsing failed.</param>
/// <param name="Validation">JSON and schema validation problems (empty when fully valid).</param>
public sealed record ProfileLoadResult(string FilePath, Profile? Profile, ValidationResult Validation)
{
    /// <summary>True when the file parsed and passed validation.</summary>
    public bool IsValid => Profile is not null && Validation.IsValid;
}

/// <summary>
/// Discovers and loads <c>profiles/*.json</c> files, returning each loaded Profile paired with its
/// per-file validation result. Never throws on a malformed or unreadable file — problems are
/// reported as data so the engine can surface them and continue.
/// </summary>
public static class ProfileStore
{
    /// <summary>Loads every <c>*.json</c> in the default profiles directory.</summary>
    public static IReadOnlyList<ProfileLoadResult> LoadAll() =>
        LoadAllFrom(Configuration.ConfigPaths.GetProfilesDirectory());

    /// <summary>
    /// Loads every <c>*.json</c> in <paramref name="profilesDirectory"/> (sorted by file name for
    /// deterministic ordering). Returns an empty list when the directory does not exist.
    /// </summary>
    public static IReadOnlyList<ProfileLoadResult> LoadAllFrom(string profilesDirectory)
    {
        if (!Directory.Exists(profilesDirectory))
            return Array.Empty<ProfileLoadResult>();

        string[] files;
        try
        {
            files = Directory.GetFiles(profilesDirectory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ProfileLoadResult>();
        }

        Array.Sort(files, StringComparer.Ordinal);

        var results = new List<ProfileLoadResult>(files.Length);
        foreach (string file in files)
            results.Add(LoadFile(file));

        return results;
    }

    /// <summary>Loads and validates a single Profile file.</summary>
    public static ProfileLoadResult LoadFile(string filePath)
    {
        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProfileLoadResult(filePath, null, ValidationResult.Fail(filePath, $"Could not read file: {ex.Message}"));
        }

        if (!ProfileSerializer.TryDeserializeProfile(json, out var profile, out string? error))
            return new ProfileLoadResult(filePath, null, ValidationResult.Fail(filePath, $"Invalid Profile JSON: {error}"));

        return new ProfileLoadResult(filePath, profile, ProfileValidator.Validate(profile!));
    }
}
