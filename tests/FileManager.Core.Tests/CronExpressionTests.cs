using FileManager.Core.Triggers.Schedule;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>Exercises the hand-rolled 5-field cron parser/evaluator.</summary>
public sealed class CronExpressionTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset Next(string expr, DateTimeOffset after)
    {
        Assert.True(CronExpression.TryParse(expr, out CronExpression? cron, out string? err), err);
        DateTimeOffset? next = cron!.NextOccurrence(after, Utc);
        Assert.NotNull(next);
        return next!.Value;
    }

    [Fact]
    public void EveryMinute_AdvancesOneMinute()
    {
        var after = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero), Next("* * * * *", after));
    }

    [Fact]
    public void StepMinutes_FiresOnMultiples()
    {
        var after = new DateTimeOffset(2026, 1, 1, 0, 7, 0, TimeSpan.Zero);
        // */15 → next multiple of 15 strictly after 00:07 is 00:15.
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 15, 0, TimeSpan.Zero), Next("*/15 * * * *", after));
    }

    [Fact]
    public void SpecificTime_DailyAt0930()
    {
        var after = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        // 30 9 * * * → 09:30 the next day.
        Assert.Equal(new DateTimeOffset(2026, 3, 11, 9, 30, 0, TimeSpan.Zero), Next("30 9 * * *", after));
    }

    [Fact]
    public void Range_HourField()
    {
        var after = new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero);
        // 0 9-17 * * * → top of the next hour in [9,17]; after 11:00 that's 12:00.
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), Next("0 9-17 * * *", after));
    }

    [Fact]
    public void List_MinuteField()
    {
        var after = new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero);
        // 0,30 * * * * → after 00:10 the next is 00:30.
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.Zero), Next("0,30 * * * *", after));
    }

    [Fact]
    public void RangeWithStep()
    {
        var after = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // 0-10/2 * * * * → 0,2,4,6,8,10; strictly after 00:00 → 00:02.
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 2, 0, TimeSpan.Zero), Next("0-10/2 * * * *", after));
    }

    [Fact]
    public void DayOfWeek_Sunday_BothZeroAndSeven()
    {
        // Both "0" and "7" mean Sunday. 2026-01-04 is a Sunday.
        var after = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // Thursday
        DateTimeOffset withZero = Next("0 0 * * 0", after);
        DateTimeOffset withSeven = Next("0 0 * * 7", after);
        Assert.Equal(new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero), withZero);
        Assert.Equal(withZero, withSeven);
    }

    [Fact]
    public void DayOfMonthAndDayOfWeek_BothRestricted_OrSemantics()
    {
        // 0 0 1 * 1 → midnight on the 1st of the month OR any Monday (Vixie OR semantics).
        var after = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero); // Jan 1 is a Thursday
        // Next Monday after Jan 1 2026 is Jan 5; the 1st already passed (noon). So first match is Jan 5.
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), Next("0 0 1 * 1", after));
    }

    [Fact]
    public void Timezone_EvaluatesWallTime()
    {
        // 09:00 wall time in a fixed +05:00 zone equals 04:00 UTC.
        TimeZoneInfo plus5 = TimeZoneInfo.CreateCustomTimeZone("t+5", TimeSpan.FromHours(5), "t+5", "t+5");
        Assert.True(CronExpression.TryParse("0 9 * * *", out CronExpression? cron, out _));
        var afterUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset? next = cron!.NextOccurrence(afterUtc, plus5);
        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 4, 0, 0, TimeSpan.Zero), next!.Value.ToUniversalTime());
    }

    [Theory]
    [InlineData("* * * *")]            // too few fields
    [InlineData("* * * * * *")]        // too many fields
    [InlineData("60 * * * *")]         // minute out of range
    [InlineData("* 24 * * *")]         // hour out of range
    [InlineData("* * 0 * *")]          // day-of-month below 1
    [InlineData("* * * 13 *")]         // month out of range
    [InlineData("* * * * 8")]          // day-of-week above 7
    [InlineData("*/0 * * * *")]        // zero step
    [InlineData("5-2 * * * *")]        // inverted range
    [InlineData("abc * * * *")]        // non-numeric
    [InlineData("")]                   // empty
    public void InvalidExpressions_FailCleanly(string expr)
    {
        Assert.False(CronExpression.TryParse(expr, out CronExpression? cron, out string? error));
        Assert.Null(cron);
        Assert.NotNull(error);
    }
}
