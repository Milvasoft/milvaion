using Milvasoft.Milvaion.Sdk.Domain.Enums;

namespace Milvasoft.Milvaion.Sdk.Utils;

public static class MilvaionSdkExtensions
{
    /// <summary>
    /// Determines whether the JobOccurrenceStatus is a final status.
    /// </summary>
    /// <param name="status"></param>
    /// <returns></returns>
    public static bool IsFinalStatus(this JobOccurrenceStatus status)
        => status is JobOccurrenceStatus.Completed
                  or JobOccurrenceStatus.Failed
                  or JobOccurrenceStatus.Cancelled
                  or JobOccurrenceStatus.TimedOut
                  or JobOccurrenceStatus.Unknown;
}
