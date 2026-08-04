using Dapper;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using ReporterWorker.Models;
using ReporterWorker.Options;
using System.Text.Json;

namespace ReporterWorker.Jobs;

public class WorkflowDurationTrendReportJob(IOptions<ReporterOptions> options) : IAsyncJobWithResult<ReporterJobData, string>
{
    private readonly ReporterOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Starting Workflow Duration Trend Report generation");

        var jobData = context.GetData<ReporterJobData>() ?? new ReporterJobData();
        var window = ReportWindow.Resolve(jobData);
        var periodStart = window.Start;
        var periodEnd = window.End;

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        var sql = @"
            SELECT 
                DATE_TRUNC(@Bucket, wr.""StartTime"") as hour,
                w.""Name"" as workflow_name,
                AVG(wr.""DurationMs"") as avg_duration_ms
            FROM ""WorkflowRuns"" wr
            INNER JOIN ""Workflows"" w ON wr.""WorkflowId"" = w.""Id""
            WHERE wr.""StartTime"" >= @PeriodStart
                AND wr.""StartTime"" < @PeriodEnd
                AND wr.""Status"" IN (2, 3, 5)
                AND wr.""DurationMs"" IS NOT NULL
            GROUP BY DATE_TRUNC(@Bucket, wr.""StartTime""), w.""Name""
            ORDER BY hour";

        var queryTimeout = _options.ReportGeneration.QueryTimeoutSeconds;

        var rows = await connection.QueryAsync<(DateTime Hour, string WorkflowName, double AvgDurationMs)>(
            new CommandDefinition(sql, new { PeriodStart = periodStart, PeriodEnd = periodEnd, window.Bucket },
                commandTimeout: queryTimeout, cancellationToken: context.CancellationToken));

        var data = new WorkflowDurationTrendData
        {
            DataPoints = [.. rows.GroupBy(r => r.Hour)
                .OrderBy(g => g.Key)
                .Select(g => new WorkflowDurationPoint
                {
                    Timestamp = g.Key,
                    WorkflowAvgDurationMs = g.ToDictionary(r => r.WorkflowName, r => Math.Round(r.AvgDurationMs, 2))
                })]
        };

        var reportId = Guid.CreateVersion7();
        var report = new MetricReport
        {
            Id = reportId,
            MetricType = MetricTypes.WorkflowDurationTrend,
            DisplayName = "Workflow Duration Trend",
            Description = "Workflow execution duration over time",
            Data = JsonSerializer.Serialize(data),
            PeriodStartTime = periodStart,
            PeriodEndTime = periodEnd,
            GeneratedAt = DateTime.UtcNow,
            Period = window.PeriodLabel,
            Tags = "workflow,duration,trend,timeseries"
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

        context.LogInformation($"Workflow Duration Trend Report generated with {data.DataPoints.Count} time points");

        return JsonSerializer.Serialize(new { Success = true, ReportId = reportId, TimePoints = data.DataPoints.Count });
    }
}
