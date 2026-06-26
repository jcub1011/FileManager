using System.Text.Json;
using FileManager.Core.Configuration;

namespace FileManager.Core.Profiles;

/// <summary>
/// Thin, AOT-safe façade over <see cref="ProfileJsonContext"/> for (de)serializing
/// <see cref="Profile"/> and <see cref="ServiceConfig"/> documents. All JSON access in the
/// codebase should go through here so options stay in one place.
/// </summary>
public static class ProfileSerializer
{
    /// <summary>Serializes a Profile to indented JSON with PascalCase names and explicit nulls.</summary>
    public static string Serialize(Profile profile) =>
        JsonSerializer.Serialize(profile, ProfileJsonContext.Default.Profile);

    /// <summary>Serializes a ServiceConfig to indented JSON.</summary>
    public static string Serialize(ServiceConfig config) =>
        JsonSerializer.Serialize(config, ProfileJsonContext.Default.ServiceConfig);

    /// <summary>
    /// Deserializes a Profile. Throws <see cref="JsonException"/> on malformed JSON, an unknown
    /// enum value, or a missing required property — callers that need structured errors should
    /// use <see cref="TryDeserializeProfile"/>.
    /// </summary>
    public static Profile? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, ProfileJsonContext.Default.Profile);

    /// <summary>Deserializes a ServiceConfig. Throws <see cref="JsonException"/> on malformed JSON.</summary>
    public static ServiceConfig? DeserializeServiceConfig(string json) =>
        JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ServiceConfig);

    /// <summary>
    /// Attempts to deserialize a Profile, capturing any JSON error (malformed document, unknown
    /// enum value, missing required property) as a message instead of throwing.
    /// </summary>
    public static bool TryDeserializeProfile(string json, out Profile? profile, out string? error)
    {
        try
        {
            profile = Deserialize(json);
            if (profile is null)
            {
                error = "Document deserialized to null.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            profile = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Attempts to deserialize a ServiceConfig, capturing any JSON error as a message.</summary>
    public static bool TryDeserializeServiceConfig(string json, out ServiceConfig? config, out string? error)
    {
        try
        {
            config = DeserializeServiceConfig(json);
            if (config is null)
            {
                error = "Document deserialized to null.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            config = null;
            error = ex.Message;
            return false;
        }
    }
}
