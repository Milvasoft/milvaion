using FluentAssertions;
using FluentValidation.TestHelper;
using Milvaion.Application.Features.ExclusionCalendars;
using Milvaion.Application.Features.ExclusionCalendars.CreateExclusionCalendar;
using Milvaion.Domain;
using Milvaion.Domain.JsonModels;

namespace Milvaion.UnitTests.ApplicationTests.Scheduling;

/// <summary>
/// Calendar matching and calendar input validation.
/// </summary>
public class ExclusionCalendarTests
{
    private static readonly ExclusionCalendar _calendar = new()
    {
        Name = "Holidays",
        Dates = [new DateOnly(2026, 4, 23)],
        DateRanges = [new ExclusionDateRange { Start = new DateOnly(2026, 12, 24), End = new DateOnly(2026, 12, 26) }],
        Weekdays = [(int)DayOfWeek.Sunday]
    };

    [Theory]
    [InlineData(2026, 4, 23, true)]   // single date
    [InlineData(2026, 4, 22, false)]
    [InlineData(2026, 12, 24, true)]  // range start
    [InlineData(2026, 12, 25, true)]  // inside range
    [InlineData(2026, 12, 26, true)]  // range end
    [InlineData(2026, 12, 23, false)]
    [InlineData(2026, 6, 14, true)]   // a Sunday
    [InlineData(2026, 6, 15, false)]  // a Monday
    public void Excludes_ShouldMatchDatesRangesAndWeekdays(int y, int m, int d, bool expected)
        => _calendar.Excludes(new DateOnly(y, m, d)).Should().Be(expected);

    [Fact]
    public void Excludes_ShouldCopeWithEmptyLists()
        => new ExclusionCalendar { Name = "Empty", Dates = null, DateRanges = null, Weekdays = null }.Excludes(new DateOnly(2026, 1, 1)).Should().BeFalse();

    [Fact]
    public void FindExcluding_ShouldReturnFirstMatchingCalendar()
    {
        var other = new ExclusionCalendar { Name = "Other", Dates = [new DateOnly(2026, 4, 23)] };

        ExclusionCalendar.FindExcluding([other, _calendar], new DateOnly(2026, 4, 23)).Should().BeSameAs(other);
        ExclusionCalendar.FindExcluding([other, _calendar], new DateOnly(2026, 4, 24)).Should().BeNull();
    }

    [Fact]
    public void Apply_ShouldSortAndDeduplicate()
    {
        var command = new CreateExclusionCalendarCommand
        {
            Name = "  Holidays ",
            Description = " ",
            Dates = [new DateOnly(2026, 5, 1), new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 1)],
            DateRanges = [new ExclusionDateRange { Start = new DateOnly(2026, 8, 1), End = new DateOnly(2026, 8, 2) },
                          new ExclusionDateRange { Start = new DateOnly(2026, 2, 1), End = new DateOnly(2026, 2, 2), Label = " Break " }],
            Weekdays = [6, 0, 6]
        };

        var calendar = new ExclusionCalendar();

        ExclusionCalendarRules.Apply(command, calendar);

        calendar.Name.Should().Be("Holidays");
        calendar.Description.Should().BeNull();
        calendar.Dates.Should().Equal(new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 1));
        calendar.DateRanges.Select(r => r.Start).Should().Equal(new DateOnly(2026, 2, 1), new DateOnly(2026, 8, 1));
        calendar.DateRanges[0].Label.Should().Be("Break");
        calendar.Weekdays.Should().Equal(0, 6);
    }

    [Fact]
    public void Validator_ShouldRejectBadInput()
    {
        var validator = new CreateExclusionCalendarCommandValidator();

        validator.TestValidate(new CreateExclusionCalendarCommand { Name = "", Weekdays = [1] }).ShouldHaveValidationErrorFor(c => c.Name);
        validator.TestValidate(new CreateExclusionCalendarCommand { Name = "Bad weekday", Weekdays = [7] }).IsValid.Should().BeFalse();
        validator.TestValidate(new CreateExclusionCalendarCommand
        {
            Name = "Reversed range",
            DateRanges = [new ExclusionDateRange { Start = new DateOnly(2026, 2, 2), End = new DateOnly(2026, 2, 1) }]
        }).IsValid.Should().BeFalse();
        validator.TestValidate(new CreateExclusionCalendarCommand { Name = "Empty" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_ShouldAcceptValidCalendar()
        => new CreateExclusionCalendarCommandValidator().TestValidate(new CreateExclusionCalendarCommand { Name = "Weekends", Weekdays = [0, 6] }).IsValid.Should().BeTrue();
}
