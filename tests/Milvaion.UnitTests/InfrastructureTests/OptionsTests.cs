using FluentAssertions;
using Milvaion.Application.Utils.Models.Options;

namespace Milvaion.UnitTests.InfrastructureTests;

[Trait("Infrastructure Unit Tests", "MemoryTrackingOptions unit tests.")]
public class MemoryTrackingOptionsTests
{
    [Fact]
    public void MemoryTrackingOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new MemoryTrackingOptions();

        // Assert
        options.CheckIntervalSeconds.Should().Be(10);
        options.LogIntervalIterations.Should().Be(50);
        options.WarningThresholdBytes.Should().Be(100 * 1024 * 1024); // 100 MB
        options.CriticalThresholdBytes.Should().Be(500 * 1024 * 1024); // 500 MB
        options.LeakDetectionThresholdBytes.Should().Be(1024 * 1024 * 1024); // 1 GB
        options.LeakDetectionMinIterations.Should().Be(100);
    }

    [Fact]
    public void MemoryTrackingOptions_WarningThreshold_ShouldBe100MB()
    {
        // Arrange
        var options = new MemoryTrackingOptions();
        var expectedBytes = 100L * 1024 * 1024;

        // Assert
        options.WarningThresholdBytes.Should().Be(expectedBytes);
    }

    [Fact]
    public void MemoryTrackingOptions_CriticalThreshold_ShouldBe500MB()
    {
        // Arrange
        var options = new MemoryTrackingOptions();
        var expectedBytes = 500L * 1024 * 1024;

        // Assert
        options.CriticalThresholdBytes.Should().Be(expectedBytes);
    }

    [Fact]
    public void MemoryTrackingOptions_LeakDetectionThreshold_ShouldBe1GB()
    {
        // Arrange
        var options = new MemoryTrackingOptions();
        var expectedBytes = 1024L * 1024 * 1024;

        // Assert
        options.LeakDetectionThresholdBytes.Should().Be(expectedBytes);
    }

    [Fact]
    public void MemoryTrackingOptions_ShouldBeConfigurable()
    {
        // Act
        var options = new MemoryTrackingOptions
        {
            CheckIntervalSeconds = 30,
            LogIntervalIterations = 100,
            WarningThresholdBytes = 200 * 1024 * 1024,
            CriticalThresholdBytes = 1024 * 1024 * 1024,
            LeakDetectionThresholdBytes = 2L * 1024 * 1024 * 1024,
            LeakDetectionMinIterations = 200
        };

        // Assert
        options.CheckIntervalSeconds.Should().Be(30);
        options.LogIntervalIterations.Should().Be(100);
        options.WarningThresholdBytes.Should().Be(200 * 1024 * 1024);
        options.CriticalThresholdBytes.Should().Be(1024 * 1024 * 1024);
        options.LeakDetectionThresholdBytes.Should().Be(2L * 1024 * 1024 * 1024);
        options.LeakDetectionMinIterations.Should().Be(200);
    }
}

[Trait("Infrastructure Unit Tests", "JobDispatcherOptions unit tests.")]
public class JobDispatcherOptionsTests
{
    [Fact]
    public void JobDispatcherOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new JobDispatcherOptions();

        // Assert
        options.Enabled.Should().BeTrue();
        options.PollingIntervalSeconds.Should().Be(10);
        options.BatchSize.Should().Be(100);
        options.LockTtlSeconds.Should().Be(600);
        options.EnableStartupRecovery.Should().BeTrue();
        options.MemoryTrackingOptions.Should().NotBeNull();
    }

    [Fact]
    public void JobDispatcherOptions_SectionKey_ShouldBeCorrect()
        // Assert
        => JobDispatcherOptions.SectionKey.Should().Be("MilvaionConfig:JobDispatcher");

    [Fact]
    public void JobDispatcherOptions_MemoryTrackingOptions_ShouldHaveDefaults()
    {
        // Arrange
        var options = new JobDispatcherOptions();

        // Assert
        options.MemoryTrackingOptions.Should().NotBeNull();
        options.MemoryTrackingOptions.CheckIntervalSeconds.Should().Be(10);
    }

    [Fact]
    public void JobDispatcherOptions_ShouldBeConfigurable()
    {
        // Act
        var options = new JobDispatcherOptions
        {
            Enabled = false,
            PollingIntervalSeconds = 30,
            BatchSize = 50,
            LockTtlSeconds = 300,
            EnableStartupRecovery = false,
        };

        // Assert
        options.Enabled.Should().BeFalse();
        options.PollingIntervalSeconds.Should().Be(30);
        options.BatchSize.Should().Be(50);
        options.LockTtlSeconds.Should().Be(300);
        options.EnableStartupRecovery.Should().BeFalse();
    }
}

