using FluentAssertions;
using Milvaion.Application.Utils.Scheduling;
using Milvaion.Domain;
using Milvaion.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;

namespace Milvaion.UnitTests.ApplicationTests.Scheduling;

/// <summary>
/// The dispatcher's per-run decision: catch-up policies for runs missed during an outage, and exclusion calendars.
/// </summary>
public class RunPlannerTests
{
    private const string _everyTenMinutes = "0 */10 * * * *";

    /// <summary>The scheduler went down at 10:00 and came back at 11:00.</summary>
    private static readonly DispatcherOutage _outage = new(Utc(10, 0), Utc(11, 0, 5));

    private static readonly DateTime _now = Utc(11, 0, 5);

    private static DateTime Utc(int h, int mi, int s = 0) => new(2026, 6, 10, h, mi, s, DateTimeKind.Utc);

    private static ScheduledJob Job(string cron = _everyTenMinutes, DateTime? executeAt = null, MissedRunPolicy policy = MissedRunPolicy.Skip, int max = 10, string zone = "UTC") => new()
    {
        Id = Guid.CreateVersion7(),
        CronExpression = cron,
        ExecuteAt = executeAt ?? Utc(10, 10),
        MissedRunPolicy = policy,
        MaxCatchUpRuns = max,
        TimeZoneId = zone
    };

    [Fact]
    public void Plan_ShouldRunNormally_WhenRunIsOnTime()
    {
        var job = Job(executeAt: Utc(11, 0));

        var decision = RunPlanner.Plan(job, Utc(11, 0, 2), _outage, []);

        decision.Kind.Should().Be(RunDecisionKind.Run);
        decision.NextRunAtUtc.Should().Be(Utc(11, 10));
        decision.Message.Should().BeNull();
    }

    [Fact]
    public void Plan_ShouldRunNormally_WhenLateButNoOutageCoversIt()
    {
        // Held back by capacity while the dispatcher kept polling: late, not missed. Runs late, as before.
        var decision = RunPlanner.Plan(Job(executeAt: Utc(10, 10)), _now, outage: null, []);

        decision.Kind.Should().Be(RunDecisionKind.Run);
        decision.MissedCount.Should().Be(0);
    }

    [Fact]
    public void Plan_ShouldRunNormally_WhenRunWasBeforeTheOutage()
    {
        // The dispatcher was polling at 09:50; this run was held back, not missed by the outage.
        var decision = RunPlanner.Plan(Job(executeAt: Utc(9, 50)), _now, _outage, []);

        decision.Kind.Should().Be(RunDecisionKind.Run);
    }

    [Fact]
    public void Plan_Skip_ShouldSkipAllMissedRuns_AndResumeAtNextSlot()
    {
        var decision = RunPlanner.Plan(Job(policy: MissedRunPolicy.Skip), _now, _outage, []);

        decision.Kind.Should().Be(RunDecisionKind.SkipMissed);
        decision.Dispatches.Should().BeFalse();
        decision.MissedCount.Should().Be(6); // 10:10, 10:20, ..., 11:00
        decision.FirstMissedUtc.Should().Be(Utc(10, 10));
        decision.LastMissedUtc.Should().Be(Utc(11, 0));
        decision.NextRunAtUtc.Should().Be(Utc(11, 10));
        decision.Message.Should().Contain("Skipped 6 runs");
    }

    [Fact]
    public void Plan_RunOnce_ShouldRunOneCatchUp_AndResumeAtNextSlot()
    {
        var decision = RunPlanner.Plan(Job(policy: MissedRunPolicy.RunOnce), _now, _outage, []);

        decision.Kind.Should().Be(RunDecisionKind.CatchUp);
        decision.Dispatches.Should().BeTrue();
        decision.MissedCount.Should().Be(6);
        decision.NextRunAtUtc.Should().Be(Utc(11, 10));
        decision.Message.Should().Contain("Catch-up run").And.Contain("RunOnce");
    }

