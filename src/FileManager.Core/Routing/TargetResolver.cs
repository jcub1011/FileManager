using System.IO;
using FileManager.Core.Profiles;

namespace FileManager.Core.Routing;

/// <summary>
/// Computes, for each Target, where a file lands (§3.1.2). Applies the layout policy and the M:1
/// flatten-forcing rule.
/// </summary>
public static class TargetResolver
{
    /// <summary>
    /// The effective layout for a Profile. <see cref="TargetLayout.Flatten"/> is forced for true M:1
    /// aggregation — multiple Sources feeding a single Target — regardless of the configured layout
    /// (§3.1.2). Every other topology (1:1, 1:N, M:N) honors <see cref="Profile.TargetLayout"/>.
    /// </summary>
    public static TargetLayout ResolveLayout(Profile profile)
    {
        bool isAggregation = profile.Sources.Count > 1 && profile.Targets.Count == 1;
        return isAggregation ? TargetLayout.Flatten : profile.TargetLayout;
    }

    /// <summary>
    /// The destination path for <paramref name="fileName"/> in <paramref name="target"/>:
    /// under <see cref="TargetLayout.PreserveStructure"/> the file's <paramref name="relativePath"/>
    /// (under its Source root) is recreated; under <see cref="TargetLayout.Flatten"/> it drops into the
    /// Target root.
    /// </summary>
    public static string ResolveDestination(
        TargetSpec target,
        string relativePath,
        string fileName,
        TargetLayout layout) =>
        layout == TargetLayout.Flatten
            ? Path.Combine(target.Path, fileName)
            : Path.Combine(target.Path, relativePath);
}