[Trait("Infrastructure Unit Tests", "RabbitMQOptions unit tests.")]
public class RabbitMQOptionsTests
{
    [Fact]
    public void RabbitMQOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new RabbitMQOptions();

        // Assert
        options.Host.Should().Be("localhost");
        options.Port.Should().Be(5672);
        options.Username.Should().BeNull();
        options.Password.Should().BeNull();
        options.VirtualHost.Should().Be("/");
        options.Durable.Should().BeTrue();
        options.AutoDelete.Should().BeFalse();
        options.ConnectionTimeout.Should().Be(30);
        options.Heartbeat.Should().Be(60);
        options.AutomaticRecoveryEnabled.Should().BeTrue();
        options.NetworkRecoveryInterval.Should().Be(10);
        options.QueueDepthWarningThreshold.Should().Be(5000);
        options.QueueDepthCriticalThreshold.Should().Be(10000);
    }

    [Fact]
    public void RabbitMQOptions_SectionKey_ShouldBeCorrect()
        // Assert
        => RabbitMQOptions.SectionKey.Should().Be("MilvaionConfig:RabbitMQ");

    [Fact]
    public void RabbitMQOptions_ShouldBeConfigurable()
    {
        // Act
        var options = new RabbitMQOptions
        {
            Host = "rabbitmq.production.com",
            Port = 5673,
            Username = "admin",
            Password = "secret123",
            VirtualHost = "/production",
            Durable = false,
            AutoDelete = true,
            ConnectionTimeout = 60,
            Heartbeat = 30,
            AutomaticRecoveryEnabled = false,
            NetworkRecoveryInterval = 5,
            QueueDepthWarningThreshold = 10000,
            QueueDepthCriticalThreshold = 20000
        };

        // Assert
        options.Host.Should().Be("rabbitmq.production.com");
        options.Port.Should().Be(5673);
        options.Username.Should().Be("admin");
        options.Password.Should().Be("secret123");
        options.VirtualHost.Should().Be("/production");
        options.Durable.Should().BeFalse();
        options.AutoDelete.Should().BeTrue();
        options.ConnectionTimeout.Should().Be(60);
        options.Heartbeat.Should().Be(30);
        options.AutomaticRecoveryEnabled.Should().BeFalse();
        options.NetworkRecoveryInterval.Should().Be(5);
        options.QueueDepthWarningThreshold.Should().Be(10000);
        options.QueueDepthCriticalThreshold.Should().Be(20000);
    }
}

[Trait("Infrastructure Unit Tests", "RedisOptions unit tests.")]
public class RedisOptionsTests
{
    [Fact]
    public void RedisOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new RedisOptions();

