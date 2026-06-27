using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileManager.Contracts.Messages;

/// <summary>
/// <see cref="JsonSerializerContext"/> source-generation root for the IPC wire DTOs. Mirroring the
/// engine's <c>ProfileJsonContext</c>, this keeps <see cref="FileManager.Contracts"/> AOT-clean and
/// reflection-free: every message type (the <see cref="IpcMessage"/> envelope and each payload) is
/// registered so the transport (de)serializes without reflection. This context is defined IN Contracts
/// and references no Core types, so Contracts stays dependency-free.
/// </summary>
/// <remarks>
/// The <see cref="MessageKind"/> discriminator serializes as its string name (via
/// <see cref="JsonStringEnumConverter{TEnum}"/> on the property) so the wire form is stable and
/// human-readable. Compact (not indented) output keeps frames small over the local stream.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Default,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(IpcMessage))]
[JsonSerializable(typeof(SubmitPayload))]
[JsonSerializable(typeof(SubmitPayloadResult))]
[JsonSerializable(typeof(EngineStateQuery))]
[JsonSerializable(typeof(EngineState))]
[JsonSerializable(typeof(SubscribeEvents))]
[JsonSerializable(typeof(JobEvent))]
[JsonSerializable(typeof(ListProfiles))]
[JsonSerializable(typeof(ProfileSummary))]
[JsonSerializable(typeof(ProfileList))]
[JsonSerializable(typeof(ReloadProfiles))]
[JsonSerializable(typeof(ReloadResult))]
[JsonSerializable(typeof(DryRunRequest))]
[JsonSerializable(typeof(DryRunMatchDto))]
[JsonSerializable(typeof(DryRunScreenedOutDto))]
[JsonSerializable(typeof(DryRunCommandDto))]
[JsonSerializable(typeof(DryRunTargetWriteDto))]
[JsonSerializable(typeof(DryRunDeletionDto))]
[JsonSerializable(typeof(DryRunDispositionDto))]
[JsonSerializable(typeof(DryRunReport))]
[JsonSerializable(typeof(ManualInvocationPending))]
[JsonSerializable(typeof(ResolveManualInvocation))]
public partial class ContractsJsonContext : JsonSerializerContext
{
}
