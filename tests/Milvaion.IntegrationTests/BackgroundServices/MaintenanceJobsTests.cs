using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Milvaion.IntegrationTests.TestBase;
using MilvaionMaintenanceWorker.Jobs;
using MilvaionMaintenanceWorker.Options;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Worker.Core;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Npgsql;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Runs the maintenance jobs against the schema produced by the real migrations,
/// since what they get wrong is the shape of hand-written SQL.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class MaintenanceJobsTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    private static JobContext CreateContext() => new(Guid.CreateVersion7(), new ScheduledJob(), "maintenance-test", null, null, new JobConsumerConfig(), CancellationToken.None);

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_factory.GetConnectionString());

        await connection.OpenAsync();

        return connection;
    }

    [Fact]
    public async Task OccurrenceArchiveJob_ShouldMoveOldOccurrencesAndLogs_AndLeaveRecentOnes()
    {
        // Arrange
        var job = await SeedScheduledJobAsync();
        var old = await SeedJobOccurrenceAsync(job.Id, status: JobOccurrenceStatus.Completed, createdAt: DateTime.UtcNow.AddDays(-200));
        var recent = await SeedJobOccurrenceAsync(job.Id, status: JobOccurrenceStatus.Completed, createdAt: DateTime.UtcNow);

        var prefix = $"ArchTest{Guid.NewGuid():N}"[..16];

        var options = new MaintenanceOptions
        {
            DatabaseConnectionString = _factory.GetConnectionString(),
            OccurrenceArchive = new OccurrenceArchiveSettings
            {
                ArchiveAfterDays = 90,
                ArchiveTablePrefix = prefix,
                StatusesToArchive = [(int)JobOccurrenceStatus.Completed],
                BatchSize = 100,
                VacuumAfterArchive = false
            }
        };

        // Act
        var result = await new OccurrenceArchiveJob(Microsoft.Extensions.Options.Options.Create(options)).ExecuteAsync(CreateContext());

        // Assert
        using var json = JsonDocument.Parse(result);

        json.RootElement.GetProperty("ArchivedCount").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("ArchivedLogsCount").GetInt32().Should().Be(1);

        var archiveTable = json.RootElement.GetProperty("ArchiveTable").GetString();
        var archiveLogsTable = json.RootElement.GetProperty("ArchiveLogsTable").GetString();

        await using var connection = await OpenConnectionAsync();

        try
        {
            (await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""JobOccurrences"" WHERE ""Id"" = @Id", new { old.Id })).Should().Be(0);
            (await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""JobOccurrenceLogs"" WHERE ""OccurrenceId"" = @Id", new { old.Id })).Should().Be(0);
            (await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""JobOccurrences"" WHERE ""Id"" = @Id", new { recent.Id })).Should().Be(1);

            (await connection.ExecuteScalarAsync<string>($@"SELECT ""JobName"" FROM ""{archiveTable}"" WHERE ""Id"" = @Id", new { old.Id })).Should().Be(old.JobName);
            (await connection.ExecuteScalarAsync<string>($@"SELECT ""Level"" FROM ""{archiveLogsTable}"" WHERE ""OccurrenceId"" = @Id", new { old.Id })).Should().Be("Information");
            json.RootElement.GetProperty("ArchiveTableSize").GetInt64().Should().BeGreaterThan(0);
        }
        finally
        {
            await connection.ExecuteAsync($@"DROP TABLE IF EXISTS ""{archiveTable}"", ""{archiveLogsTable}""");
        }
    }

    [Fact]
    public async Task OccurrenceArchiveJob_ShouldReplaceEmptyTableLeftByOldSchema()
    {
        // Arrange - the table earlier versions created before every insert into it failed
        var job = await SeedScheduledJobAsync();
        var old = await SeedJobOccurrenceAsync(job.Id, status: JobOccurrenceStatus.Completed, createdAt: DateTime.UtcNow.AddDays(-200));

        var prefix = $"ArchTest{Guid.NewGuid():N}"[..16];
        var archiveTable = $"{prefix}_{DateTime.UtcNow:yyyy}_{DateTime.UtcNow:MM}";

        await using var connection = await OpenConnectionAsync();

        await connection.ExecuteAsync($@"CREATE TABLE ""{archiveTable}"" (""Id"" uuid PRIMARY KEY, ""ScheduledTime"" timestamp with time zone NOT NULL)");

        var options = new MaintenanceOptions
        {
            DatabaseConnectionString = _factory.GetConnectionString(),
            OccurrenceArchive = new OccurrenceArchiveSettings
            {
                ArchiveTablePrefix = prefix,
                StatusesToArchive = [(int)JobOccurrenceStatus.Completed],
                VacuumAfterArchive = false
            }
        };

        try
        {
            // Act
            var result = await new OccurrenceArchiveJob(Microsoft.Extensions.Options.Options.Create(options)).ExecuteAsync(CreateContext());

            // Assert
            using var json = JsonDocument.Parse(result);

            json.RootElement.GetProperty("ArchivedCount").GetInt32().Should().Be(1);
            (await connection.ExecuteScalarAsync<int>($@"SELECT COUNT(*) FROM ""{archiveTable}"" WHERE ""Id"" = @Id", new { old.Id })).Should().Be(1);
        }
        finally
        {
            await connection.ExecuteAsync($@"DROP TABLE IF EXISTS ""{archiveTable}"", ""{archiveTable}_Logs""");
        }
    }

    [Fact]
    public async Task OccurrenceRetentionJob_ShouldDeleteOccurrenceAndLogsTogether()
    {
        // Arrange
        var job = await SeedScheduledJobAsync();
        var old = await SeedJobOccurrenceAsync(job.Id, status: JobOccurrenceStatus.Completed, createdAt: DateTime.UtcNow.AddDays(-200));
        var recent = await SeedJobOccurrenceAsync(job.Id, status: JobOccurrenceStatus.Completed, createdAt: DateTime.UtcNow);

        var options = new MaintenanceOptions
        {
            DatabaseConnectionString = _factory.GetConnectionString(),
            OccurrenceRetention = new OccurrenceRetentionSettings { VacuumAfterCleanup = false }
        };

        // Act
        await new OccurrenceRetentionJob(Microsoft.Extensions.Options.Options.Create(options)).ExecuteAsync(CreateContext());

        // Assert
        await using var connection = await OpenConnectionAsync();

        (await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""JobOccurrences"" WHERE ""Id"" = @Id", new { old.Id })).Should().Be(0);
        (await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""JobOccurrenceLogs"" WHERE ""OccurrenceId"" = @Id", new { old.Id })).Should().Be(0);
        (await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""JobOccurrences"" WHERE ""Id"" = @Id", new { recent.Id })).Should().Be(1);
        (await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""JobOccurrenceLogs"" WHERE ""OccurrenceId"" = @Id", new { recent.Id })).Should().Be(1);
    }
}
