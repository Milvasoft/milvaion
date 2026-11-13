using Microsoft.EntityFrameworkCore;
using Milvaion.Domain.JsonModels;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// A named set of days on which jobs that reference it do not run - public holidays, maintenance windows,
/// company shutdowns.
/// </summary>
/// <remarks>
/// A calendar holds three kinds of rule and a day is excluded when any of them matches: single dates, inclusive
/// date ranges, and days of the week. Dates are calendar dates without a time or a zone; each job compares them
/// with the local date of its run in its own time zone, so one "public holidays" calendar works for jobs in
/// different zones.
/// </remarks>
[Table(TableNames.ExclusionCalendars)]
[Index(nameof(Name), IsUnique = true)]
public class ExclusionCalendar : AuditableEntity<Guid>
{
    /// <summary>
    /// Unique name of the calendar.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; }

    /// <summary>
    /// What the calendar is for.
    /// </summary>
    [MaxLength(500)]
    public string Description { get; set; }

    /// <summary>
    /// Single excluded dates.
    /// </summary>
    public List<DateOnly> Dates { get; set; } = [];

    /// <summary>
    /// Excluded date ranges, both ends inclusive.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<ExclusionDateRange> DateRanges { get; set; } = [];

    /// <summary>
    /// Excluded days of the week, as <see cref="DayOfWeek"/> values (0 = Sunday ... 6 = Saturday).
    /// </summary>
    public List<int> Weekdays { get; set; } = [];

    /// <summary>
    /// Returns true when <paramref name="date"/> is excluded by this calendar.
    /// </summary>
    public bool Excludes(DateOnly date)
    {
        if (Weekdays?.Contains((int)date.DayOfWeek) == true)
            return true;

        if (Dates?.Contains(date) == true)
            return true;

        return DateRanges?.Any(r => r.Contains(date)) == true;
    }

    /// <summary>
    /// Returns the first calendar that excludes <paramref name="date"/>, or null when none does.
    /// </summary>
    public static ExclusionCalendar FindExcluding(IEnumerable<ExclusionCalendar> calendars, DateOnly date)
        => calendars?.FirstOrDefault(c => c is not null && c.Excludes(date));
}
