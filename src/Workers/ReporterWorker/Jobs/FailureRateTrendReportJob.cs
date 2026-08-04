using Dapper;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using ReporterWorker.Models;
using ReporterWorker.Options;
using System.Text.Json;

namespace ReporterWorker.Jobs;

public class FailureRateTrendReportJob(IOptions<ReporterOptions> options) : IAsyncJobWithResult<ReporterJobData, string>
{
    private readonly ReporterOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Starting Failure Rate Trend Report generation");

        var jobData = context.GetData<ReporterJobData>() ?? new ReporterJobData();
        var window = ReportWindow.Resolve(jobData);
        var periodStart = window.Start;
        var periodEnd = window.End;

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        var sql = @"
            SELECT 
                DATE_TRUNC(@Bucket, ""StartTime"") as hour,
                COUNT(*) as total,
                SUM(CASE WHEN ""Status"" = 3 THEN 1 ELSE 0 END) as failed
            FROM ""JobOccurrences""
            WHERE ""StartTime"" >= @PeriodStart AND ""StartTime"" < @PeriodEnd
            GROUP BY DATE_TRUNC(@Bucket, ""StartTime"")
            ORDER BY hour";

        var queryTimeout = _options.ReportGeneration.QueryTimeoutSeconds;

        var hourlyStats = await connection.QueryAsync<(DateTime Hour, int Total, int Failed)>(
            new CommandDefinition(sql, new { PeriodStart = periodStart, PeriodEnd = periodEnd, window.Bucket },
                commandTimeout: queryTimeout, cancellationToken: context.CancellationToken));

        var data = new FailureRateTrendData
        {
            ThresholdPercentage = 5.0,
            DataPoints = [.. hourlyStats.Select(s => new TimeSeriesPoint
            {
                Timestamp = s.Hour,
                Value = s.Total > 0 ? (s.Failed * 100.0 / s.Total) : 0
            })]
        };

        var reportId = Guid.CreateVersion7();
        var report = new MetricReport
        {
            Id = reportId,
            MetricType = MetricTypes.FailureRateTrend,
            DisplayName = "Failure Rate Trend",
            Description = "Error rate changes over time",
            Data = JsonSerializer.Serialize(data),
            PeriodStartTime = periodStart,
            PeriodEndTime = periodEnd,
            GeneratedAt = DateTime.UtcNow,
            Period = window.PeriodLabel,
            Tags = "trend,failure,monitoring"
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

        context.LogInformation($"Failure Rate Trend Report generated with {data.DataPoints.Count} data points");

        return JsonSerializer.Serialize(new { Success = true, ReportId = reportId, DataPoints = data.DataPoints.Count });
    }
}
