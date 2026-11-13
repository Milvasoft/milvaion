using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ExclusionCalendarDtos;

/// <summary>
/// An exclusion calendar as listed.
/// </summary>
[Translate]
public class ExclusionCalendarListDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Unique name of the calendar.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// What the calendar is for.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Number of single excluded dates.
    /// </summary>
    public int DateCount { get; set; }

    /// <summary>
    /// Number of excluded date ranges.
    /// </summary>
    public int DateRangeCount { get; set; }

    /// <summary>
    /// Excluded days of the week (0 = Sunday ... 6 = Saturday).
    /// </summary>
    public List<int> Weekdays { get; set; }

    /// <summary>
    /// When the calendar was created.
    /// </summary>
    public DateTime? CreationDate { get; set; }

    /// <summary>
    /// Projection from the entity.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<ExclusionCalendar, ExclusionCalendarListDto>> Projection { get; } = c => new ExclusionCalendarListDto
    {
        Id = c.Id,
        Name = c.Name,
        Description = c.Description,
        DateCount = c.Dates.Count,
        DateRangeCount = c.DateRanges.Count,
        Weekdays = c.Weekdays,
        CreationDate = c.CreationDate
    };
}

/// <summary>
/// An exclusion calendar in full, with the jobs that use it.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class ExclusionCalendarDetailDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Unique name of the calendar.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// What the calendar is for.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Single excluded dates, sorted.
    /// </summary>
    public List<DateOnly> Dates { get; set; }

    /// <summary>
    /// Excluded date ranges, both ends inclusive.
    /// </summary>
    public List<ExclusionDateRange> DateRanges { get; set; }

    /// <summary>
    /// Excluded days of the week (0 = Sunday ... 6 = Saturday).
    /// </summary>
    public List<int> Weekdays { get; set; }

    /// <summary>
    /// Jobs that reference this calendar.
    /// </summary>
    public List<ExclusionCalendarJobRefDto> Jobs { get; set; } = [];

    /// <summary>
    /// Information about record audit.
    /// </summary>
    public AuditDto<Guid> AuditInfo { get; set; }

    /// <summary>
    /// Projection from the entity.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<ExclusionCalendar, ExclusionCalendarDetailDto>> Projection { get; } = c => new ExclusionCalendarDetailDto
    {
        Id = c.Id,
        Name = c.Name,
        Description = c.Description,
        Dates = c.Dates,
        DateRanges = c.DateRanges,
        Weekdays = c.Weekdays,
        AuditInfo = new AuditDto<Guid>(c)
    };
}

/// <summary>
/// A short reference to a calendar, as shown on a job.
/// </summary>
public class ExclusionCalendarRefDto
{
    /// <summary>
    /// Calendar id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Calendar name.
    /// </summary>
    public string Name { get; set; }
}

/// <summary>
/// A short reference to a job, as shown on a calendar.
/// </summary>
public class ExclusionCalendarJobRefDto
{
    /// <summary>
    /// Job id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Job display name.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Whether the job is active.
    /// </summary>
    public bool IsActive { get; set; }
}