        // Assert
        options.ConnectionString.Should().Be("localhost:6379");
        options.Password.Should().BeNull();
        options.Database.Should().Be(0);
        options.ConnectTimeout.Should().Be(5000);
        options.SyncTimeout.Should().Be(5000);
        options.KeyPrefix.Should().Be("Milvaion:JobScheduler:");
        options.DefaultLockTtlSeconds.Should().Be(600);
    }

    [Fact]
    public void RedisOptions_SectionKey_ShouldBeCorrect()
        // Assert
        => RedisOptions.SectionKey.Should().Be("MilvaionConfig:Redis");

    [Fact]
    public void RedisOptions_ScheduledJobsKey_ShouldUseKeyPrefix()
    {
        // Arrange
        var options = new RedisOptions { KeyPrefix = "Test:" };

        // Act & Assert
        options.ScheduledJobsKey.Should().Be("Test:scheduled_jobs");
    }

    [Fact]
    public void RedisOptions_GetLockKey_ShouldFormatCorrectly()
    {
        // Arrange
        var options = new RedisOptions { KeyPrefix = "Milvaion:" };
        var jobId = Guid.Parse("12345678-1234-1234-1234-123456789012");

        // Act
        var lockKey = options.GetLockKey(jobId);

        // Assert
        lockKey.Should().Be("Milvaion:lock:12345678-1234-1234-1234-123456789012");
    }

    [Fact]
    public void RedisOptions_CancellationChannel_ShouldUseKeyPrefix()
    {
        // Arrange
        var options = new RedisOptions { KeyPrefix = "MyApp:" };

        // Act & Assert
        options.CancellationChannel.Should().Be("MyApp:cancellation_channel");
    }

    [Fact]
    public void RedisOptions_ShouldBeConfigurable()
    {
        // Act
        var options = new RedisOptions
        {
            ConnectionString = "redis.production.com:6380",
            Password = "redis-secret",
            Database = 5,
            ConnectTimeout = 10000,
            SyncTimeout = 10000,
            KeyPrefix = "Production:Scheduler:",
            DefaultLockTtlSeconds = 300
        };

        // Assert
        options.ConnectionString.Should().Be("redis.production.com:6380");
        options.Password.Should().Be("redis-secret");
        options.Database.Should().Be(5);
        options.ConnectTimeout.Should().Be(10000);
        options.SyncTimeout.Should().Be(10000);
        options.KeyPrefix.Should().Be("Production:Scheduler:");
        options.DefaultLockTtlSeconds.Should().Be(300);
    }

    [Fact]
    public void RedisOptions_DefaultKeyPrefix_ShouldGenerateCorrectKeys()
    {
        // Arrange
        var options = new RedisOptions();

        // Assert
        options.ScheduledJobsKey.Should().Be("Milvaion:JobScheduler:scheduled_jobs");
        options.CancellationChannel.Should().Be("Milvaion:JobScheduler:cancellation_channel");
    }
}

[Trait("Infrastructure Unit Tests", "StatusTrackerOptions unit tests.")]
public class StatusTrackerOptionsTests
{
    [Fact]
    public void StatusTrackerOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new StatusTrackerOptions();

        // Assert
        options.Enabled.Should().BeTrue();
        options.BatchSize.Should().Be(50);
        options.BatchIntervalMs.Should().Be(500);
    }

    [Fact]
    public void StatusTrackerOptions_SectionKey_ShouldBeCorrect()
        // Assert
        => StatusTrackerOptions.SectionKey.Should().Be("MilvaionConfig:StatusTracker");

    [Fact]
    public void StatusTrackerOptions_ShouldBeConfigurable()
    {
        // Act
        var options = new StatusTrackerOptions
        {
            Enabled = false,
            BatchSize = 100,
            BatchIntervalMs = 1000
        };

        // Assert
        options.Enabled.Should().BeFalse();
        options.BatchSize.Should().Be(100);
        options.BatchIntervalMs.Should().Be(1000);
    }
}

[Trait("Infrastructure Unit Tests", "LogCollectorOptions unit tests.")]
public class LogCollectorOptionsTests
{
    [Fact]
    public void LogCollectorOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new LogCollectorOptions();

        // Assert
        options.Enabled.Should().BeTrue();
        options.BatchSize.Should().Be(100);
        options.BatchIntervalMs.Should().Be(1000);
    }

    [Fact]
    public void LogCollectorOptions_SectionKey_ShouldBeCorrect()
        // Assert
        => LogCollectorOptions.SectionKey.Should().Be("MilvaionConfig:LogCollector");

    [Fact]
    public void LogCollectorOptions_ShouldBeConfigurable()
    {
        // Act
        var options = new LogCollectorOptions
        {
            Enabled = false,
            BatchSize = 200,
            BatchIntervalMs = 500
        };

        // Assert
        options.Enabled.Should().BeFalse();
        options.BatchSize.Should().Be(200);
        options.BatchIntervalMs.Should().Be(500);
    }
}

