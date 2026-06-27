using System.IO;
using FileManager.Contracts.Messages;
using FileManager.Core.Execution;
using FileManager.Core.IO;
using FileManager.Core.Jobs;
using FileManager.Core.Profiles;

namespace FileManager.Service;

/// <summary>
/// Bridges IPC <see cref="SubmitPayload"/> submissions and shell handoffs into the engine's
/// <see cref="JobQueue"/> (spec §2 Payload queue). For a submitted path it resolves the owning Profile
/// (by the chosen <see cref="SubmitPayload.ProfileId"/>, else by matching the path to a Profile's
/// configured Source), enumerates the matching files (respecting the recursive flag for a directory),
/// and enqueues one <see cref="JobRequest"/> per file. It reports back accepted/queued counts so the
/// caller can answer the client.
/// </summary>
/// <remarks>
/// The set of active Profiles is supplied by a snapshot delegate so a reload swaps the snapshot without
/// rebuilding the queue. Files are enqueued with <see cref="JobQueue.TryEnqueue"/> (non-blocking): if the
/// bounded queue is momentarily full the submission reports the partial count rather than blocking the
/// IPC dispatch thread.
/// </remarks>
public sealed class PayloadQueue
{
    private readonly JobQueue _queue;
    private readonly Func<IReadOnlyList<Profile>> _activeProfiles;
    private readonly IFileOperations _files;
    private readonly TimeProvider _clock;
    private readonly Action<int>? _onEnqueued;

    /// <summary>
    /// Creates a payload queue feeding <paramref name="queue"/>. <paramref name="activeProfiles"/>
    /// returns the current Profile snapshot, <paramref name="files"/> enumerates the filesystem,
    /// <paramref name="clock"/> stamps the <see cref="IngestionContext"/>, and the optional
    /// <paramref name="onEnqueued"/> is invoked with the number of Jobs queued by each
    /// <see cref="Submit"/>. Routing every enqueue through this one callback keeps the engine's queued
    /// tally accurate regardless of trigger path (IPC submit, watcher, scheduler).
    /// </summary>
    public PayloadQueue(
        JobQueue queue,
        Func<IReadOnlyList<Profile>> activeProfiles,
        IFileOperations files,
        TimeProvider? clock = null,
        Action<int>? onEnqueued = null)
    {
        _queue = queue;
        _activeProfiles = activeProfiles;
        _files = files;
        _clock = clock ?? TimeProvider.System;
        _onEnqueued = onEnqueued;
    }

    /// <summary>
    /// Resolves the Profile for <paramref name="payload"/>'s path, enumerates the matching files, and
    /// enqueues a Job for each. Returns an accepted result with the queued submission ids, or a rejected
    /// result naming why nothing was queued (no matching Profile, no files, etc.).
    /// </summary>
    public SubmitPayloadResult Submit(SubmitPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Path))
            return SubmitPayloadResult.Rejected("Submit path is empty.");

        string path = PathNormalizer.Normalize(payload.Path);
        IReadOnlyList<Profile> profiles = _activeProfiles();

        Profile? profile = ResolveProfile(profiles, path, payload.ProfileId, out string? resolveError);
        if (profile is null)
            return SubmitPayloadResult.Rejected(resolveError ?? "No active Profile owns the submitted path.");

        IReadOnlyList<string> files = EnumerateFiles(path, payload.Recursive);
        if (files.Count == 0)
            return SubmitPayloadResult.Rejected("No files matched the submitted path.");

        var context = new IngestionContext { Now = _clock.GetUtcNow() };
        var jobIds = new List<string>(files.Count);
        foreach (string file in files)
        {
            string normalized = PathNormalizer.Normalize(file);
            if (!OwnsPath(profile, normalized))
                continue; // a directory submit may surface files outside the resolved Profile's Sources.

            var request = new JobRequest { Profile = profile, SourcePath = normalized, Context = context };
            if (_queue.TryEnqueue(request))
                jobIds.Add(SubmissionId(profile, normalized));
        }

        if (jobIds.Count == 0)
            return SubmitPayloadResult.Rejected("Matched files could not be queued (queue full or none owned).");

        // Account every enqueue in one place so the host's queued tally reflects work from ALL triggers
        // (IPC submit + watcher + scheduler), not just the IPC facade.
        _onEnqueued?.Invoke(jobIds.Count);
        return SubmitPayloadResult.Ok(jobIds);
    }

    // Picks the Profile to run under: the named one (when ProfileId is set and it owns the path), else
    // the Profile whose Source most specifically contains the path. Returns null with a reason when none.
    private static Profile? ResolveProfile(
        IReadOnlyList<Profile> profiles, string path, string? profileId, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(profileId))
        {
            Profile? named = profiles.FirstOrDefault(p =>
                string.Equals(p.ProfileId, profileId, StringComparison.Ordinal));
            if (named is null)
            {
                error = $"No active Profile with id '{profileId}'.";
                return null;
            }

            if (!OwnsPath(named, path))
            {
                error = $"Profile '{profileId}' does not own the submitted path.";
                return null;
            }

            return named;
        }

        // No id: choose the Profile with the longest (most specific) owning Source root.
        Profile? best = null;
        int bestRootLength = -1;
        foreach (Profile profile in profiles)
        {
            foreach (SourceSpec source in profile.Sources)
            {
                if (!PathNormalizer.IsUnder(source.Path, path))
                    continue;

                int rootLength = PathNormalizer.Normalize(source.Path).Length;
                if (rootLength > bestRootLength)
                {
                    best = profile;
                    bestRootLength = rootLength;
                }
            }
        }

        return best;
    }

    // Whether any of the Profile's Sources contains the (file or directory) path.
    private static bool OwnsPath(Profile profile, string path) =>
        profile.Sources.Any(s => PathNormalizer.IsUnder(s.Path, path));

    // Returns the files implied by a submit path: the file itself, or every file under a directory
    // (recursively when requested). Best-effort — an unreadable directory yields an empty set.
    private IReadOnlyList<string> EnumerateFiles(string path, bool recursive)
    {
        if (_files.FileExists(path))
            return new[] { path };

        if (!_files.DirectoryExists(path))
            return Array.Empty<string>();

        try
        {
            return _files.EnumerateFiles(path, recursive).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // A stable submission id for the file under this Profile (correlates the JobEvent the engine emits).
    private static string SubmissionId(Profile profile, string file) =>
        $"{profile.ProfileId}:{file}";
}
