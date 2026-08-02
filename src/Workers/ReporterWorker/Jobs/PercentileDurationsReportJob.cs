using Dapper;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using ReporterWorker.Models;
using ReporterWorker.Options;
using System.Text.Json;

namespace ReporterWorker.Jobs;

public class PercentileDurationsReportJob(IOptions<ReporterOptions> options) : IAsyncJobWithResult<ReporterJobData, string>
{
    private readonly ReporterOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Starting Percentile Durations Report generation");

        var jobData = context.GetData<ReporterJobData>() ?? new ReporterJobData();
        var window = ReportWindow.Resolve(jobData);
        var periodStart = window.Start;
        var periodEnd = window.End;

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        var sql = @"
            SELECT 
                ""JobName"",
                PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ""DurationMs"") as p50,
                PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY ""DurationMs"") as p95,
                PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY ""DurationMs"") as p99
            FROM ""JobOccurrences""
            WHERE ""StartTime"" >= @PeriodStart
                AND ""StartTime"" < @PeriodEnd
                AND ""DurationMs"" IS NOT NULL
            GROUP BY ""JobName""
            HAVING COUNT(*) >= 10
            ORDER BY p99 DESC
            LIMIT @MaxGroups";

        var queryTimeout = _options.ReportGeneration.QueryTimeoutSeconds;

        var jobStats = await connection.QueryAsync<(string JobName, double P50, double P95, double P99)>(
            new CommandDefinition(sql, new { PeriodStart = periodStart, PeriodEnd = periodEnd, MaxGroups = _options.ReportGeneration.MaxGroupsPerReport },
                commandTimeout: queryTimeout, cancellationToken: context.CancellationToken));

        var data = new PercentileDurationsData
        {
            Jobs = jobStats.ToDictionary(
                s => s.JobName,
                s => new PercentileData
                {
                    P50 = s.P50,
                    P95 = s.P95,
                    P99 = s.P99
                })
        };

        var reportId = Guid.CreateVersion7();
        var report = new MetricReport
        {
            Id = reportId,
            MetricType = MetricTypes.PercentileDurations,
            DisplayName = "P50 / P95 / P99 Durations",
            Description = "Percentile-based duration distribution",
            Data = JsonSerializer.Serialize(data),
            PeriodStartTime = periodStart,
            PeriodEndTime = periodEnd,
            GeneratedAt = DateTime.UtcNow,
            Period = window.PeriodLabel,
            Tags = "performance,duration,percentile"
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

        context.LogInformation($"Percentile Durations Report generated for {data.Jobs.Count} jobs");

        return JsonSerializer.Serialize(new { Success = true, ReportId = reportId, JobCount = data.Jobs.Count });
    }
}
