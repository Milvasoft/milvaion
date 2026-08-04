using Dapper;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using ReporterWorker.Models;
using ReporterWorker.Options;
using System.Text.Json;

namespace ReporterWorker.Jobs;

public class WorkflowStepBottleneckReportJob(IOptions<ReporterOptions> options) : IAsyncJobWithResult<ReporterJobData, string>
{
    private readonly ReporterOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Starting Workflow Step Bottleneck Report generation");

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
                jo.""JobName"" as step_name,
                AVG(jo.""DurationMs"") as avg_duration_ms,
                MAX(jo.""DurationMs"") as max_duration_ms,
                COUNT(*) as execution_count,
                SUM(CASE WHEN jo.""StepStatus"" = 3 THEN 1 ELSE 0 END) as failure_count,
                SUM(CASE WHEN jo.""StepStatus"" = 4 THEN 1 ELSE 0 END) as skipped_count,
                CAST(SUM(jo.""StepRetryCount"") AS bigint) as retry_count
            FROM ""JobOccurrences"" jo
            INNER JOIN ""WorkflowRuns"" wr ON jo.""WorkflowRunId"" = wr.""Id""
            INNER JOIN ""Workflows"" w ON wr.""WorkflowId"" = w.""Id""
            WHERE jo.""StartTime"" >= @PeriodStart
                AND jo.""StartTime"" < @PeriodEnd
                AND jo.""WorkflowRunId"" IS NOT NULL
            GROUP BY wr.""WorkflowId"", w.""Name"", jo.""JobName""
            ORDER BY wr.""WorkflowId"", AVG(jo.""DurationMs"") DESC
            LIMIT @MaxGroups";

        var queryTimeout = _options.ReportGeneration.QueryTimeoutSeconds;

        var rows = await connection.QueryAsync<(Guid WorkflowId, string WorkflowName, string StepName, double? AvgDurationMs, long? MaxDurationMs, int ExecutionCount, int FailureCount, int SkippedCount, long RetryCount)>(
            new CommandDefinition(sql, new { PeriodStart = periodStart, PeriodEnd = periodEnd, MaxGroups = _options.ReportGeneration.MaxGroupsPerReport },
                commandTimeout: queryTimeout, cancellationToken: context.CancellationToken));

        var data = new WorkflowStepBottleneckData
        {
            Workflows = [.. rows.GroupBy(r => new { r.WorkflowId, r.WorkflowName })
                .Select(g => new WorkflowBottleneckInfo
                {
                    WorkflowId = g.Key.WorkflowId,
                    WorkflowName = g.Key.WorkflowName,
                    Steps = [.. g.Select(r => new StepPerformanceInfo
                    {
                        StepName = r.StepName,
                        AvgDurationMs = r.AvgDurationMs ?? 0,
                        MaxDurationMs = r.MaxDurationMs ?? 0,
                        ExecutionCount = r.ExecutionCount,
                        FailureCount = r.FailureCount,
                        SkippedCount = r.SkippedCount,
                        RetryCount = (int)r.RetryCount
                    }).OrderByDescending(s => s.AvgDurationMs)]
                })]
        };

        var reportId = Guid.CreateVersion7();
        var report = new MetricReport
        {
            Id = reportId,
            MetricType = MetricTypes.WorkflowStepBottleneck,
            DisplayName = "Workflow Step Bottleneck",
            Description = "Step-level performance analysis per workflow",
            Data = JsonSerializer.Serialize(data),
            PeriodStartTime = periodStart,
            PeriodEndTime = periodEnd,
            GeneratedAt = DateTime.UtcNow,
            Period = window.PeriodLabel,
            Tags = "workflow,bottleneck,step,performance"
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

        context.LogInformation($"Workflow Step Bottleneck Report generated with {data.Workflows.Count} workflows");

        return JsonSerializer.Serialize(new { Success = true, ReportId = reportId, WorkflowCount = data.Workflows.Count });
    }
}
