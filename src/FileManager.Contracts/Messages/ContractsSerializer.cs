using System.Text;
using System.Text.Json;

namespace FileManager.Contracts.Messages;

/// <summary>
/// Thin, AOT-safe façade over <see cref="ContractsJsonContext"/> for (de)serializing the
/// <see cref="IpcMessage"/> envelope, mirroring the shape of the engine's <c>ProfileSerializer</c>. All
/// IPC JSON access goes through here so the options live in one place.
/// </summary>
public static class ContractsSerializer
{
    /// <summary>Serializes an envelope to its UTF-8 JSON bytes (the framed payload).</summary>
    public static byte[] SerializeToUtf8Bytes(IpcMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, ContractsJsonContext.Default.IpcMessage);

    /// <summary>Serializes an envelope to a JSON string (diagnostics/tests).</summary>
    public static string Serialize(IpcMessage message) =>
        JsonSerializer.Serialize(message, ContractsJsonContext.Default.IpcMessage);

    /// <summary>
    /// Deserializes an envelope from UTF-8 JSON bytes. Throws <see cref="JsonException"/> on malformed
    /// JSON — transport callers use <see cref="TryDeserialize(System.ReadOnlySpan{byte}, out IpcMessage?, out string?)"/>
    /// to handle a bad frame without crashing the connection.
    /// </summary>
    public static IpcMessage? Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, ContractsJsonContext.Default.IpcMessage);

    /// <summary>
    /// Attempts to deserialize an envelope from UTF-8 JSON bytes, capturing any JSON error as a message
    /// instead of throwing, so a connection handler can log + close on a corrupt frame.
    /// </summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> utf8Json, out IpcMessage? message, out string? error)
    {
        try
        {
            message = Deserialize(utf8Json);
            if (message is null)
            {
                error = "Message deserialized to null.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            message = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Attempts to deserialize an envelope from a JSON string (diagnostics/tests).</summary>
    public static bool TryDeserialize(string json, out IpcMessage? message, out string? error) =>
        TryDeserialize(Encoding.UTF8.GetBytes(json), out message, out error);
}
