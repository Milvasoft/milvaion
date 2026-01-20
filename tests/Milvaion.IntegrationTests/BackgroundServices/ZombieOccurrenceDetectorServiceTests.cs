using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for ZombieOccurrenceDetectorService.
/// Tests zombie detection for stuck Queued occurrences and job-specific timeouts.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
public class ZombieOccurrenceDetectorServiceTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    [Fact]
    public async Task DetectAndCleanupZombieOccurrences_ShouldMarkStuckOccurrencesAsUnkown()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"ZombieTestJob_{Guid.NewGuid():N}");

        // Create an occurrence that is "stuck" in Queued status for longer than the timeout
        var stuckOccurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Queued,
            createdAt: DateTime.UtcNow.AddMinutes(-15) // Older than default 10 minute timeout
        );

        // Create a recent occurrence that should NOT be marked as zombie
        var recentOccurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Queued,
            createdAt: DateTime.UtcNow.AddMinutes(-1) // Very recent, should not be zombie
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start the service
        var service = CreateZombieDetectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for zombie detection
        var found = await WaitForConditionAsync(
            async () =>
            {
                var stuck = await GetOccurrenceAsync(stuckOccurrence.Id);
                return stuck?.Status == JobOccurrenceStatus.Unknown;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("stuck occurrence should be marked as Failed");

        var stuckResult = await GetOccurrenceAsync(stuckOccurrence.Id);
        var recentResult = await GetOccurrenceAsync(recentOccurrence.Id);

        stuckResult.Status.Should().Be(JobOccurrenceStatus.Unknown);
        stuckResult.Exception.Should().Contain("Zombie occurrence detected");
        stuckResult.EndTime.Should().NotBeNull();

        // Recent occurrence should still be Queued
        recentResult.Status.Should().Be(JobOccurrenceStatus.Queued);
    }

    [Fact]
    public async Task DetectAndCleanupZombieOccurrences_ShouldRespectJobSpecificTimeout()
    {
        // Arrange
        await InitializeAsync();

        // Create a job with a longer timeout
        var longTimeoutJob = await SeedScheduledJobAsync(
            $"LongRunningJob_{Guid.NewGuid():N}",
            timeoutMinutes: 60 // 60 minute timeout
        );

        // Create occurrence that would be zombie with default timeout but NOT with job-specific timeout
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: longTimeoutJob.Id,
            jobName: longTimeoutJob.JobNameInWorker,
            status: JobOccurrenceStatus.Queued,
            createdAt: DateTime.UtcNow.AddMinutes(-15), // 15 minutes old
            timeoutMinutes: 60 // Job-specific 60 minute timeout
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var service = CreateZombieDetectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait a reasonable time for detection cycle
        await Task.Delay(2000, cts.Token);

        await service.StopAsync(cts.Token);

        // Assert - Should NOT be marked as zombie due to job-specific timeout
        var result = await GetOccurrenceAsync(occurrence.Id);
        result.Status.Should().Be(JobOccurrenceStatus.Queued);
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task DetectAndCleanupZombieOccurrences_ShouldUpdateStatusChangeLogs()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"StatusLogTestJob_{Guid.NewGuid():N}");

        var zombieOccurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Queued,
            createdAt: DateTime.UtcNow.AddMinutes(-20)
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var service = CreateZombieDetectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for zombie detection
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(zombieOccurrence.Id);
                return occ?.StatusChangeLogs?.Any(s => s.To == JobOccurrenceStatus.Unknown) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("status change log should be recorded");

        var result = await GetOccurrenceAsync(zombieOccurrence.Id);
        result.StatusChangeLogs.Should().NotBeNull();
        result.StatusChangeLogs.Should().NotBeEmpty();

        var statusChange = result.StatusChangeLogs.First();
        statusChange.From.Should().Be(JobOccurrenceStatus.Queued);
        statusChange.To.Should().Be(JobOccurrenceStatus.Unknown);
    }

    [Fact]
    public async Task DetectAndCleanupZombieOccurrences_ShouldAddLogEntry()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"LogTestJob_{Guid.NewGuid():N}");

        var zombieOccurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Queued,
            createdAt: DateTime.UtcNow.AddMinutes(-15)
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var service = CreateZombieDetectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for zombie detection
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(zombieOccurrence.Id);
                return occ?.Logs?.Any(l => l.Category == "ZombieDetector") == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("zombie log entry should be added");

        var result = await GetOccurrenceAsync(zombieOccurrence.Id);
        result.Logs.Should().NotBeEmpty();

        var zombieLog = result.Logs.FirstOrDefault(l => l.Category == "ZombieDetector");
        zombieLog.Should().NotBeNull();
        zombieLog!.Level.Should().Be("Error");
        zombieLog.Message.Should().Contain("Zombie occurrence detected");
    }

    [Fact]
    public async Task DetectAndCleanupZombieOccurrences_ShouldMarkAffectRunningOccurrences()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"RunningTestJob_{Guid.NewGuid():N}");

        // Create a Running occurrence that is old (but Running, not Queued)
        var runningOccurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running,
            createdAt: DateTime.UtcNow.AddMinutes(-30)
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var service = CreateZombieDetectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait enough time for detection cycles
        await Task.Delay(2000, cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        var result = await GetOccurrenceAsync(runningOccurrence.Id);
        result.Status.Should().Be(JobOccurrenceStatus.Unknown);
    }

    [Fact]
    public async Task DetectAndCleanupZombieOccurrences_ShouldCalculateDurationMs()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"DurationTestJob_{Guid.NewGuid():N}");
        var createdAt = DateTime.UtcNow.AddMinutes(-15);

        var zombieOccurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Queued,
            createdAt: createdAt
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var service = CreateZombieDetectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for zombie detection
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(zombieOccurrence.Id);
                return occ?.DurationMs != null && occ.DurationMs > 0;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("duration should be calculated");

        var result = await GetOccurrenceAsync(zombieOccurrence.Id);
        result.DurationMs.Should().NotBeNull();
        result.DurationMs.Should().BeGreaterThan(0);

        // Duration should be approximately 15 minutes (900000 ms) with some tolerance
        var expectedDurationMs = (DateTime.UtcNow - createdAt).TotalMilliseconds;
        result.DurationMs!.Value.Should().BeInRange((long)(expectedDurationMs - 60000), (long)(expectedDurationMs + 60000));
    }

    private ZombieOccurrenceDetectorService CreateZombieDetectorService() => new(
            _serviceProvider,
            _serviceProvider.GetRequiredService<IRedisSchedulerService>(),
            _serviceProvider.GetRequiredService<IRedisWorkerService>(),
            Options.Create(new ZombieOccurrenceDetectorOptions
            {
                Enabled = true,
                CheckIntervalSeconds = 1, // Fast interval for testing
                ZombieTimeoutMinutes = 10 // Default 10 minute timeout
            }),
            _serviceProvider.GetRequiredService<ILoggerFactory>()
        );
}
