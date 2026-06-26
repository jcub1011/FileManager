using System.Text.Json;
using System.Text.Json.Serialization;
using FileManager.Core.Configuration;

namespace FileManager.Core.Profiles;

/// <summary>
/// <see cref="JsonSerializerContext"/> source-generation root for all FileManager JSON models.
/// Using the source generator (rather than reflection-based serialization) keeps the engine
/// library AOT-clean. <see cref="JsonSerializerDefaults.General"/> behavior with PascalCase
/// property names preserves the on-disk schema exactly; nulls are written (not omitted) so a
/// Profile round-trips unchanged.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(ServiceConfig))]
public partial class ProfileJsonContext : JsonSerializerContext
{
}
