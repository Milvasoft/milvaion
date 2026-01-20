using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Worker.Options;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "WorkerOptions unit tests.")]
public class WorkerOptionsTests
{
    [Fact]
    public void WorkerOptions_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new WorkerOptions();

        // Assert
        options.WorkerId.Should().Be(Environment.MachineName);
        options.MaxParallelJobs.Should().Be(Environment.ProcessorCount * 2);
        options.ExecutionTimeoutSeconds.Should().Be(3600);
        options.RabbitMQ.Should().NotBeNull();
        options.Redis.Should().NotBeNull();
        options.Heartbeat.Should().NotBeNull();
        options.OfflineResilience.Should().NotBeNull();
    }

    [Fact]
    public void RegenerateInstanceId_ShouldGenerateUniqueInstanceId()
    {
        // Arrange
        var options = new WorkerOptions { WorkerId = "test-worker" };

        // Act
        options.RegenerateInstanceId();

        // Assert
        options.InstanceId.Should().NotBeNullOrEmpty();
        options.InstanceId.Should().StartWith("test-worker-");
        options.InstanceId.Length.Should().BeGreaterThan("test-worker-".Length);
    }

    [Fact]
    public void RegenerateInstanceId_ShouldGenerateDifferentIds_ForDifferentWorkerIds()
    {
        // Arrange - In real usage, different workers have different WorkerIds
        var options1 = new WorkerOptions { WorkerId = "worker-alpha" };
        var options2 = new WorkerOptions { WorkerId = "worker-beta" };

        // Act
        options1.RegenerateInstanceId();
        options2.RegenerateInstanceId();

        // Assert - Different WorkerIds produce different prefixes
        options1.InstanceId.Should().StartWith("worker-alpha-");
        options2.InstanceId.Should().StartWith("worker-beta-");
        options1.InstanceId.Should().NotBe(options2.InstanceId);
    }

    [Fact]
    public void SetInstanceId_ShouldSetInstanceIdExplicitly()
    {
        // Arrange
        var options = new WorkerOptions();
        var customInstanceId = "custom-instance-id";

        // Act
        options.SetInstanceId(customInstanceId);

        // Assert
        options.InstanceId.Should().Be(customInstanceId);
    }

    [Fact]
    public void WorkerId_ShouldBeConfigurable()
    {
        // Act
        var options = new WorkerOptions { WorkerId = "custom-worker" };

        // Assert
        options.WorkerId.Should().Be("custom-worker");
    }

    [Fact]
    public void MaxParallelJobs_ShouldBeConfigurable()
    {
        // Act
        var options = new WorkerOptions { MaxParallelJobs = 10 };

        // Assert
        options.MaxParallelJobs.Should().Be(10);
    }

    [Fact]
    public void ExecutionTimeoutSeconds_ShouldBeConfigurable()
    {
        // Act
        var options = new WorkerOptions { ExecutionTimeoutSeconds = 7200 };

        // Assert
        options.ExecutionTimeoutSeconds.Should().Be(7200);
    }

    [Fact]
    public void SectionKey_ShouldBeWorker()
        // Assert
        => WorkerOptions.SectionKey.Should().Be("Worker");
}

[Trait("SDK Unit Tests", "JobConsumerConfig unit tests.")]
public class JobConsumerConfigTests
{
    [Fact]
    public void JobConsumerConfig_ShouldInitializeWithDefaultValues()
    {
        // Act
        var config = new JobConsumerConfig();

        // Assert
        config.ConsumerId.Should().BeNull();
        config.RoutingPattern.Should().BeNull();
        config.MaxParallelJobs.Should().Be(10);
        config.ExecutionTimeoutSeconds.Should().Be(3600);
        config.MaxRetries.Should().Be(3);
        config.BaseRetryDelaySeconds.Should().Be(5);
        config.LogUserFriendlyLogsViaLogger.Should().BeFalse();
    }

    [Fact]
    public void JobConsumerConfig_ShouldBeConfigurable()
    {
        // Act
        var config = new JobConsumerConfig
        {
            ConsumerId = "test-consumer",
            RoutingPattern = "jobs.test.*",
            MaxParallelJobs = 20,
            ExecutionTimeoutSeconds = 1800,
            MaxRetries = 5,
            BaseRetryDelaySeconds = 10,
            LogUserFriendlyLogsViaLogger = true
        };

        // Assert
        config.ConsumerId.Should().Be("test-consumer");
        config.RoutingPattern.Should().Be("jobs.test.*");
        config.MaxParallelJobs.Should().Be(20);
        config.ExecutionTimeoutSeconds.Should().Be(1800);
        config.MaxRetries.Should().Be(5);
        config.BaseRetryDelaySeconds.Should().Be(10);
        config.LogUserFriendlyLogsViaLogger.Should().BeTrue();
    }

