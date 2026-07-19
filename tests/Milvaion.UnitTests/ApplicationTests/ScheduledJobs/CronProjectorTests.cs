using FluentAssertions;
using Milvaion.Application.Features.ScheduledJobs.GetUpcomingExecutionList;

namespace Milvaion.UnitTests.ApplicationTests.ScheduledJobs;

/// <summary>
/// Unit tests for the workflow next-run projection used by the upcoming executions screen.
/// </summary>
/// <remarks>
/// These exist because the projection is a second implementation of a rule that already
/// lives in <c>WorkflowEngineService</c>. Two copies of a scheduling rule drift silently -
/// the screen would keep showing plausible times while the engine fired at different ones -
/// so the behaviour that has to stay identical is pinned here.
/// </remarks>
public class CronProjectorTests
{
    private const int _pollingIntervalSeconds = 5;

    private static readonly DateTime _now = new(2026, 7, 19, 12, 00, 00, DateTimeKind.Utc);

    /// <summary>
    /// Every hour on the hour.
    /// </summary>
    private const string _hourlyCron = "0 0 * * * *";

    [Fact]
    public void GetNextOccurrence_ShouldCountFromLastRun_NotFromNow()
    {
        // The engine bases the next run on the last trigger. Counting from "now" instead
        // would quietly skip an occurrence whenever the engine was briefly behind.
        var lastRun = _now.AddMinutes(-90);

        var result = CronProjector.GetNextOccurrence(_hourlyCron, lastRun, _now, _pollingIntervalSeconds);

        result.Should().Be(new DateTime(2026, 7, 19, 11, 00, 00, DateTimeKind.Utc));
    }

    [Fact]
    public void GetNextOccurrence_ShouldReturnPastTime_WhenEngineIsBehind()
    {
        // A time in the past is the correct answer, not an error: it means the engine has
        // not caught up and will trigger on its next poll. Clamping it to now would hide
        // a workflow that is running late, which is exactly what the screen must surface.
        var lastRun = _now.AddDays(-3);

        var result = CronProjector.GetNextOccurrence(_hourlyCron, lastRun, _now, _pollingIntervalSeconds);

        result.Should().NotBeNull();
        result.Value.Should().BeBefore(_now);
    }

    [Fact]
    public void GetNextOccurrence_ShouldLookSlightlyBackwards_WhenWorkflowHasNeverRun()
    {
        // Never-run workflows get a window reaching two poll intervals into the past, so a
        // first occurrence landing in that gap is not skipped. Mirrors the engine's fallback.
        var justPassed = new DateTime(2026, 7, 19, 11, 59, 56, DateTimeKind.Utc);
        var everySecond = "* * * * * *";

        var result = CronProjector.GetNextOccurrence(everySecond, lastScheduledRunAt: null, justPassed.AddSeconds(1), _pollingIntervalSeconds);

        result.Should().NotBeNull();
        result.Value.Should().BeOnOrBefore(justPassed.AddSeconds(1));
    }

    [Fact]
    public void GetNextOccurrence_ShouldReturnTheFirstBoundaryAfterTheLastRun()
    {
        // Cronos treats the starting point as exclusive, so a run at 11:55 projects to the
        // 12:00 boundary rather than skipping to 13:00.
        var lastRun = _now.AddMinutes(-5);

        var result = CronProjector.GetNextOccurrence(_hourlyCron, lastRun, _now, _pollingIntervalSeconds);

        result.Should().Be(new DateTime(2026, 7, 19, 12, 00, 00, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetNextOccurrence_ShouldReturnNull_WhenExpressionIsMissing(string cron)
    {
        var result = CronProjector.GetNextOccurrence(cron, _now.AddHours(-1), _now, _pollingIntervalSeconds);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("not a cron")]
    [InlineData("0 0 * * *")]          // five parts - this project uses the six part form
    [InlineData("0 0 99 * * *")]       // hour out of range
    public void GetNextOccurrence_ShouldReturnNull_WhenExpressionIsInvalid(string cron)
    {
        // Null is what makes a row render as InvalidSchedule. Letting the parse exception
        // escape would take down the whole timeline over one bad expression.
        var result = CronProjector.GetNextOccurrence(cron, _now.AddHours(-1), _now, _pollingIntervalSeconds);

        result.Should().BeNull();
    }

    [Fact]
    public void GetNextOccurrence_ShouldMatchEngineRule_ForTheSameInputs()
    {
        // The engine's rule, written out independently: parse, base on last run or fall back
        // to two poll intervals ago, take the next occurrence in UTC. If this test starts
        // failing, the projector and the engine have diverged.
        var lastRun = _now.AddMinutes(-37);

        var expression = Cronos.CronExpression.Parse(_hourlyCron, Cronos.CronFormat.IncludeSeconds);
        var expected = expression.GetNextOccurrence(lastRun, TimeZoneInfo.Utc);

        var actual = CronProjector.GetNextOccurrence(_hourlyCron, lastRun, _now, _pollingIntervalSeconds);

        actual.Should().Be(expected);
    }
}
