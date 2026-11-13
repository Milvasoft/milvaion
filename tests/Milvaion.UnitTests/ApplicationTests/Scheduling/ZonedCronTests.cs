using FluentAssertions;
using Milvaion.Application.Utils.Scheduling;

namespace Milvaion.UnitTests.ApplicationTests.Scheduling;

/// <summary>
/// Cron evaluation in a job's own time zone, with the wall clock rules across daylight saving time.
/// </summary>
/// <remarks>
/// Dates used: in 2026 the EU moves clocks forward on 29 March (02:00 -> 03:00 local in Berlin) and back on
/// 25 October (03:00 -> 02:00 local). The US moves forward on 8 March and back on 1 November (02:00 local).
/// </remarks>
public class ZonedCronTests
{
    private static readonly TimeZoneInfo _berlin = ZonedCron.ResolveTimeZone("Europe/Berlin");
    private static readonly TimeZoneInfo _newYork = ZonedCron.ResolveTimeZone("America/New_York");

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void GetNextOccurrence_ShouldEvaluateInJobZone()
    {
        // 09:00 in Istanbul (UTC+3, no DST) is 06:00 UTC.
        var next = ZonedCron.GetNextOccurrence("0 0 9 * * *", Utc(2026, 7, 1, 0), "Europe/Istanbul");

        next.Should().Be(Utc(2026, 7, 1, 6));
    }

    [Fact]
    public void GetNextOccurrence_ShouldBehaveAsBefore_ForUtc()
    {
        var next = ZonedCron.GetNextOccurrence("0 30 2 * * *", Utc(2026, 3, 29, 0), "UTC");

        next.Should().Be(Utc(2026, 3, 29, 2, 30));
    }

    [Fact]
    public void GetNextOccurrence_ShouldSkipRun_WhenLocalTimeIsSkippedBySpringForward()
    {
        // 02:30 does not exist in Berlin on 29 March 2026. The run that day is dropped; the next is 30 March 02:30
        // local, which is 00:30 UTC (CEST, UTC+2).
        var next = ZonedCron.GetNextOccurrence(ZonedCron.Parse("0 30 2 * * *"), Utc(2026, 3, 28, 12), _berlin);

        next.Should().Be(Utc(2026, 3, 30, 0, 30));
    }

    [Fact]
    public void GetNextOccurrence_ShouldKeepRunsOutsideTheGap_OnSpringForwardDay()
    {
        // 03:00 local exists on the transition day: 01:00 UTC.
        var next = ZonedCron.GetNextOccurrence(ZonedCron.Parse("0 0 3 * * *"), Utc(2026, 3, 28, 12), _berlin);

        next.Should().Be(Utc(2026, 3, 29, 1));
    }

    [Fact]
    public void GetOccurrences_ShouldNotFireInsideTheGap_ForIntervalExpression()
    {
        // Every 30 minutes, 01:00 to 04:00 local on spring-forward night in New York (02:00 -> 03:00).
        var cron = ZonedCron.Parse("0 */30 * * * *");
        var from = Utc(2026, 3, 8, 6);   // 01:00 EST
        var to = Utc(2026, 3, 8, 8);     // 04:00 EDT

        var local = ZonedCron.GetOccurrences(cron, from, to, _newYork)
                             .Select(o => ZonedCron.ToLocalTime(o, _newYork).ToString("HH:mm"))
                             .ToList();

        local.Should().Equal("01:00", "01:30", "03:00", "03:30", "04:00");
    }

    [Fact]
    public void GetOccurrences_ShouldRunRepeatedHourOnce_ForDailyExpression()
    {
        // 02:30 happens twice in Berlin on 25 October 2026 (first CEST at 00:30 UTC, then CET at 01:30 UTC).
        var cron = ZonedCron.Parse("0 30 2 * * *");

        var runs = ZonedCron.GetOccurrences(cron, Utc(2026, 10, 24, 12), Utc(2026, 10, 26, 12), _berlin).ToList();

        runs.Should().Equal(Utc(2026, 10, 25, 0, 30), Utc(2026, 10, 26, 1, 30));
    }

    [Fact]
    public void GetOccurrences_ShouldRunRepeatedHourOnce_ForIntervalExpression()
    {
        // Every 30 minutes across fall-back in New York: 01:00 and 01:30 local happen twice, each must run once.
        var cron = ZonedCron.Parse("0 */30 * * * *");
        var from = Utc(2026, 11, 1, 4);  // 00:00 EDT
        var to = Utc(2026, 11, 1, 8);    // 03:00 EST

        var local = ZonedCron.GetOccurrences(cron, from, to, _newYork)
                             .Select(o => ZonedCron.ToLocalTime(o, _newYork).ToString("HH:mm"))
                             .ToList();

        local.Should().Equal("00:00", "00:30", "01:00", "01:30", "02:00", "02:30", "03:00");
    }

    [Fact]
    public void GetNextOccurrence_ShouldNotReturnSecondCopy_WhenSearchStartsInsideRepeatedHour()
    {
        // Start between the two copies of 01:30 New York time (first at 05:30 UTC, second at 06:30 UTC).
        var next = ZonedCron.GetNextOccurrence(ZonedCron.Parse("0 30 1 * * *"), Utc(2026, 11, 1, 6), _newYork);

        next.Should().Be(Utc(2026, 11, 2, 6, 30)); // 01:30 EST the next day
    }

    [Theory]
    [InlineData("UTC", true)]
    [InlineData("Europe/Istanbul", true)]
    [InlineData("America/New_York", true)]
    [InlineData("Asia/Kolkata", true)]
    [InlineData("Turkey Standard Time", false)]
    [InlineData("Not/AZone", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(" Europe/Istanbul", false)]
    public void IsValidTimeZoneId_ShouldAcceptOnlyKnownIanaIds(string id, bool expected) => ZonedCron.IsValidTimeZoneId(id).Should().Be(expected);

    [Fact]
    public void ResolveTimeZone_ShouldFallBackToUtc_ForUnknownId() => ZonedCron.ResolveTimeZone("Nowhere/Nothing").Should().Be(TimeZoneInfo.Utc);

    [Fact]
    public void ToLocalDate_ShouldUseJobZone()
    {
        // 22:30 UTC on 31 December is already 1 January in Istanbul.
        ZonedCron.ToLocalDate(Utc(2026, 12, 31, 22, 30), ZonedCron.ResolveTimeZone("Europe/Istanbul")).Should().Be(new DateOnly(2027, 1, 1));
    }
}