    [Fact]
    public void JobConsumerOptions_ShouldBeDictionary()
    {
        // Act
        var options = new JobConsumerOptions
        {
            ["TestJob"] = new JobConsumerConfig { ExecutionTimeoutSeconds = 120 },
            ["EmailJob"] = new JobConsumerConfig { MaxRetries = 5 }
        };

        // Assert
        options.Should().HaveCount(2);
        options["TestJob"].ExecutionTimeoutSeconds.Should().Be(120);
        options["EmailJob"].MaxRetries.Should().Be(5);
    }

    [Fact]
    public void JobConsumerOptions_SectionKey_ShouldBeJobConsumers()
        // Assert
        => JobConsumerOptions.SectionKey.Should().Be("JobConsumers");
}

[Trait("SDK Unit Tests", "RabbitMQSettings unit tests.")]
public class RabbitMQSettingsTests
{
    [Fact]
    public void RabbitMQSettings_ShouldInitializeWithDefaultValues()
    {
        // Act
        var settings = new RabbitMQSettings();

        // Assert
        settings.Host.Should().Be("localhost");
        settings.Port.Should().Be(5672);
        settings.Username.Should().Be("guest");
        settings.Password.Should().Be("guest");
        settings.VirtualHost.Should().Be("/");
        settings.RoutingKeyPattern.Should().BeNull(); // No default routing pattern
    }

    [Fact]
    public void RabbitMQSettings_ShouldBeConfigurable()
    {
        // Act
        var settings = new RabbitMQSettings
        {
            Host = "rabbitmq.example.com",
            Port = 5673,
            Username = "admin",
            Password = "secret",
            VirtualHost = "/production",
            RoutingKeyPattern = "jobs.email.*"
        };

        // Assert
        settings.Host.Should().Be("rabbitmq.example.com");
        settings.Port.Should().Be(5673);
        settings.Username.Should().Be("admin");
        settings.Password.Should().Be("secret");
        settings.VirtualHost.Should().Be("/production");
        settings.RoutingKeyPattern.Should().Be("jobs.email.*");
    }
}

[Trait("SDK Unit Tests", "RedisSettings unit tests.")]
public class RedisSettingsTests
{
    [Fact]
    public void RedisSettings_ShouldInitializeWithDefaultValues()
    {
        // Act
        var settings = new RedisSettings();

        // Assert
        settings.ConnectionString.Should().Be("localhost:6379");
    }

    [Fact]
    public void RedisSettings_ShouldBeConfigurable()
    {
        // Act
        var settings = new RedisSettings
        {
            ConnectionString = "redis.example.com:6379,password=secret"
        };

        // Assert
        settings.ConnectionString.Should().Be("redis.example.com:6379,password=secret");
    }
}

[Trait("SDK Unit Tests", "HeartbeatSettings unit tests.")]
public class HeartbeatSettingsTests
{
    [Fact]
    public void HeartbeatSettings_ShouldInitializeWithDefaultValues()
    {
        // Act
        var settings = new HeartbeatSettings();

        // Assert
        settings.IntervalSeconds.Should().Be(30);
        settings.Enabled.Should().BeTrue();
    }

    [Fact]
    public void HeartbeatSettings_ShouldBeConfigurable()
    {
        // Act
        var settings = new HeartbeatSettings
        {
            IntervalSeconds = 15,
            Enabled = false
        };

        // Assert
        settings.IntervalSeconds.Should().Be(15);
        settings.Enabled.Should().BeFalse();
    }
}

[Trait("SDK Unit Tests", "OfflineResilienceSettings unit tests.")]
public class OfflineResilienceSettingsTests
{
    [Fact]
    public void OfflineResilienceSettings_ShouldInitializeWithDefaultValues()
    {
        // Act
        var settings = new OfflineResilienceSettings();

        // Assert
        settings.Enabled.Should().BeTrue();
        settings.LocalStoragePath.Should().Be("./worker_data");
        settings.SyncIntervalSeconds.Should().Be(30);
        settings.MaxSyncRetries.Should().Be(3);
        settings.CleanupIntervalHours.Should().Be(1);
        settings.RecordRetentionDays.Should().Be(1);
    }

    [Fact]
    public void OfflineResilienceSettings_ShouldBeConfigurable()
    {
        // Act
        var settings = new OfflineResilienceSettings
        {
            Enabled = false,
            LocalStoragePath = "/var/lib/worker/data",
            SyncIntervalSeconds = 10,
            MaxSyncRetries = 5,
            CleanupIntervalHours = 12,
            RecordRetentionDays = 14
        };

        // Assert
        settings.Enabled.Should().BeFalse();
        settings.LocalStoragePath.Should().Be("/var/lib/worker/data");
        settings.SyncIntervalSeconds.Should().Be(10);
        settings.MaxSyncRetries.Should().Be(5);
        settings.CleanupIntervalHours.Should().Be(12);
        settings.RecordRetentionDays.Should().Be(14);
    }
}
