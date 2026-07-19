using FluentAssertions;
using Milvaion.Infrastructure.Scheduling;

namespace Milvaion.UnitTests.InfrastructureTests;

/// <summary>
/// Unit tests for the schedule startup recovery writes to Redis.
/// </summary>
/// <remarks>
/// These pin down a bug that only showed up in production: every deploy fired the
/// whole job catalogue at once. Recovery was writing the <c>ExecuteAt</c> column back
/// into the sorted set, and that column stops being current the first time a recurring
/// job runs - the dispatcher advances the schedule in memory and in Redis, never in
/// the database. Every restart therefore reset every recurring job to a past time.
///
/// The failure is silent by nature: nothing errors, the jobs simply all run. Worth
/// having tests that fail loudly instead.
/// </remarks>
public class StartupScheduleResolverTests
{
    private static readonly DateTime _now = new(2026, 7, 19, 12, 00, 00, DateTimeKind.Utc);

    /// <summary>
    /// Every hour on the hour.
    /// </summary>
    private const string _hourlyCron = "0 0 * * * *";

    /// <summary>
    /// What the column looks like for a job created months ago and running ever since.
    /// </summary>
    private static readonly DateTime _staleExecuteAt = new(2026, 3, 1, 9, 00, 00, DateTimeKind.Utc);

    [Fact]
    public void Resolve_ShouldLeaveLiveScheduleAlone_WhenRedisAlreadyHasOne()
    {
        // The regression itself. Redis says tomorrow at 03:00, the column says months ago.
        // Recovery must not have an opinion here.
        var liveScore = new DateTime(2026, 7, 20, 3, 00, 00, DateTimeKind.Utc);

        var result = StartupScheduleResolver.Resolve(_hourlyCron, _staleExecuteAt, completedAt: null, liveScore, _now);

        result.Should().BeNull("recovery must not overwrite a schedule Redis is already holding");
    }

    [Fact]
    public void Resolve_ShouldLeaveLiveScheduleAlone_EvenWhenItIsInThePast()
    {
        // A due job waiting to be picked up looks the same as an overdue one. Either way
        // the dispatcher owns it, and rewriting the score would move a job that is about
        // to run.
        var duePast = _now.AddMinutes(-2);

        var result = StartupScheduleResolver.Resolve(_hourlyCron, _staleExecuteAt, completedAt: null, duePast, _now);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_ShouldDeriveFromCron_WhenRedisIsCold()
    {
        // Redis was flushed or restarted alongside the API. The next run has to come from
        // the cron expression - the column would put it in the past and fire immediately.
        var result = StartupScheduleResolver.Resolve(_hourlyCron, _staleExecuteAt, completedAt: null, existingScheduleTime: null, _now);

        result.Should().Be(new DateTime(2026, 7, 19, 13, 00, 00, DateTimeKind.Utc));
        result.Should().NotBe(_staleExecuteAt);
    }

    [Fact]
    public void Resolve_ShouldReturnAFutureTime_ForEveryRecurringJob_WhenRedisIsCold()
    {
        // The property that matters for the reported symptom: after a cold start no
        // recurring job may land on a score the dispatcher would treat as due.
        string[] crons = ["0 0 * * * *", "0 */5 * * * *", "0 0 9 * * *", "*/30 * * * * *"];

        foreach (var cron in crons)
        {
            var result = StartupScheduleResolver.Resolve(cron, _staleExecuteAt, completedAt: null, existingScheduleTime: null, _now);

            result.Should().NotBeNull();
            result.Value.Should().BeAfter(_now, $"cron '{cron}' must not be recovered into the past");
        }
    }

    [Fact]
    public void Resolve_ShouldUseExecuteAt_ForOneTimeJobs_WhenRedisIsCold()
    {
        // A one-time job is never rescheduled, so its column never goes stale and is the
        // whole schedule.
        var runAt = _now.AddHours(4);

        var result = StartupScheduleResolver.Resolve(cronExpression: null, runAt, completedAt: null, existingScheduleTime: null, _now);

        result.Should().Be(runAt);
    }

    [Fact]
    public void Resolve_ShouldNotReschedule_WhenOneTimeJobHasAlreadyRun()
    {
        // The second half of the deploy problem. A one-time job that has run is absent from
        // Redis, and its ExecuteAt is in the past - which used to look exactly like a job
        // waiting to be scheduled, so recovery scheduled it and it ran again.
        var ranAt = _now.AddHours(-4);

        var result = StartupScheduleResolver.Resolve(cronExpression: null,
                                                     executeAt: _now.AddHours(-5),
                                                     completedAt: ranAt,
                                                     existingScheduleTime: null,
                                                     _now);

        result.Should().BeNull("a one-time job that has already run must never be scheduled again");
    }

    [Fact]
    public void Resolve_ShouldScheduleMissedOneTimeJob_WhenItNeverRan()
    {
        // The case the completion stamp lets us tell apart: due while the API was down, so
        // it is in the past and absent from Redis, but it genuinely never ran.
        var missed = _now.AddHours(-4);

        var result = StartupScheduleResolver.Resolve(cronExpression: null,
                                                     executeAt: missed,
                                                     completedAt: null,
                                                     existingScheduleTime: null,
                                                     _now);

        result.Should().Be(missed);
    }

    [Theory]
    [InlineData("not a cron")]
    [InlineData("0 0 * * *")]
    public void Resolve_ShouldReturnNull_WhenCronCannotBeParsed(string cron)
    {
        // Keeping it out of the sorted set is better than falling back to the column,
        // which would schedule it in the past.
        var result = StartupScheduleResolver.Resolve(cron, _staleExecuteAt, completedAt: null, existingScheduleTime: null, _now);

        result.Should().BeNull();
    }
}
