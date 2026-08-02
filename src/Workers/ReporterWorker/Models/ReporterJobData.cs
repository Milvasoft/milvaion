using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ReporterWorker.Models;

/// <summary>
/// Period a report covers.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReportPeriod
{
    /// <summary> Previous complete calendar day. </summary>
    Daily,

    /// <summary> Previous complete ISO week (Monday–Monday). </summary>
    Weekly,

    /// <summary> Previous complete calendar month. </summary>
    Monthly,

    /// <summary> Explicit window given by <see cref="ReporterJobData.CustomStart"/> and <see cref="ReporterJobData.CustomEnd"/>. </summary>
    Custom,
}

/// <summary>
/// Parameters passed to every reporter job through the scheduler, controlling which window the report covers.
/// </summary>
/// <remarks>
/// The <see cref="Description"/> and <see cref="DefaultValueAttribute"/> annotations are what the scheduled-job
/// configuration UI reads to render the form, the same way <c>SqlJobData</c> does for the SQL worker. When a job
/// is dispatched without any data (an older schedule, or a quick manual run) the job falls back to a default
/// instance, i.e. <see cref="ReportPeriod.Daily"/>, preserving the original nightly behaviour.
/// </remarks>
public class ReporterJobData
{
    /// <summary>
    /// Reporting period.
    /// </summary>
    [DefaultValue(ReportPeriod.Daily)]
    [Description("Reporting period. Daily, Weekly and Monthly cover the previous complete period; Custom uses the dates below.")]
    public ReportPeriod Period { get; set; } = ReportPeriod.Daily;

    /// <summary>
    /// Start of the window (UTC). Required and used only when <see cref="Period"/> is <see cref="ReportPeriod.Custom"/>.
    /// </summary>
    [Description("Custom window start in UTC. Required and used only when Period is Custom.")]
    public DateTime? CustomStart { get; set; }

    /// <summary>
    /// End of the window (UTC). Required and used only when <see cref="Period"/> is <see cref="ReportPeriod.Custom"/>.
    /// </summary>
    [Description("Custom window end in UTC. Required and used only when Period is Custom.")]
    public DateTime? CustomEnd { get; set; }
}
