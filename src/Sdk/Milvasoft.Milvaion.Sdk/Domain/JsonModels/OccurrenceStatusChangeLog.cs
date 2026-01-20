using Milvasoft.Milvaion.Sdk.Domain.Enums;

namespace Milvasoft.Milvaion.Sdk.Domain.JsonModels;

/// <summary>
/// Represents status change history of a job occurrence.
/// </summary>
public class OccurrenceStatusChangeLog
{
    /// <summary>
    /// Timestamp when the status change entry was created (UTC).
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// From status of the job occurrence.
    /// </summary>
    public JobOccurrenceStatus From { get; set; }

    /// <summary>
    /// To status of the job occurrence.
    /// </summary>
    public JobOccurrenceStatus To { get; set; }
}
