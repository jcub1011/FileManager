namespace FileManager.Contracts.Messages;

/// <summary>
/// The discriminator for a wire message: identifies which payload field of <see cref="IpcMessage"/> is
/// populated so the dispatcher can route without polymorphic (reflection-based) deserialization.
/// </summary>
public enum MessageKind
{
    /// <summary>Unset / unknown — an envelope that failed to specify its kind. Never dispatched.</summary>
    Unknown = 0,

    /// <summary>Carries a <see cref="IpcMessage.Submit"/> payload (client → service).</summary>
    Submit,

    /// <summary>Carries a <see cref="IpcMessage.SubmitResult"/> payload (service → client).</summary>
    SubmitResult,

    /// <summary>Carries a <see cref="IpcMessage.StateQuery"/> payload (client → service).</summary>
    StateQuery,

    /// <summary>Carries a <see cref="IpcMessage.State"/> payload (service → client).</summary>
    State,

    /// <summary>Carries a <see cref="IpcMessage.Subscribe"/> payload (client → service).</summary>
    Subscribe,

    /// <summary>Carries a <see cref="IpcMessage.Event"/> payload (service → client, pushed).</summary>
    Event,

    /// <summary>Carries a <see cref="IpcMessage.ListProfiles"/> payload (client → service).</summary>
    ListProfiles,

    /// <summary>Carries a <see cref="IpcMessage.ProfileList"/> payload (service → client).</summary>
    ProfileList,

    /// <summary>Carries a <see cref="IpcMessage.ReloadProfiles"/> payload (client → service).</summary>
    ReloadProfiles,

    /// <summary>Carries a <see cref="IpcMessage.ReloadResult"/> payload (service → client).</summary>
    ReloadResult,

    /// <summary>Carries a <see cref="IpcMessage.DryRun"/> payload (client → service).</summary>
    DryRun,

    /// <summary>Carries a <see cref="IpcMessage.DryRunReport"/> payload (service → client).</summary>
    DryRunReport,

    /// <summary>Carries a <see cref="IpcMessage.ManualInvocationPending"/> payload (service → client, pushed).</summary>
    ManualInvocationPending,

    /// <summary>Carries a <see cref="IpcMessage.ResolveManualInvocation"/> payload (client → service).</summary>
    ResolveManualInvocation,
}

/// <summary>
/// The single wire message envelope used over the IPC transport (spec §2.1 length-prefixed JSON). To
/// stay AOT-clean we deliberately avoid polymorphic serialization: the envelope is one concrete record
/// carrying a <see cref="Kind"/> discriminator plus a set of <em>nullable typed payload fields</em>, of
/// which exactly one is populated for a given <see cref="Kind"/>. Every field is a registered
/// source-gen type, so the whole message round-trips through <see cref="ContractsJsonContext"/> with no
/// reflection. The static factories build a correctly-tagged envelope for each payload.
/// </summary>
public sealed record IpcMessage
{
    /// <summary>Which payload this envelope carries.</summary>
    public required MessageKind Kind { get; init; }

    /// <summary>The <see cref="MessageKind.Submit"/> payload, when <see cref="Kind"/> is Submit.</summary>
    public SubmitPayload? Submit { get; init; }

    /// <summary>The <see cref="MessageKind.SubmitResult"/> payload.</summary>
    public SubmitPayloadResult? SubmitResult { get; init; }

    /// <summary>The <see cref="MessageKind.StateQuery"/> payload.</summary>
    public EngineStateQuery? StateQuery { get; init; }

    /// <summary>The <see cref="MessageKind.State"/> payload.</summary>
    public EngineState? State { get; init; }

    /// <summary>The <see cref="MessageKind.Subscribe"/> payload.</summary>
    public SubscribeEvents? Subscribe { get; init; }

    /// <summary>The <see cref="MessageKind.Event"/> payload.</summary>
    public JobEvent? Event { get; init; }

    /// <summary>The <see cref="MessageKind.ListProfiles"/> payload.</summary>
    public ListProfiles? ListProfiles { get; init; }

    /// <summary>The <see cref="MessageKind.ProfileList"/> payload.</summary>
    public ProfileList? ProfileList { get; init; }

    /// <summary>The <see cref="MessageKind.ReloadProfiles"/> payload.</summary>
    public ReloadProfiles? ReloadProfiles { get; init; }

