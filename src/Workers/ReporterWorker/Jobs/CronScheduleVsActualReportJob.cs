using Dapper;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using ReporterWorker.Models;
using ReporterWorker.Options;
using System.Text.Json;

namespace ReporterWorker.Jobs;

public class CronScheduleVsActualReportJob(IOptions<ReporterOptions> options) : IAsyncJobWithResult<ReporterJobData, string>
{
    private readonly ReporterOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Starting Cron Schedule vs Actual Report generation");

        var jobData = context.GetData<ReporterJobData>() ?? new ReporterJobData();
        var window = ReportWindow.Resolve(jobData);
        var periodStart = window.Start;
        var periodEnd = window.End;

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        var sql = @"
            SELECT 
                jo.""Id"" as occurrence_id,
                jo.""JobId"" as job_id,
                jo.""JobName"",
                jo.""CreatedAt"" as scheduled_time,
                jo.""StartTime"" as actual_time,
                EXTRACT(EPOCH FROM (jo.""StartTime"" - jo.""CreatedAt"")) as deviation_seconds
            FROM ""JobOccurrences"" jo
            INNER JOIN ""ScheduledJobs"" sj ON jo.""JobId"" = sj.""Id""
            WHERE jo.""StartTime"" >= @PeriodStart
                AND jo.""StartTime"" < @PeriodEnd
                AND sj.""CronExpression"" IS NOT NULL
                AND jo.""Status"" IN (2, 3)
            ORDER BY ABS(EXTRACT(EPOCH FROM (jo.""StartTime"" - jo.""CreatedAt""))) DESC
            LIMIT @TopN";

        var queryTimeout = _options.ReportGeneration.QueryTimeoutSeconds;

        var deviations = await connection.QueryAsync<(Guid OccurrenceId, Guid JobId, string JobName, DateTime ScheduledTime, DateTime ActualTime, double DeviationSeconds)>(
            new CommandDefinition(sql, new { PeriodStart = periodStart, PeriodEnd = periodEnd, TopN = _options.ReportGeneration.MaxScheduleDeviations },
                commandTimeout: queryTimeout, cancellationToken: context.CancellationToken));

        var data = new CronScheduleVsActualData
        {
            Jobs = [.. deviations.Select(d => new ScheduleDeviationInfo
            {
                OccurrenceId = d.OccurrenceId,
                JobId = d.JobId,
                JobName = d.JobName,
                ScheduledTime = d.ScheduledTime,
                ActualTime = d.ActualTime,
                DeviationSeconds = d.DeviationSeconds
            })]
        };

        var reportId = Guid.CreateVersion7();
        var report = new MetricReport
        {
            Id = reportId,
            MetricType = MetricTypes.CronScheduleVsActual,
            DisplayName = "Cron Schedule vs Actual",
            Description = "Scheduled vs actual execution time deviation",
            Data = JsonSerializer.Serialize(data),
            PeriodStartTime = periodStart,
            PeriodEndTime = periodEnd,
            GeneratedAt = DateTime.UtcNow,
            Period = window.PeriodLabel,
            Tags = "scheduling,cron,deviation,timing"
        };

        var insertSql = @"
            INSERT INTO ""MetricReports""
            (""Id"", ""MetricType"", ""DisplayName"", ""Description"", ""Data"", ""DataSizeBytes"",
             ""PeriodStartTime"", ""PeriodEndTime"", ""GeneratedAt"", ""Tags"", ""Period"", ""CreationDate"")
            VALUES
            (@Id, @MetricType, @DisplayName, @Description, @Data::jsonb, @DataSizeBytes,
             @PeriodStartTime, @PeriodEndTime, @GeneratedAt, @Tags, @Period, @CreationDate)";

        await connection.ExecuteAsync(new CommandDefinition(insertSql, new
        {
            report.Id,
            report.MetricType,
            report.DisplayName,
            report.Description,
            report.Data,
            DataSizeBytes = report.Data.Length,
            report.PeriodStartTime,
            report.PeriodEndTime,
            report.GeneratedAt,
            report.Tags,
            report.Period,
            CreationDate = DateTime.UtcNow
        }, commandTimeout: queryTimeout, cancellationToken: context.CancellationToken));

        context.LogInformation($"Cron Schedule vs Actual Report generated with {data.Jobs.Count} deviations");

        return JsonSerializer.Serialize(new { Success = true, ReportId = reportId, DeviationCount = data.Jobs.Count });
    }
}
