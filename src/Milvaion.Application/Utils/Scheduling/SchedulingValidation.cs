namespace Milvaion.Application.Utils.Scheduling;

/// <summary>
/// Limits and messages shared by the job create and update validators and the calendar validators.
/// </summary>
public static class SchedulingValidation
{
    /// <summary>Upper bound for <see cref="ScheduledJob.MaxCatchUpRuns"/>.</summary>
    public const int MaxCatchUpRunsLimit = 100;

    /// <summary>How many calendars one job may reference.</summary>
    public const int MaxCalendarsPerJob = 20;

    /// <summary>How many single dates or ranges one calendar may hold.</summary>
    public const int MaxCalendarEntries = 1000;

    /// <summary>Message for an unknown or non-IANA time zone id.</summary>
    public static string InvalidTimeZoneMessage(string timeZoneId)
        => $"'{timeZoneId}' is not a valid IANA time zone id. Use an id such as 'UTC', 'Europe/Istanbul' or 'America/New_York'.";

    /// <summary>Message for an out of range missed-run policy.</summary>
    public const string InvalidMissedRunPolicyMessage = "Missed-run policy must be Skip, RunOnce or RunAll.";

    /// <summary>Message for an out of range catch-up maximum.</summary>
    public static readonly string InvalidMaxCatchUpRunsMessage = $"Maximum catch-up runs must be between 1 and {MaxCatchUpRunsLimit}.";

    /// <summary>Message for an out of range priority.</summary>
    public static readonly string InvalidPriorityMessage = $"Priority must be between {ScheduledJob.MinPriority} and {ScheduledJob.MaxPriority}.";

    /// <summary>Message for too many calendars on a job.</summary>
    public static readonly string TooManyCalendarsMessage = $"A job can reference at most {MaxCalendarsPerJob} exclusion calendars.";
}
