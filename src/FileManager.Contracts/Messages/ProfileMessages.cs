namespace FileManager.Contracts.Messages;

/// <summary>A request for the list of Profiles known to the service. Carries no fields.</summary>
public sealed record ListProfiles;

/// <summary>A compact summary of one Profile (spec §2 list Profiles), with only display-safe fields.</summary>
/// <param name="ProfileId">The Profile's stable id.</param>
/// <param name="Name">The Profile's display name.</param>
/// <param name="Active">Whether the engine acts on the Profile.</param>
public sealed record ProfileSummary(string ProfileId, string Name, bool Active);

/// <summary>The response to <see cref="ListProfiles"/>: the per-Profile summaries.</summary>
/// <param name="Profiles">One summary per loaded Profile.</param>
public sealed record ProfileList(IReadOnlyList<ProfileSummary> Profiles);

/// <summary>A request to reload Profiles from disk (spec §2 reload Profiles). Carries no fields.</summary>
public sealed record ReloadProfiles;

/// <summary>
/// The response to <see cref="ReloadProfiles"/>: how many Profiles are now loaded and any per-file
/// problems found during the reload (kept as strings so the contract stays dependency-free).
/// </summary>
/// <param name="LoadedCount">Number of valid Profiles after the reload.</param>
/// <param name="Errors">Human-readable problems for files that failed to load/validate.</param>
public sealed record ReloadResult(int LoadedCount, IReadOnlyList<string> Errors);
