using FluentAssertions;
using Milvaion.Infrastructure.BackgroundServices;
using Milvasoft.Milvaion.Sdk.Domain;

namespace Milvaion.UnitTests.InfrastructureTests;

/// <summary>
/// Order in which the dispatcher handles jobs that are due in the same cycle.
/// </summary>
public class DispatchPriorityTests
{
    private static readonly DateTime _base = new(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);

    private static ScheduledJob Job(string name, int priority, int secondsLate) => new()
    {
        Id = Guid.CreateVersion7(),
        DisplayName = name,
        Priority = priority,
        ExecuteAt = _base.AddSeconds(-secondsLate)
    };

    [Fact]
    public void OrderByPriority_ShouldPutHigherPriorityFirst_ThenLongestWaiting()
    {
        var jobs = new[]
        {
            Job("low-old", 0, 30),
            Job("high-new", 9, 1),
            Job("mid", 5, 10),
            Job("high-old", 9, 20),
            Job("low-new", 0, 2)
        };

        JobDispatcherService.OrderByPriority(jobs).Select(j => j.DisplayName).Should().Equal("high-old", "high-new", "mid", "low-old", "low-new");
    }

    [Fact]
    public void OrderByPriority_ShouldClampOutOfRangeValues()
    {
        var jobs = new[] { Job("nine", 9, 5), Job("huge", 50, 1), Job("negative", -3, 30), Job("zero", 0, 1) };

        // 50 is treated as 9 and -3 as 0; ties fall back to who waited longer.
        JobDispatcherService.OrderByPriority(jobs).Select(j => j.DisplayName).Should().Equal("nine", "huge", "negative", "zero");
    }

    [Fact]
    public void OrderByPriority_ShouldKeepTimeOrder_WhenAllPrioritiesAreDefault()
    {
        var jobs = new[] { Job("b", 0, 5), Job("a", 0, 15), Job("c", 0, 1) };

        JobDispatcherService.OrderByPriority(jobs).Select(j => j.DisplayName).Should().Equal("a", "b", "c");
    }
}
