using FluentAssertions;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Models;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.Services;

/// <summary>
/// Integration tests for RedisWorkerService.
/// Tests worker registration, heartbeat, and retrieval operations against real Redis.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class RedisWorkerServiceTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : RedisServiceTestBase(factory, output)
{
    [Fact]
    public async Task RegisterWorkerAsync_ShouldRegisterNewWorker()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        var registration = CreateTestRegistration("test-worker-01", "instance-01");

        // Act
        var result = await workerService.RegisterWorkerAsync(registration);

        // Assert
        result.Should().BeTrue();

        var workers = await workerService.GetAllWorkersAsync();
        workers.Should().Contain(w => w.WorkerId == "test-worker-01");
    }

    [Fact]
    public async Task RegisterWorkerAsync_ShouldUpdateExistingWorker()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        var registration = CreateTestRegistration("test-worker-01", "instance-01");

        await workerService.RegisterWorkerAsync(registration);

        // Act - Register again with updated info
        registration.DisplayName = "Updated Worker";
        var result = await workerService.RegisterWorkerAsync(registration);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateHeartbeatAsync_ShouldUpdateHeartbeat_WhenInstanceExists()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        var registration = CreateTestRegistration("heartbeat-worker", "instance-01");

        await workerService.RegisterWorkerAsync(registration);

        // Act
        var result = await workerService.UpdateHeartbeatAsync("heartbeat-worker", "instance-01", currentJobs: 3);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateHeartbeatAsync_ShouldReturnFalse_WhenInstanceDoesNotExist()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        // Act - No registration, try to heartbeat
        var result = await workerService.UpdateHeartbeatAsync("nonexistent-worker", "nonexistent-instance", currentJobs: 0);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllWorkersAsync_ShouldReturnAllRegisteredWorkers()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        await workerService.RegisterWorkerAsync(CreateTestRegistration("worker-01", "instance-01"));
        await workerService.RegisterWorkerAsync(CreateTestRegistration("worker-02", "instance-01"));

        // Act
        var workers = await workerService.GetAllWorkersAsync();

        // Assert
        workers.Should().HaveCountGreaterThanOrEqualTo(2);
        workers.Should().Contain(w => w.WorkerId == "worker-01");
        workers.Should().Contain(w => w.WorkerId == "worker-02");
    }

    [Fact]
    public async Task GetAllWorkersAsync_ShouldReturnEmptyList_WhenNoWorkersRegistered()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        // Act
        var workers = await workerService.GetAllWorkersAsync();

        // Assert
        workers.Should().BeEmpty();
    }

    [Fact]
    public async Task RegisterWorkerAsync_ShouldSupportMultipleInstances()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        await workerService.RegisterWorkerAsync(CreateTestRegistration("multi-instance-worker", "instance-01"));
        await workerService.RegisterWorkerAsync(CreateTestRegistration("multi-instance-worker", "instance-02"));

        // Act
        var workers = await workerService.GetAllWorkersAsync();

        // Assert
        var worker = workers.FirstOrDefault(w => w.WorkerId == "multi-instance-worker");
        worker.Should().NotBeNull();
        worker.Instances.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    #region BulkUpdateHeartbeatsAsync

    [Fact]
    public async Task BulkUpdateHeartbeatsAsync_ShouldUpdateMultipleInstances()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        await workerService.RegisterWorkerAsync(CreateTestRegistration("bulk-worker-01", "instance-01"));
        await workerService.RegisterWorkerAsync(CreateTestRegistration("bulk-worker-02", "instance-01"));

        var updates = new List<(string WorkerId, string InstanceId, int CurrentJobs, long MemoryBytes, double CpuUsagePercent, DateTime Timestamp)>
        {
            ("bulk-worker-01", "instance-01", 3, 0, 0, DateTime.UtcNow),
            ("bulk-worker-02", "instance-01", 5, 0, 0, DateTime.UtcNow)
        };

        // Act
        var result = await workerService.BulkUpdateHeartbeatsAsync(updates);

        // Assert
        result.Should().Be(2);

        var worker1 = await workerService.GetWorkerAsync("bulk-worker-01");
        worker1.Should().NotBeNull();
        worker1!.CurrentJobs.Should().Be(3);

        var worker2 = await workerService.GetWorkerAsync("bulk-worker-02");
        worker2.Should().NotBeNull();
        worker2!.CurrentJobs.Should().Be(5);
    }

    [Fact]
    public async Task BulkUpdateHeartbeatsAsync_ShouldReturnZero_WhenListIsEmpty()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        // Act
        var result = await workerService.BulkUpdateHeartbeatsAsync([]);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public async Task BulkUpdateHeartbeatsAsync_ShouldReturnZero_WhenListIsNull()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        // Act
        var result = await workerService.BulkUpdateHeartbeatsAsync(null);

        // Assert
        result.Should().Be(0);
    }

    #endregion

    #region IsWorkerActiveAsync

    [Fact]
    public async Task IsWorkerActiveAsync_ShouldReturnTrue_WhenWorkerIsActive()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("active-worker", "instance-01"));

        // Act
        var result = await workerService.IsWorkerActiveAsync("active-worker");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsWorkerActiveAsync_ShouldReturnFalse_WhenWorkerDoesNotExist()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        // Act
        var result = await workerService.IsWorkerActiveAsync("nonexistent-worker");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region GetWorkerCapacityAsync

    [Fact]
    public async Task GetWorkerCapacityAsync_ShouldReturnCapacity_WhenWorkerExists()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("capacity-worker", "instance-01"));

        // Update heartbeat with current jobs
        await workerService.UpdateHeartbeatAsync("capacity-worker", "instance-01", currentJobs: 3);

        // Act
        var (currentJobs, maxParallelJobs) = await workerService.GetWorkerCapacityAsync("capacity-worker");

        // Assert
        currentJobs.Should().Be(3);
        maxParallelJobs.Should().Be(5);
    }

    [Fact]
    public async Task GetWorkerCapacityAsync_ShouldReturnZeroAndNull_WhenWorkerDoesNotExist()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        // Act
        var (currentJobs, maxParallelJobs) = await workerService.GetWorkerCapacityAsync("nonexistent-worker");

        // Assert
        currentJobs.Should().Be(0);
        maxParallelJobs.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkerCapacityAsync_ShouldAggregateAcrossInstances()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("multi-cap-worker", "instance-01"));
        await workerService.RegisterWorkerAsync(CreateTestRegistration("multi-cap-worker", "instance-02"));

        await workerService.UpdateHeartbeatAsync("multi-cap-worker", "instance-01", currentJobs: 2);
        await workerService.UpdateHeartbeatAsync("multi-cap-worker", "instance-02", currentJobs: 3);

        // Act
        var (currentJobs, maxParallelJobs) = await workerService.GetWorkerCapacityAsync("multi-cap-worker");

        // Assert
        currentJobs.Should().Be(5);
        maxParallelJobs.Should().Be(5);
    }

    #endregion

    #region GetConsumerCapacityAsync

    [Fact]
    public async Task GetConsumerCapacityAsync_ShouldReturnConsumerCapacity_WhenMetadataHasJobConfigs()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        var metadata = JsonSerializer.Serialize(new
        {
            JobConfigs = new[]
            {
                new { JobType = "EmailJob", ConsumerId = "email-consumer", MaxParallelJobs = 3, ExecutionTimeoutSeconds = 60 }
            }
        });

        var registration = CreateTestRegistration("consumer-worker", "instance-01");
        registration.Metadata = metadata;
        await workerService.RegisterWorkerAsync(registration);

        // Increment consumer job count
        await workerService.IncrementConsumerJobCountAsync("consumer-worker", "EmailJob");

        // Act
        var (currentJobs, maxParallelJobs) = await workerService.GetConsumerCapacityAsync("consumer-worker", "EmailJob");

        // Assert
        currentJobs.Should().Be(1);
        maxParallelJobs.Should().Be(3);
    }

    [Fact]
    public async Task GetConsumerCapacityAsync_ShouldReturnNullMax_WhenJobTypeNotInConfig()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("nocfg-worker", "instance-01"));

        // Act
        var (currentJobs, maxParallelJobs) = await workerService.GetConsumerCapacityAsync("nocfg-worker", "UnknownJob");

        // Assert
        currentJobs.Should().Be(0);
        maxParallelJobs.Should().BeNull();
    }

    #endregion

    #region IncrementConsumerJobCountAsync / DecrementConsumerJobCountAsync

    [Fact]
    public async Task IncrementConsumerJobCountAsync_ShouldIncrementCount()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("inc-worker", "instance-01"));

        // Act
        await workerService.IncrementConsumerJobCountAsync("inc-worker", "TestJob");
        await workerService.IncrementConsumerJobCountAsync("inc-worker", "TestJob");

        // Assert
        var (currentJobs, _) = await workerService.GetConsumerCapacityAsync("inc-worker", "TestJob");
        currentJobs.Should().Be(2);
    }

    [Fact]
    public async Task DecrementConsumerJobCountAsync_ShouldDecrementCount()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("dec-worker", "instance-01"));

        await workerService.IncrementConsumerJobCountAsync("dec-worker", "TestJob");
        await workerService.IncrementConsumerJobCountAsync("dec-worker", "TestJob");
        await workerService.IncrementConsumerJobCountAsync("dec-worker", "TestJob");

        // Act
        await workerService.DecrementConsumerJobCountAsync("dec-worker", "TestJob");

        // Assert
        var (currentJobs, _) = await workerService.GetConsumerCapacityAsync("dec-worker", "TestJob");
        currentJobs.Should().Be(2);
    }

    [Fact]
    public async Task DecrementConsumerJobCountAsync_ShouldNotGoBelowZero()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("floor-worker", "instance-01"));

        // Act - Decrement without any prior increment
        await workerService.DecrementConsumerJobCountAsync("floor-worker", "TestJob");

        // Assert
        var (currentJobs, _) = await workerService.GetConsumerCapacityAsync("floor-worker", "TestJob");
        currentJobs.Should().Be(0);
    }

    #endregion

    #region BatchUpdateConsumerJobCountsAsync

    [Fact]
    public async Task BatchUpdateConsumerJobCountsAsync_ShouldUpdateMultipleConsumers()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("batch-worker", "instance-01"));

        var db = GetRedisDatabase();

        // Pre-seed some initial counts
        await db.HashSetAsync("workers:batch-worker:instances:instance-01:job_counts", "EmailJob", 2);
        await db.HashSetAsync("workers:batch-worker:instances:instance-01:job_counts", "SmsJob", 1);

        var updates = new Dictionary<string, int>
        {
            ["batch-worker:instance-01:EmailJob"] = 1,  // +1 -> should become 3
            ["batch-worker:instance-01:SmsJob"] = -1     // -1 -> should become 0
        };

        // Act
        await workerService.BatchUpdateConsumerJobCountsAsync(updates);

        // Assert
        var emailCount = await db.HashGetAsync("workers:batch-worker:instances:instance-01:job_counts", "EmailJob");
        emailCount.ToString().Should().Be("3");

        var smsCount = await db.HashGetAsync("workers:batch-worker:instances:instance-01:job_counts", "SmsJob");
        smsCount.ToString().Should().Be("0");
    }

    [Fact]
    public async Task BatchUpdateConsumerJobCountsAsync_ShouldHandleEmptyUpdates()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        // Act & Assert - should not throw
        await workerService.BatchUpdateConsumerJobCountsAsync([]);
    }

    [Fact]
    public async Task BatchUpdateConsumerJobCountsAsync_ShouldFloorAtZero()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("floor-batch-worker", "instance-01"));

        var db = GetRedisDatabase();
        await db.HashSetAsync("workers:floor-batch-worker:instances:instance-01:job_counts", "TestJob", 1);

        var updates = new Dictionary<string, int>
        {
            ["floor-batch-worker:instance-01:TestJob"] = -5  // -5 from 1 -> should floor at 0
        };

        // Act
        await workerService.BatchUpdateConsumerJobCountsAsync(updates);

        // Assert
        var count = await db.HashGetAsync("workers:floor-batch-worker:instances:instance-01:job_counts", "TestJob");
        count.ToString().Should().Be("0");
    }

    #endregion

    #region RemoveWorkerAsync

    [Fact]
    public async Task RemoveWorkerAsync_ShouldRemoveWorkerAndAllInstances()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("remove-worker", "instance-01"));
        await workerService.RegisterWorkerAsync(CreateTestRegistration("remove-worker", "instance-02"));

        var workerBefore = await workerService.GetWorkerAsync("remove-worker");
        workerBefore.Should().NotBeNull();

        // Act
        var result = await workerService.RemoveWorkerAsync("remove-worker");

        // Assert
        result.Should().BeTrue();

        var workerAfter = await workerService.GetWorkerAsync("remove-worker");
        workerAfter.Should().BeNull();

        var allWorkers = await workerService.GetAllWorkersAsync();
        allWorkers.Should().NotContain(w => w.WorkerId == "remove-worker");
    }

    [Fact]
    public async Task RemoveWorkerAsync_ShouldReturnTrue_WhenWorkerDoesNotExist()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        // Act
        var result = await workerService.RemoveWorkerAsync("nonexistent-worker");

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region RemoveWorkerInstanceAsync

    [Fact]
    public async Task RemoveWorkerInstanceAsync_ShouldRemoveSpecificInstance()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("inst-remove-worker", "instance-01"));
        await workerService.RegisterWorkerAsync(CreateTestRegistration("inst-remove-worker", "instance-02"));

        // Act
        var result = await workerService.RemoveWorkerInstanceAsync("inst-remove-worker", "instance-01");

        // Assert
        result.Should().BeTrue();

        var worker = await workerService.GetWorkerAsync("inst-remove-worker");
        worker.Should().NotBeNull();
        worker!.Instances.Should().HaveCount(1);
        worker.Instances.Should().Contain(i => i.InstanceId == "instance-02");
        worker.Instances.Should().NotContain(i => i.InstanceId == "instance-01");
    }

    [Fact]
    public async Task RemoveWorkerInstanceAsync_ShouldReturnTrue_WhenInstanceDoesNotExist()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();

        // Act
        var result = await workerService.RemoveWorkerInstanceAsync("nonexistent-worker", "nonexistent-instance");

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region DetectZombieWorkersAsync

    [Fact]
    public async Task DetectZombieWorkersAsync_ShouldReturnEmptyList()
    {
        // Arrange
        await InitializeAsync();
        await FlushRedisAsync();

        var workerService = GetRedisWorkerService();
        await workerService.RegisterWorkerAsync(CreateTestRegistration("zombie-test-worker", "instance-01"));

        // Act
        var result = await workerService.DetectZombieWorkersAsync(TimeSpan.FromMinutes(1));

        // Assert
        result.Should().BeEmpty("TTL handles zombie detection automatically");
    }

    #endregion

    private static WorkerDiscoveryRequest CreateTestRegistration(string workerId, string instanceId) => new()
    {
        WorkerId = workerId,
        InstanceId = instanceId,
        DisplayName = $"Test {workerId}",
        HostName = "test-host",
        IpAddress = "127.0.0.1",
        Version = "1.0.0",
        MaxParallelJobs = 5,
        JobTypes = ["TestJob"],
        RoutingPatterns = new Dictionary<string, string> { [$"worker.{workerId}"] = $"worker.{workerId}" },
        Metadata = "{}",
        JobDataDefinitions = []
    };
}
