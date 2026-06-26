using System.Globalization;

namespace FileManager.Core.Triggers.Schedule;

/// <summary>
/// A hand-rolled, dependency-free parser and evaluator for the standard 5-field cron expression
/// (the milestone Risks require an AOT-safe parser with no external cron package).
/// </summary>
/// <remarks>
/// <para><b>Fields (in order):</b> minute (0–59), hour (0–23), day-of-month (1–31), month (1–12),
/// day-of-week (0–6, where <c>0</c> = Sunday; <c>7</c> is also accepted as Sunday). Fields are
/// whitespace-separated.</para>
/// <para><b>Supported syntax per field:</b>
/// <list type="bullet">
/// <item><c>*</c> — every value in the field's range.</item>
/// <item><c>*/step</c> — every <c>step</c>th value across the whole range (e.g. <c>*/15</c> minutes).</item>
/// <item><c>a</c> — a single value.</item>
/// <item><c>a-b</c> — an inclusive range.</item>
/// <item><c>a-b/step</c> — every <c>step</c>th value within the range.</item>
/// <item><c>a,b,c</c> — a list whose elements are any of the forms above.</item>
/// </list>
/// Month and day-of-week names (<c>JAN</c>…, <c>SUN</c>…) are <b>not</b> supported — numbers only.
/// This is the deliberately small subset the spec calls for; anything else fails
/// <see cref="TryParse"/> cleanly (no throw at config time).</para>
/// <para><b>Day-of-month / day-of-week semantics</b> follow Vixie cron: when <em>both</em> day fields
/// are restricted (neither is <c>*</c>), a date matches if it satisfies <em>either</em> (OR); when one
/// is <c>*</c>, only the other constrains the day.</para>
/// </remarks>
public sealed class CronExpression
{
    private readonly bool[] _minutes;     // 0..59
    private readonly bool[] _hours;       // 0..23
    private readonly bool[] _daysOfMonth; // 1..31 (index 0 unused)
    private readonly bool[] _months;      // 1..12 (index 0 unused)
    private readonly bool[] _daysOfWeek;  // 0..6 (Sunday=0)
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    private CronExpression(
        bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months, bool[] daysOfWeek,
        bool domRestricted, bool dowRestricted)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _domRestricted = domRestricted;
        _dowRestricted = dowRestricted;
    }

    /// <summary>
    /// Attempts to parse <paramref name="expression"/> into a <see cref="CronExpression"/>. On failure
    /// returns false and fills <paramref name="error"/> with a human-readable reason — never throws, so
    /// config validation can surface the problem as a <c>ValidationError</c>.
    /// </summary>
    public static bool TryParse(string? expression, out CronExpression? cron, out string? error)
    {
        cron = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Cron expression is empty.";
            return false;
        }

        string[] fields = expression.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = $"Cron expression must have exactly 5 fields (minute hour day-of-month month day-of-week); got {fields.Length}.";
            return false;
        }

        if (!TryParseField(fields[0], 0, 59, "minute", out bool[] minutes, out error)
            || !TryParseField(fields[1], 0, 23, "hour", out bool[] hours, out error)
            || !TryParseField(fields[2], 1, 31, "day-of-month", out bool[] daysOfMonth, out error)
            || !TryParseField(fields[3], 1, 12, "month", out bool[] months, out error)
            || !TryParseDayOfWeek(fields[4], out bool[] daysOfWeek, out error))
        {
            return false;
        }

        cron = new CronExpression(
            minutes, hours, daysOfMonth, months, daysOfWeek,
            domRestricted: fields[2].Trim() != "*",
            dowRestricted: fields[4].Trim() != "*");
        return true;
    }

    /// <summary>
    /// The next instant strictly after <paramref name="after"/> at which this expression fires, in the
    /// supplied <paramref name="timeZone"/>. Returns null only in the pathological case that no match
    /// exists within a bounded search horizon (e.g. an impossible date like Feb 30). The returned value
    /// is an absolute <see cref="DateTimeOffset"/> (the wall time interpreted in <paramref name="timeZone"/>).
    /// </summary>
    /// <remarks>
    /// Evaluation is minute-granular (the smallest cron field). We advance the <em>local</em> wall
    /// clock minute-by-minute from the minute after <paramref name="after"/>, testing each field set,
    /// then convert the first match back to an absolute offset. Local→absolute conversion handles DST:
    /// a wall time that is skipped by a spring-forward never matches (it does not exist), and a time in
    /// the fall-back overlap resolves to its first (pre-transition) occurrence — adequate for a
    /// minute-granular scheduler and documented here so the behavior is intentional.
    /// </remarks>
    public DateTimeOffset? NextOccurrence(DateTimeOffset after, TimeZoneInfo timeZone)
    {
        // Work in the target zone's wall time. Start at the next whole minute after `after`.
        DateTime local = TimeZoneInfo.ConvertTime(after, timeZone).DateTime;
        local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified)
            .AddMinutes(1);

        // Bound the search to four years: enough to clear any Feb-29 / day-of-week combination, while
        // guaranteeing termination for an unsatisfiable expression.
        DateTime limit = local.AddYears(4);
        while (local < limit)
        {
            if (Matches(local))
            {
                DateTime candidate = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                if (timeZone.IsInvalidTime(candidate))
                {
                    // Wall time skipped by a spring-forward DST transition — it never occurs.
                    local = local.AddMinutes(1);
                    continue;
                }

                TimeSpan offset = timeZone.GetUtcOffset(candidate);
                return new DateTimeOffset(candidate, offset);
            }

            local = local.AddMinutes(1);
        }

        return null;
    }

    private bool Matches(DateTime local)
    {
        if (!_minutes[local.Minute] || !_hours[local.Hour] || !_months[local.Month])
            return false;

        bool domOk = _daysOfMonth[local.Day];
        bool dowOk = _daysOfWeek[(int)local.DayOfWeek];

        // Vixie cron day semantics: both restricted ⇒ OR; otherwise the restricted one (if any) wins.
        if (_domRestricted && _dowRestricted)
            return domOk || dowOk;
        if (_domRestricted)
            return domOk;
        if (_dowRestricted)
            return dowOk;
        return true; // both '*'
    }

    private static bool TryParseDayOfWeek(string field, out bool[] set, out string? error)
    {
        // Accept 0..7 with 7 == Sunday, normalizing onto the 0..6 (Sunday=0) set.
        set = new bool[7];
        error = null;
        var raw = new bool[8];
        if (!TryParseField(field, 0, 7, "day-of-week", out raw, out error))
            return false;

        for (int i = 0; i <= 7; i++)
        {
            if (raw[i])
                set[i == 7 ? 0 : i] = true;
        }

        return true;
    }

    // Parses one field (after the day-of-week normalization handles its 0..7 quirk separately) into a
    // membership bitmap over [min, max]. Supports *, */step, a, a-b, a-b/step, and comma lists.
    private static bool TryParseField(string field, int min, int max, string name, out bool[] set, out string? error)
    {
        set = new bool[max + 1];
        error = null;
        field = field.Trim();

        foreach (string part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParsePart(part.Trim(), min, max, name, set, out error))
                return false;
        }

        // A field consisting only of empty/comma noise is invalid.
        bool any = false;
        for (int i = min; i <= max; i++)
        {
            if (set[i]) { any = true; break; }
        }

        if (!any)
        {
            error = $"Invalid {name} field '{field}': no values selected.";
            return false;
        }

        return true;
    }

    private static bool TryParsePart(string part, int min, int max, string name, bool[] set, out string? error)
    {
        error = null;

        int step = 1;
        string rangePart = part;
        int slash = part.IndexOf('/');
        if (slash >= 0)
        {
            rangePart = part[..slash];
            string stepText = part[(slash + 1)..];
            if (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
            {
                error = $"Invalid {name} field '{part}': step must be a positive integer.";
                return false;
            }
        }

        int lo, hi;
        if (rangePart == "*")
        {
            lo = min;
            hi = max;
        }
        else
        {
            int dash = rangePart.IndexOf('-');
            if (dash >= 0)
            {
                if (!TryParseValue(rangePart[..dash], min, max, name, out lo, out error)
                    || !TryParseValue(rangePart[(dash + 1)..], min, max, name, out hi, out error))
                {
                    return false;
                }

                if (lo > hi)
                {
                    error = $"Invalid {name} field '{part}': range start exceeds end.";
                    return false;
                }
            }
            else
            {
                if (!TryParseValue(rangePart, min, max, name, out lo, out error))
                    return false;

                // A bare single value with no step selects just that value; with a step it means
                // "from this value to the field maximum, every step" (standard cron a/step semantics).
                hi = slash >= 0 ? max : lo;
            }
        }

        for (int v = lo; v <= hi; v += step)
            set[v] = true;

        return true;
    }

    private static bool TryParseValue(string text, int min, int max, string name, out int value, out string? error)
    {
        error = null;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            error = $"Invalid {name} field: '{text}' is not a non-negative integer.";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"Invalid {name} field: {value} is out of range [{min}, {max}].";
            return false;
        }

        return true;
    }
}
