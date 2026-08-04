using Dapper;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using ReporterWorker.Models;
using ReporterWorker.Options;
using System.Text.Json;

namespace ReporterWorker.Jobs;

public class WorkflowSuccessRateReportJob(IOptions<ReporterOptions> options) : IAsyncJobWithResult<ReporterJobData, string>
{
    private readonly ReporterOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Starting Workflow Success Rate Report generation");

        var jobData = context.GetData<ReporterJobData>() ?? new ReporterJobData();
        var window = ReportWindow.Resolve(jobData);
        var periodStart = window.Start;
        var periodEnd = window.End;

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        var sql = @"
            SELECT 
                wr.""WorkflowId"" as workflow_id,
                w.""Name"" as workflow_name,
                COUNT(*) as total_runs,
                SUM(CASE WHEN wr.""Status"" = 2 THEN 1 ELSE 0 END) as completed_count,
                SUM(CASE WHEN wr.""Status"" = 3 THEN 1 ELSE 0 END) as failed_count,
                SUM(CASE WHEN wr.""Status"" = 5 THEN 1 ELSE 0 END) as partial_count,
                SUM(CASE WHEN wr.""Status"" = 4 THEN 1 ELSE 0 END) as cancelled_count,
                AVG(wr.""DurationMs"") as avg_duration_ms
            FROM ""WorkflowRuns"" wr
            INNER JOIN ""Workflows"" w ON wr.""WorkflowId"" = w.""Id""
            WHERE wr.""StartTime"" >= @PeriodStart
                AND wr.""StartTime"" < @PeriodEnd
                AND wr.""Status"" IN (2, 3, 4, 5)
            GROUP BY wr.""WorkflowId"", w.""Name""
            ORDER BY COUNT(*) DESC
            LIMIT @MaxGroups";

        var queryTimeout = _options.ReportGeneration.QueryTimeoutSeconds;

        var stats = await connection.QueryAsync<(Guid WorkflowId, string WorkflowName, int TotalRuns, int CompletedCount, int FailedCount, int PartialCount, int CancelledCount, double? AvgDurationMs)>(
            new CommandDefinition(sql, new { PeriodStart = periodStart, PeriodEnd = periodEnd, MaxGroups = _options.ReportGeneration.MaxGroupsPerReport },
                commandTimeout: queryTimeout, cancellationToken: context.CancellationToken));

        var data = new WorkflowSuccessRateData
        {
            Workflows = [.. stats.Select(s => new WorkflowHealthInfo
            {
                WorkflowId = s.WorkflowId,
                WorkflowName = s.WorkflowName,
                TotalRuns = s.TotalRuns,
                CompletedCount = s.CompletedCount,
                FailedCount = s.FailedCount,
                PartialCount = s.PartialCount,
                CancelledCount = s.CancelledCount,
                SuccessRate = s.TotalRuns > 0 ? (s.CompletedCount + s.PartialCount) * 100.0 / s.TotalRuns : 0,
                AvgDurationMs = s.AvgDurationMs ?? 0
            })]
        };

        var reportId = Guid.CreateVersion7();
        var report = new MetricReport
        {
            Id = reportId,
            MetricType = MetricTypes.WorkflowSuccessRate,
            DisplayName = "Workflow Success Rate",
            Description = "Success and failure rates for each workflow",
            Data = JsonSerializer.Serialize(data),
            PeriodStartTime = periodStart,
            PeriodEndTime = periodEnd,
            GeneratedAt = DateTime.UtcNow,
            Period = window.PeriodLabel,
            Tags = "workflow,success-rate,health"
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

        context.LogInformation($"Workflow Success Rate Report generated with {data.Workflows.Count} workflows");

        return JsonSerializer.Serialize(new { Success = true, ReportId = reportId, WorkflowCount = data.Workflows.Count });
    }
}
