using Microsoft.EntityFrameworkCore;
using Milvaion.Domain.Enums;
using Milvasoft.Core.EntityBases.Concrete;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the ActivityLogs table.
/// </summary>
[Table(TableNames.ActivityLogs)]
[Index(nameof(ActivityDate), IsDescending = [true])]
public class ActivityLog : BaseEntity<int>
{
    /// <summary>
    /// Username of the user performing the activity.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string UserName { get; set; }

    /// <summary>
    /// Description of the activity performed.
    /// </summary>
    [Required]
    public UserActivity Activity { get; set; }

    /// <summary>
    /// Date and time when the activity occurred.
    /// </summary>
    [Required]
    [MaxLength(255)]
    public DateTimeOffset ActivityDate { get; set; }
}
