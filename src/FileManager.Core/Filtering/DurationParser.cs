namespace FileManager.Core.Filtering;

/// <summary>
/// Parses the compact duration strings used by age filters (§5.1) — e.g. <c>"7d"</c>, <c>"24h"</c>,
/// <c>"30m"</c>, <c>"45s"</c>. Manual parse (no regex) to stay AOT-clean and dependency-free.
/// </summary>
public static class DurationParser
{
    /// <summary>
    /// Tries to parse a duration like <c>"7d"</c>. Accepts a non-negative integer magnitude followed
    /// by a unit suffix: <c>d</c> (days), <c>h</c> (hours), <c>m</c> (minutes), <c>s</c> (seconds).
    /// Units are lowercase only, per spec §5.1 — uppercase is rejected so an ambiguous <c>"3M"</c>
    /// (months?) is never silently treated as 3 minutes. Returns false for null/empty/malformed input.
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();
        char unit = trimmed[^1];
        string magnitudeText = trimmed[..^1];

        if (!long.TryParse(magnitudeText, out long magnitude) || magnitude < 0)
            return false;

        switch (unit)
        {
            case 'd':
                duration = TimeSpan.FromDays(magnitude);
                return true;
            case 'h':
                duration = TimeSpan.FromHours(magnitude);
                return true;
            case 'm':
                duration = TimeSpan.FromMinutes(magnitude);
                return true;
            case 's':
                duration = TimeSpan.FromSeconds(magnitude);
                return true;
            default:
                return false;
        }
    }
}