    [Fact]
    public void Plan_RunAll_ShouldStepThroughEveryMissedRun()
    {
        var job = Job(policy: MissedRunPolicy.RunAll);
        var runs = new List<DateTime>();

        // Simulate the dispatcher: each cycle runs the planned slot and writes the next score back.
        for (var cycle = 0; cycle < 20; cycle++)
        {
            var now = _now.AddSeconds(cycle);
            var decision = RunPlanner.Plan(job, now, _outage, []);

            decision.Dispatches.Should().BeTrue();
            runs.Add(decision.RunSlotUtc);

            if (decision.NextRunAtUtc > now)
                break;

            job.ExecuteAt = decision.NextRunAtUtc!.Value;
        }

        runs.Should().Equal(Utc(10, 10), Utc(10, 20), Utc(10, 30), Utc(10, 40), Utc(10, 50), Utc(11, 0));
    }

    [Fact]
    public void Plan_RunAll_ShouldDropOldestRuns_BeyondMaximum()
    {
        var decision = RunPlanner.Plan(Job(policy: MissedRunPolicy.RunAll, max: 2), _now, _outage, []);

        decision.Kind.Should().Be(RunDecisionKind.CatchUp);
        decision.MissedCount.Should().Be(6);
        decision.DroppedCount.Should().Be(4);
        decision.RunSlotUtc.Should().Be(Utc(10, 50)); // the two most recent are 10:50 and 11:00
        decision.NextRunAtUtc.Should().Be(Utc(11, 0));
        decision.Message.Should().Contain("4 runs older missed were dropped");
    }

    [Fact]
    public void Plan_ShouldNeverDropOneTimeJob_EvenWhenMissed()
    {
        var job = Job(cron: null, executeAt: Utc(10, 30), policy: MissedRunPolicy.Skip);

        var decision = RunPlanner.Plan(job, _now, _outage, []);

        decision.Kind.Should().Be(RunDecisionKind.Run);
        decision.NextRunAtUtc.Should().BeNull();
    }

    [Fact]
    public void Plan_ShouldReportInvalidSchedule_ForUnparsableCron()
        => RunPlanner.Plan(Job(cron: "not a cron"), _now, null, []).Kind.Should().Be(RunDecisionKind.InvalidSchedule);

    [Fact]
    public void Plan_ShouldSkipRunOnExcludedDay_AndJumpPastExcludedStretch()
    {
        // Daily at 09:00 Istanbul (06:00 UTC). Saturday 13 June 2026 is excluded by weekday and Sunday by date.
        var calendar = new ExclusionCalendar { Id = Guid.NewGuid(), Name = "Weekend", Weekdays = [6], Dates = [new DateOnly(2026, 6, 14)] };
        var job = Job(cron: "0 0 9 * * *", executeAt: new DateTime(2026, 6, 13, 6, 0, 0, DateTimeKind.Utc), zone: "Europe/Istanbul");

        var decision = RunPlanner.Plan(job, new DateTime(2026, 6, 13, 6, 0, 1, DateTimeKind.Utc), null, [calendar]);

        decision.Kind.Should().Be(RunDecisionKind.SkipExcluded);
        decision.Dispatches.Should().BeFalse();
        decision.ExcludedBy.Should().BeSameAs(calendar);
        decision.LocalDate.Should().Be(new DateOnly(2026, 6, 13));
        decision.NextRunAtUtc.Should().Be(new DateTime(2026, 6, 15, 6, 0, 0, DateTimeKind.Utc)); // Monday 09:00 local
        decision.Message.Should().Contain("2026-06-13").And.Contain("'Weekend'");
    }

    [Fact]
    public void Plan_ShouldUseLocalDateOfJobZone_ForCalendarMatch()
    {
        // 23:30 UTC on 31 Dec is 02:30 on 1 Jan in Istanbul - excluded there, not in UTC.
        var newYear = new ExclusionCalendar { Id = Guid.NewGuid(), Name = "New year", Dates = [new DateOnly(2027, 1, 1)] };
        var at = new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc);

