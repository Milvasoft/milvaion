using FluentValidation;

namespace Milvaion.Application.Features.ExclusionCalendars;

/// <summary>
/// The fields a calendar is created and updated with.
/// </summary>
public interface IExclusionCalendarFields
{
    /// <summary>Unique name of the calendar.</summary>
    string Name { get; }

    /// <summary>What the calendar is for.</summary>
    string Description { get; }

    /// <summary>Single excluded dates.</summary>
    List<DateOnly> Dates { get; }

    /// <summary>Excluded date ranges, both ends inclusive.</summary>
    List<ExclusionDateRange> DateRanges { get; }

    /// <summary>Excluded days of the week (0 = Sunday ... 6 = Saturday).</summary>
    List<int> Weekdays { get; }
}

/// <summary>
/// Validation and clean-up shared by the create and update commands.
/// </summary>
public static class ExclusionCalendarRules
{
    /// <summary>Longest range accepted, so a typo in a year cannot create a range of centuries.</summary>
    public const int MaxRangeDays = 3660;

    /// <summary>
    /// Adds the rules for <see cref="IExclusionCalendarFields"/> to a validator.
    /// </summary>
    public static void AddFieldRules<T>(AbstractValidator<T> validator) where T : IExclusionCalendarFields
    {
        validator.RuleFor(c => c.Name)
                 .NotEmpty().WithMessage("Calendar name is required.")
                 .MaximumLength(100).WithMessage("Calendar name can be at most 100 characters.");

        validator.RuleFor(c => c.Description)
                 .MaximumLength(500).WithMessage("Description can be at most 500 characters.");

        validator.RuleFor(c => c.Dates)
                 .Must(d => d is null || d.Count <= SchedulingValidation.MaxCalendarEntries)
                 .WithMessage($"A calendar can hold at most {SchedulingValidation.MaxCalendarEntries} single dates.");

        validator.RuleFor(c => c.DateRanges)
                 .Must(r => r is null || r.Count <= SchedulingValidation.MaxCalendarEntries)
                 .WithMessage($"A calendar can hold at most {SchedulingValidation.MaxCalendarEntries} date ranges.");

        validator.RuleForEach(c => c.DateRanges)
                 .Must(r => r is not null && r.Start <= r.End)
                 .WithMessage("Each date range needs a start date on or before its end date.")
                 .Must(r => r is null || r.End.DayNumber - r.Start.DayNumber <= MaxRangeDays)
                 .WithMessage($"A date range can span at most {MaxRangeDays} days.");

        validator.RuleForEach(c => c.Weekdays)
                 .InclusiveBetween(0, 6)
                 .WithMessage("Weekdays are numbered 0 (Sunday) to 6 (Saturday).");

        validator.RuleFor(c => c)
                 .Must(c => (c.Dates?.Count ?? 0) + (c.DateRanges?.Count ?? 0) + (c.Weekdays?.Count ?? 0) > 0)
                 .WithMessage("A calendar needs at least one excluded date, date range or weekday.");
    }

    /// <summary>
    /// Copies the fields onto an entity, sorted and de-duplicated so the stored calendar reads cleanly.
    /// </summary>
    public static void Apply(IExclusionCalendarFields fields, ExclusionCalendar calendar)
    {
        calendar.Name = fields.Name?.Trim();
        calendar.Description = string.IsNullOrWhiteSpace(fields.Description) ? null : fields.Description.Trim();
        calendar.Dates = [.. (fields.Dates ?? []).Distinct().Order()];
        calendar.DateRanges = [.. (fields.DateRanges ?? []).Where(r => r is not null)
                                                           .Select(r => new ExclusionDateRange
                                                           {
                                                               Start = r.Start,
                                                               End = r.End,
                                                               Label = string.IsNullOrWhiteSpace(r.Label) ? null : r.Label.Trim()
                                                           })
                                                           .OrderBy(r => r.Start)];
        calendar.Weekdays = [.. (fields.Weekdays ?? []).Distinct().Order()];
    }
}