[Trait("Infrastructure Unit Tests", "ZombieOccurrenceDetectorOptions unit tests.")]
public class ZombieOccurrenceDetectorOptionsTests
{
    [Fact]
    public void ZombieOccurrenceDetectorOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new ZombieOccurrenceDetectorOptions();

        // Assert
        options.Enabled.Should().BeTrue();
        options.CheckIntervalSeconds.Should().Be(300); // 5 minutes
        options.ZombieTimeoutMinutes.Should().Be(10);
    }

    [Fact]
    public void ZombieOccurrenceDetectorOptions_SectionKey_ShouldBeCorrect()
        // Assert
        => ZombieOccurrenceDetectorOptions.SectionKey.Should().Be("MilvaionConfig:ZombieOccurrenceDetector");

    [Fact]
    public void ZombieOccurrenceDetectorOptions_ShouldBeConfigurable()
    {
        // Act
        var options = new ZombieOccurrenceDetectorOptions
        {
            Enabled = false,
            CheckIntervalSeconds = 600,
            ZombieTimeoutMinutes = 15
        };

        // Assert
        options.Enabled.Should().BeFalse();
        options.CheckIntervalSeconds.Should().Be(600);
        options.ZombieTimeoutMinutes.Should().Be(15);
    }
}

[Trait("Infrastructure Unit Tests", "WorkerAutoDiscoveryOptions unit tests.")]
public class WorkerAutoDiscoveryOptionsTests
{
    [Fact]
    public void WorkerAutoDiscoveryOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new WorkerAutoDiscoveryOptions();

        // Assert
        options.Enabled.Should().BeTrue();
    }

    [Fact]
    public void WorkerAutoDiscoveryOptions_SectionKey_ShouldBeCorrect()
        // Assert
        => WorkerAutoDiscoveryOptions.SectionKey.Should().Be("MilvaionConfig:WorkerAutoDiscovery");

    [Fact]
    public void WorkerAutoDiscoveryOptions_ShouldBeConfigurable()
    {
        // Act
        var options = new WorkerAutoDiscoveryOptions
        {
            Enabled = false
        };

        // Assert
        options.Enabled.Should().BeFalse();
    }
}

[Trait("Infrastructure Unit Tests", "JobAutoDisableOptions unit tests.")]
public class JobAutoDisableOptionsTests
{
    [Fact]
    public void JobAutoDisableOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new JobAutoDisableOptions();

        // Assert
        options.Enabled.Should().BeTrue();
        options.ConsecutiveFailureThreshold.Should().Be(5);
        options.FailureWindowMinutes.Should().Be(60);
    }

    [Fact]
    public void JobAutoDisableOptions_SectionKey_ShouldBeCorrect()
        // Assert
        => JobAutoDisableOptions.SectionKey.Should().Be("MilvaionConfig:JobAutoDisable");

    [Fact]
    public void JobAutoDisableOptions_ShouldBeConfigurable()
    {
        // Act
        var options = new JobAutoDisableOptions
        {
            Enabled = false,
            ConsecutiveFailureThreshold = 10,
            FailureWindowMinutes = 120,
        };

        // Assert
        options.Enabled.Should().BeFalse();
        options.ConsecutiveFailureThreshold.Should().Be(10);
        options.FailureWindowMinutes.Should().Be(120);
    }
}

[Trait("Infrastructure Unit Tests", "FailedOccurrenceHandlerOptions unit tests.")]
public class FailedOccurrenceHandlerOptionsTests
{
    [Fact]
    public void FailedOccurrenceHandlerOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new FailedOccurrenceHandlerOptions();

        // Assert
        options.Enabled.Should().BeTrue();
    }

    [Fact]
    public void FailedOccurrenceHandlerOptions_SectionKey_ShouldBeCorrect()
        // Assert
        => FailedOccurrenceHandlerOptions.SectionKey.Should().Be("MilvaionConfig:FailedOccurrenceHandler");

    [Fact]
    public void FailedOccurrenceHandlerOptions_ShouldBeConfigurable()
    {
        // Act
        var options = new FailedOccurrenceHandlerOptions
        {
            Enabled = false
        };

        // Assert
        options.Enabled.Should().BeFalse();
    }
}