        RunPlanner.Plan(Job(cron: "0 30 2 * * *", executeAt: at, zone: "Europe/Istanbul"), at, null, [newYear]).Kind.Should().Be(RunDecisionKind.SkipExcluded);
        RunPlanner.Plan(Job(cron: "0 30 23 * * *", executeAt: at, zone: "UTC"), at, null, [newYear]).Kind.Should().Be(RunDecisionKind.Run);
    }

    [Fact]
    public void Plan_ShouldSkipOneTimeJob_OnExcludedDay_AndLeaveSchedule()
    {
        var calendar = new ExclusionCalendar { Name = "Holiday", DateRanges = [new ExclusionDateRange { Start = new DateOnly(2026, 6, 1), End = new DateOnly(2026, 6, 30) }] };

        var decision = RunPlanner.Plan(Job(cron: null, executeAt: Utc(10, 30)), Utc(10, 30, 1), null, [calendar]);

        decision.Kind.Should().Be(RunDecisionKind.SkipExcluded);
        decision.NextRunAtUtc.Should().BeNull();
    }

    [Fact]
    public void Plan_RunAll_ShouldKeepCatchingUp_PastAnExcludedMissedRun()
    {
        // Hourly; outage covers 22:00 on 10 June to 02:00 on 11 June; 11 June is excluded.
        var outage = new DispatcherOutage(new DateTime(2026, 6, 10, 21, 30, 0, DateTimeKind.Utc), new DateTime(2026, 6, 11, 2, 0, 5, DateTimeKind.Utc));
        var calendar = new ExclusionCalendar { Name = "Day off", Dates = [new DateOnly(2026, 6, 11)] };
        var job = Job(cron: "0 0 * * * *", executeAt: new DateTime(2026, 6, 10, 23, 0, 0, DateTimeKind.Utc), policy: MissedRunPolicy.RunAll);

        var decision = RunPlanner.Plan(job, outage.EndUtc, outage, [calendar]);

        decision.Kind.Should().Be(RunDecisionKind.CatchUp);

        job.ExecuteAt = decision.NextRunAtUtc!.Value; // 00:00 on 11 June - excluded

        var excluded = RunPlanner.Plan(job, outage.EndUtc, outage, [calendar]);

        excluded.Kind.Should().Be(RunDecisionKind.SkipExcluded);
        excluded.NextRunAtUtc.Should().Be(new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetNextAllowedOccurrence_ShouldReturnNull_WhenEveryDayIsExcluded()
    {
        var allWeek = new ExclusionCalendar { Name = "Never", Weekdays = [0, 1, 2, 3, 4, 5, 6] };

        RunPlanner.GetNextAllowedOccurrence(ZonedCron.Parse("0 0 9 * * *"), Utc(0, 0), TimeZoneInfo.Utc, [allWeek]).Should().BeNull();
    }

    [Fact]
    public void Outage_ShouldCoverOnlyTimesAfterLastPollUpToFirstPollBack()
    {
        _outage.Covers(Utc(10, 0)).Should().BeFalse();
        _outage.Covers(Utc(10, 0, 1)).Should().BeTrue();
        _outage.Covers(Utc(11, 0, 5)).Should().BeTrue();
        _outage.Covers(Utc(11, 0, 6)).Should().BeFalse();
    }

    [Fact]
    public void MisfireRules_ShouldUseSixtySecondGrace()
    {
        MisfireRules.IsMisfired(Utc(10, 0), Utc(10, 1)).Should().BeFalse();
        MisfireRules.IsMisfired(Utc(10, 0), Utc(10, 1, 1)).Should().BeTrue();
        MisfireRules.MisfireCutoff(Utc(10, 1)).Should().Be(Utc(10, 0));
    }
}
