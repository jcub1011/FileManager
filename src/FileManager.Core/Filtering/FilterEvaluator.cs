using System.Text.RegularExpressions;
using FileManager.Core.IO;
using FileManager.Core.Profiles;

namespace FileManager.Core.Filtering;

/// <summary>
/// Phase 2 filter screening (§5.1). Evaluates a file against a <see cref="FilterSet"/> rule by rule
/// and returns the first rejecting rule as the <see cref="FilterDecision.DecidingFilter"/>. Reusable
/// by the M7 dry-run report.
/// </summary>
/// <remarks>
/// Regex uses the interpreted engine (no <see cref="RegexOptions.Compiled"/>) to stay AOT-clean.
/// All rules except <c>ContentHashDedupe</c> operate purely on the <see cref="FilterCandidate"/>;
/// dedupe is the one rule that reads the file system, via the injected <see cref="IFileOperations"/>.
/// </remarks>
public sealed class FilterEvaluator
{
    private readonly IFileOperations _files;

    public FilterEvaluator(IFileOperations files) => _files = files;

    /// <summary>
    /// Resolves the effective filters for a Source: per-Source filters <b>override</b> the global set
    /// when present (they do not additively merge), per §5.1.
    /// </summary>
    public static FilterSet ResolveEffective(FilterSet global, FilterSet? perSource) => perSource ?? global;

    /// <summary>
    /// Screens <paramref name="candidate"/> against <paramref name="filters"/>. <paramref name="now"/>
    /// anchors the age rules; <paramref name="targets"/> is scanned only when dedupe is enabled.
    /// </summary>
    public FilterDecision Evaluate(
        FilterSet filters,
        FilterCandidate candidate,
        IReadOnlyList<TargetSpec> targets,
        DateTimeOffset now)
    {
        string name = candidate.FileName;
        FileMetadata meta = candidate.Metadata;

        // Include globs: must match at least one when specified.
        if (filters.Include is { Count: > 0 } include && !GlobMatcher.MatchesAny(include, name))
            return FilterDecision.Reject("Include");

        // Exclude globs: reject on any match.
        if (filters.ExcludeGlob is { Count: > 0 } excludeGlob)
        {
            foreach (string pattern in excludeGlob)
            {
                if (GlobMatcher.IsMatch(pattern, name))
                    return FilterDecision.Reject("ExcludeGlob", pattern);
            }
        }

        // Include regex: must match at least one when specified.
        if (filters.IncludeRegex is { Count: > 0 } includeRegex && !RegexMatchesAny(includeRegex, name))
            return FilterDecision.Reject("IncludeRegex");

        // Exclude regex: reject on any match.
        if (filters.ExcludeRegex is { Count: > 0 } excludeRegex)
        {
            foreach (string pattern in excludeRegex)
            {
                if (SafeRegexIsMatch(pattern, name))
                    return FilterDecision.Reject("ExcludeRegex", pattern);
            }
        }

        // Size bounds.
        if (filters.MinSizeBytes is { } min && meta.Length < min)
            return FilterDecision.Reject("MinSizeBytes");
        if (filters.MaxSizeBytes is { } max && meta.Length > max)
            return FilterDecision.Reject("MaxSizeBytes");

        // Age rules (unparseable durations are treated as "no constraint").
        DateTime nowUtc = now.UtcDateTime;
        if (DurationParser.TryParse(filters.ModifiedWithin, out TimeSpan modWithin)
            && nowUtc - meta.LastWriteTimeUtc > modWithin)
            return FilterDecision.Reject("ModifiedWithin");
        if (DurationParser.TryParse(filters.ModifiedOlderThan, out TimeSpan modOlder)
            && nowUtc - meta.LastWriteTimeUtc < modOlder)
            return FilterDecision.Reject("ModifiedOlderThan");
        if (DurationParser.TryParse(filters.CreatedWithin, out TimeSpan createdWithin)
            && nowUtc - meta.CreationTimeUtc > createdWithin)
            return FilterDecision.Reject("CreatedWithin");

        // Attributes (hidden / system / symlink).
        AttributeFilter attrs = filters.Attributes ?? AttributeChecks.Defaults;
        string? blockingAttr = AttributeChecks.FindBlockingAttribute(meta, attrs);
        if (blockingAttr is not null)
            return FilterDecision.Reject(blockingAttr);

        // Depth.
        if (filters.MaxDepth is { } maxDepth && candidate.Depth > maxDepth)
            return FilterDecision.Reject("MaxDepth");

        // Content-hash dedupe (the only rule that reads the file system).
        if (filters.ContentHashDedupe && DedupeIndex.ExistsInTargets(_files, candidate.FullPath, targets))
            return FilterDecision.Reject("ContentHashDedupe");

        return FilterDecision.Pass;
    }

    private static bool RegexMatchesAny(IReadOnlyList<string> patterns, string input)
    {
        foreach (string pattern in patterns)
        {
            if (SafeRegexIsMatch(pattern, input))
                return true;
        }

        return false;
    }

    private static bool SafeRegexIsMatch(string pattern, string input)
    {
        try
        {
            return Regex.IsMatch(input, pattern);
        }
        catch (RegexParseException)
        {
            // A malformed pattern matches nothing rather than crashing the Job.
            return false;
        }
    }
}