    /// <summary>The <see cref="MessageKind.ReloadResult"/> payload.</summary>
    public ReloadResult? ReloadResult { get; init; }

    /// <summary>The <see cref="MessageKind.DryRun"/> payload.</summary>
    public DryRunRequest? DryRun { get; init; }

    /// <summary>The <see cref="MessageKind.DryRunReport"/> payload.</summary>
    public DryRunReport? DryRunReport { get; init; }

    /// <summary>The <see cref="MessageKind.ManualInvocationPending"/> payload.</summary>
    public ManualInvocationPending? ManualInvocationPending { get; init; }

    /// <summary>The <see cref="MessageKind.ResolveManualInvocation"/> payload.</summary>
    public ResolveManualInvocation? ResolveManualInvocation { get; init; }

    /// <summary>Wraps a <see cref="SubmitPayload"/> as a <see cref="MessageKind.Submit"/> envelope.</summary>
    public static IpcMessage ForSubmit(SubmitPayload payload) =>
        new() { Kind = MessageKind.Submit, Submit = payload };

    /// <summary>Wraps a <see cref="SubmitPayloadResult"/> envelope.</summary>
    public static IpcMessage ForSubmitResult(SubmitPayloadResult payload) =>
        new() { Kind = MessageKind.SubmitResult, SubmitResult = payload };

    /// <summary>Builds a <see cref="MessageKind.StateQuery"/> envelope.</summary>
    public static IpcMessage ForStateQuery() =>
        new() { Kind = MessageKind.StateQuery, StateQuery = new EngineStateQuery() };

    /// <summary>Wraps an <see cref="EngineState"/> envelope.</summary>
    public static IpcMessage ForState(EngineState payload) =>
        new() { Kind = MessageKind.State, State = payload };

    /// <summary>Builds a <see cref="MessageKind.Subscribe"/> envelope.</summary>
    public static IpcMessage ForSubscribe() =>
        new() { Kind = MessageKind.Subscribe, Subscribe = new SubscribeEvents() };

    /// <summary>Wraps a <see cref="JobEvent"/> envelope.</summary>
    public static IpcMessage ForEvent(JobEvent payload) =>
        new() { Kind = MessageKind.Event, Event = payload };

    /// <summary>Builds a <see cref="MessageKind.ListProfiles"/> envelope.</summary>
    public static IpcMessage ForListProfiles() =>
        new() { Kind = MessageKind.ListProfiles, ListProfiles = new ListProfiles() };

    /// <summary>Wraps a <see cref="ProfileList"/> envelope.</summary>
    public static IpcMessage ForProfileList(ProfileList payload) =>
        new() { Kind = MessageKind.ProfileList, ProfileList = payload };

    /// <summary>Builds a <see cref="MessageKind.ReloadProfiles"/> envelope.</summary>
    public static IpcMessage ForReloadProfiles() =>
        new() { Kind = MessageKind.ReloadProfiles, ReloadProfiles = new ReloadProfiles() };

    /// <summary>Wraps a <see cref="ReloadResult"/> envelope.</summary>
    public static IpcMessage ForReloadResult(ReloadResult payload) =>
        new() { Kind = MessageKind.ReloadResult, ReloadResult = payload };

    /// <summary>Wraps a <see cref="DryRunRequest"/> envelope.</summary>
    public static IpcMessage ForDryRun(DryRunRequest payload) =>
        new() { Kind = MessageKind.DryRun, DryRun = payload };

    /// <summary>Wraps a <see cref="DryRunReport"/> envelope.</summary>
    public static IpcMessage ForDryRunReport(DryRunReport payload) =>
        new() { Kind = MessageKind.DryRunReport, DryRunReport = payload };

    /// <summary>Wraps a <see cref="Messages.ManualInvocationPending"/> envelope (service → client push).</summary>
    public static IpcMessage ForManualInvocationPending(ManualInvocationPending payload) =>
        new() { Kind = MessageKind.ManualInvocationPending, ManualInvocationPending = payload };

    /// <summary>Wraps a <see cref="Messages.ResolveManualInvocation"/> envelope (client → service).</summary>
    public static IpcMessage ForResolveManualInvocation(ResolveManualInvocation payload) =>
        new() { Kind = MessageKind.ResolveManualInvocation, ResolveManualInvocation = payload };
}
