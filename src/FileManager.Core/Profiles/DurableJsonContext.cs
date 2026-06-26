using System.Text.Json;
using System.Text.Json.Serialization;
using FileManager.Core.Audit;
using FileManager.Core.Journal;

namespace FileManager.Core.Profiles;

/// <summary>
/// A second source-generation root for the M4 durable append-logs (the journal and deletion audit
/// trail), configured with <c>WriteIndented = false</c> so each record/entry serializes as a single
/// compact line. The journal frames records by length so indentation would not break it, but the audit
/// trail is one-entry-per-line — compact output keeps both formats tight and greppable. Kept separate
/// from <see cref="ProfileJsonContext"/> (which writes indented Profiles/config) so each surface keeps
/// its own on-disk shape; both stay AOT-clean (no reflection).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(JournalRecord))]
[JsonSerializable(typeof(AuditEntry))]
public partial class DurableJsonContext : JsonSerializerContext
{
}
