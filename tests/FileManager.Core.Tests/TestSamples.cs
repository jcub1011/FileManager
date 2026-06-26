using System.IO;
using System.Text.Json.Nodes;

namespace FileManager.Core.Tests;

/// <summary>Helpers for locating and manipulating the §5.1 sample Profile in tests.</summary>
internal static class TestSamples
{
    /// <summary>Path to the verbatim §5.1 sample Profile (copied next to the test assembly).</summary>
    public static string ProfileSamplePath =>
        Path.Combine(AppContext.BaseDirectory, "Samples", "profile-v2-sample.json");

    /// <summary>Raw JSON text of the §5.1 sample.</summary>
    public static string ReadProfileSampleJson() => File.ReadAllText(ProfileSamplePath);

    /// <summary>Parses the §5.1 sample into a mutable <see cref="JsonObject"/> for tweaking.</summary>
    public static JsonObject ParseProfileSample() =>
        (JsonObject)JsonNode.Parse(ReadProfileSampleJson())!;

    /// <summary>
    /// Recursively removes object properties whose value is JSON <c>null</c>, so that an explicit
    /// <c>"X": null</c> and an omitted <c>X</c> compare equal. Returns a new normalized node.
    /// </summary>
    public static JsonNode? NormalizeDroppingNulls(string json)
    {
        JsonNode? node = JsonNode.Parse(json);
        StripNulls(node);
        return node;
    }

    private static void StripNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var nullKeys = new List<string>();
                foreach (var kvp in obj)
                {
                    if (kvp.Value is null)
                        nullKeys.Add(kvp.Key);
                    else
                        StripNulls(kvp.Value);
                }
                foreach (string key in nullKeys)
                    obj.Remove(key);
                break;

            case JsonArray arr:
                foreach (JsonNode? item in arr)
                    StripNulls(item);
                break;
        }
    }
}
