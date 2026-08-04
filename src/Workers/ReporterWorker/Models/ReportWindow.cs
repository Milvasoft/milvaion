using Milvasoft.Milvaion.Sdk.Worker.Exceptions;

namespace ReporterWorker.Models;

/// <summary>
/// The UTC analysis window a report covers, together with the time-series bucket granularity to use for it.
/// </summary>
/// <remarks>
/// Every boundary is snapped to a whole day (or the start of the current UTC hour for sub-day windows) rather than
/// taken as "this exact instant". The reporter jobs fire on the same schedule as the jobs they measure (for example
/// the midnight cron), but only begin executing a few seconds later once dequeued from RabbitMQ. A window anchored
/// to the exact start time therefore opens a few seconds <i>after</i> the batch it is meant to summarise, so the
/// aligned executions fall just below <see cref="Start"/> and are excluded - which is why reports came back empty
/// even though the jobs ran. Snapping to a boundary makes the window contain the whole previous period and leaves
/// the still-running current batch out of it.
/// <para>
/// <see cref="Bucket"/> is <c>hour</c> for daily windows and <c>day</c> for weekly/monthly (and long custom) windows,
/// so time-series reports do not explode into hundreds of points over a month.
/// </para>
/// </remarks>
public readonly record struct ReportWindow(DateTime Start, DateTime End, string Bucket, string PeriodLabel)
{
    /// <summary>
    /// Resolves the window for the requested <see cref="ReporterJobData.Period"/>.
    /// </summary>
    public static ReportWindow Resolve(ReporterJobData data)
    {
        var now = DateTime.UtcNow;
        var today = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

        return data.Period switch
        {
            ReportPeriod.Weekly => Weekly(today),
            ReportPeriod.Monthly => Monthly(now),
            ReportPeriod.Custom => Custom(data),
            _ => new ReportWindow(today.AddDays(-1), today, "hour", nameof(ReportPeriod.Daily)),
        };
    }

    private static ReportWindow Weekly(DateTime today)
    {
        // Monday 00:00 of the current ISO week; DayOfWeek has Sunday = 0, so shift so Monday = 0.
        var startOfThisWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        return new ReportWindow(startOfThisWeek.AddDays(-7), startOfThisWeek, "day", nameof(ReportPeriod.Weekly));
    }

    private static ReportWindow Monthly(DateTime now)
    {
        var startOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        return new ReportWindow(startOfThisMonth.AddMonths(-1), startOfThisMonth, "day", nameof(ReportPeriod.Monthly));
    }

    private static ReportWindow Custom(ReporterJobData data)
    {
        if (data.CustomStart is null || data.CustomEnd is null)
            throw new PermanentJobException("Period is Custom but CustomStart and CustomEnd were not both provided.");

        var start = AsUtc(data.CustomStart.Value);
        var end = AsUtc(data.CustomEnd.Value);

        if (end <= start)
            throw new PermanentJobException($"Custom window end ({end:o}) must be after start ({start:o}).");

        // Anything longer than a couple of days is bucketed per day so the series stays readable.
        var bucket = end - start > TimeSpan.FromDays(2) ? "day" : "hour";

        return new ReportWindow(start, end, bucket, nameof(ReportPeriod.Custom));
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
