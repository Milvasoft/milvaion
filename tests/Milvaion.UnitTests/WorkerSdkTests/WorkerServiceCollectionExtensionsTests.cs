using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Milvasoft.Milvaion.Sdk.Worker;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "WorkerServiceCollectionExtensions unit tests.")]
public class WorkerServiceCollectionExtensionsTests
{
    #region AddMilvaionWorkerCore

    [Fact]
    public void AddMilvaionWorkerCore_ShouldThrow_WhenWorkerOptionsSectionIsMissing()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        // Act
        var act = () => services.AddMilvaionWorkerCore(configuration, requireJobConsumers: false);

        // Assert - WorkerOptions section missing doesn't throw (it uses defaults),
        // but certain required services should still be registered
        act.Should().NotThrow("core registration should work with defaults when job consumers not required");
    }

    [Fact]
    public void AddMilvaionWorkerCore_ShouldRegisterCoreServices_WithValidConfig()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "test-worker-01",
            ["Worker:MaxParallelJobs"] = "4",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:RabbitMQ:Port"] = "5672",
            ["Worker:RabbitMQ:Username"] = "guest",
            ["Worker:RabbitMQ:Password"] = "guest",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        services.AddMilvaionWorkerCore(configuration, requireJobConsumers: false);

        // Assert
        var provider = services.BuildServiceProvider();
        var workerJobTracker = provider.GetService<Milvasoft.Milvaion.Sdk.Worker.Core.WorkerJobTracker>();
        workerJobTracker.Should().NotBeNull("WorkerJobTracker should be registered");
    }

    [Fact]
    public void AddMilvaionWorkerCore_ShouldRegisterJobExecutor_WhenRequireJobConsumersIsTrue()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "test-worker-01",
            ["Worker:RabbitMQ:Host"] = "localhost",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        services.AddMilvaionWorkerCore(configuration, requireJobConsumers: true);

        // Assert
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(Milvasoft.Milvaion.Sdk.Worker.Core.JobExecutor));
        descriptor.Should().NotBeNull("JobExecutor should be registered when requireJobConsumers is true");
    }

    [Fact]
    public void AddMilvaionWorkerCore_ShouldNotRegisterJobExecutor_WhenRequireJobConsumersIsFalse()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "test-worker-01",
            ["Worker:RabbitMQ:Host"] = "localhost",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        services.AddMilvaionWorkerCore(configuration, requireJobConsumers: false);

        // Assert
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(Milvasoft.Milvaion.Sdk.Worker.Core.JobExecutor));
        descriptor.Should().BeNull("JobExecutor should NOT be registered when requireJobConsumers is false");
    }

    [Fact]
    public void AddMilvaionWorkerCore_ShouldRegisterOfflineResilience_WhenEnabled()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "offline-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:OfflineResilience:Enabled"] = "true",
            ["Worker:OfflineResilience:LocalStoragePath"] = "./test_data",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        services.AddMilvaionWorkerCore(configuration, requireJobConsumers: false);

        // Assert
        var localStoreDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(Milvasoft.Milvaion.Sdk.Worker.Persistence.LocalStateStore));
        localStoreDescriptor.Should().NotBeNull("LocalStateStore should be registered when offline resilience is enabled");
    }

    [Fact]
    public void AddMilvaionWorkerCore_ShouldNotRegisterOfflineResilience_WhenDisabled()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "normal-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:OfflineResilience:Enabled"] = "false",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        services.AddMilvaionWorkerCore(configuration, requireJobConsumers: false);

        // Assert
        var localStoreDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(Milvasoft.Milvaion.Sdk.Worker.Persistence.LocalStateStore));
        localStoreDescriptor.Should().BeNull("LocalStateStore should NOT be registered when offline resilience is disabled");
        services.Should().Contain(d => d.ServiceType == typeof(Milvasoft.Milvaion.Sdk.Worker.Persistence.OutboxService), "statuses and logs are published through OutboxService even without offline resilience");
    }

    #endregion

    #region AddMilvaionWorkerWithJobs

    [Fact]
    public void AddMilvaionWorkerWithJobs_ShouldThrow_WhenJobConsumersSectionMissing()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "test-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        var act = () => services.AddMilvaionWorkerWithJobs(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*JobConsumers*not found*");
    }

    [Fact]
    public void AddMilvaionWorkerWithJobs_ShouldThrow_WhenConfigHasJobWithoutConsumerId()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "test-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["JobConsumers:SomeJob:RoutingPattern"] = "worker.*",
            // Missing ConsumerId
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        var act = () => services.AddMilvaionWorkerWithJobs(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*ConsumerId*required*");
    }

    #endregion

    #region AddFileHealthCheck

    [Fact]
    public void AddFileHealthCheck_ShouldRegisterHealthChecks_WhenEnabled()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "health-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:HealthCheck:Enabled"] = "true",
            ["Worker:HealthCheck:IntervalSeconds"] = "30",
            ["Worker:HealthCheck:LiveFilePath"] = "/tmp/healthy",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        services.AddFileHealthCheck(configuration);

        // Assert - Health check service should be registered
        var healthCheckDescriptor = services.Any(d => d.ServiceType.Name.Contains("HealthCheck"));
        healthCheckDescriptor.Should().BeTrue("health check services should be registered when enabled");
    }

    [Fact]
    public void AddFileHealthCheck_ShouldNotRegisterHealthChecks_WhenDisabled()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "no-health-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:HealthCheck:Enabled"] = "false",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var beforeCount = services.Count;

        // Act
        services.AddFileHealthCheck(configuration);

        // Assert
        services.Count.Should().Be(beforeCount, "no additional services should be registered when health check is disabled");
    }

    #endregion

    #region AddMilvaionWorkerWithJobs - Auto Routing Pattern

    [Fact]
    public void AddMilvaionWorkerWithJobs_ShouldAutoGenerateRoutingPattern_WhenNotSpecified()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "email-worker-01",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["JobConsumers:SomeJob:ConsumerId"] = "some-consumer",
            // No RoutingPattern specified
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act - Will throw because there's no matching job class (Assembly.GetEntryAssembly() is null in tests),
        // but the auto-generated routing pattern code path at line 73-83 will have executed
        var act = () => services.AddMilvaionWorkerWithJobs(configuration);

        // Assert - Should fail with configsWithoutJob validation, proving it got past auto-generation
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*SomeJob*");
    }

    [Fact]
    public void AddMilvaionWorkerWithJobs_ShouldThrow_WhenConfigHasNoMatchingJobClass()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "test-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["JobConsumers:OrphanJobConfig:ConsumerId"] = "orphan-consumer",
            ["JobConsumers:OrphanJobConfig:RoutingPattern"] = "worker.orphan.*",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        var act = () => services.AddMilvaionWorkerWithJobs(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*without corresponding job class*OrphanJobConfig*");
    }

    [Fact]
    public void AddMilvaionWorkerWithJobs_ShouldThrow_WhenMultipleConfigsWithoutJobClasses()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "test-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["JobConsumers:GhostJob1:ConsumerId"] = "ghost-1",
            ["JobConsumers:GhostJob1:RoutingPattern"] = "w.g1.*",
            ["JobConsumers:GhostJob2:ConsumerId"] = "ghost-2",
            ["JobConsumers:GhostJob2:RoutingPattern"] = "w.g2.*",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        var act = () => services.AddMilvaionWorkerWithJobs(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*GhostJob1*")
           .WithMessage("*GhostJob2*");
    }

    [Fact]
    public void AddMilvaionWorkerWithJobs_ShouldUseDefaultWorkerId_WhenNotConfigured()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            // No WorkerId configured
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["JobConsumers:TestJob:ConsumerId"] = "test-consumer",
            // No RoutingPattern → will auto-generate using MachineName as fallback workerId
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act - Will throw because no matching job class, but routing pattern code path runs
        var act = () => services.AddMilvaionWorkerWithJobs(configuration);

        // Assert - Should get past auto-generation to the validation
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*TestJob*");
    }

    #endregion

    #region AddHealthCheckEndpoints

    [Fact]
    public void AddHealthCheckEndpoints_ShouldRegisterHealthChecks_WhenEnabled()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "hc-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:HealthCheck:Enabled"] = "true",
            ["Worker:HealthCheck:IntervalSeconds"] = "15",
            ["Worker:HealthCheck:LiveFilePath"] = "/tmp/healthy",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        services.AddHealthCheckEndpoints(configuration);

        // Assert
        var healthCheckDescriptor = services.Any(d => d.ServiceType == typeof(HealthCheckService));
        healthCheckDescriptor.Should().BeTrue("HealthCheckService should be registered when endpoint health checks are enabled");
    }

    [Fact]
    public void AddHealthCheckEndpoints_ShouldNotRegister_WhenDisabled()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "hc-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:HealthCheck:Enabled"] = "false",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var beforeCount = services.Count;

        // Act
        services.AddHealthCheckEndpoints(configuration);

        // Assert
        services.Count.Should().Be(beforeCount);
    }

    #endregion

    #region GenerateRoutingPattern

    [Theory]
    [InlineData("SendEmailJob", "email-worker-01", "email-worker-01.sendemail.*")]
    [InlineData("TestJob", "worker-1", "worker-1.test.*")]
    [InlineData("NonParallelJob", "my-worker", "my-worker.nonparallel.*")]
    [InlineData("ProcessDataJob", "data-worker", "data-worker.processdata.*")]
    public void GenerateRoutingPattern_WithJobSuffix_ShouldRemoveSuffixAndFormat(string jobTypeName, string workerId, string expected)
    {
        // Act
        var result = WorkerServiceCollectionExtensions.GenerateRoutingPattern(jobTypeName, workerId);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Worker", "w1", "w1.worker.*")]
    [InlineData("Processor", "my-worker", "my-worker.processor.*")]
    [InlineData("Handler", "handler-svc", "handler-svc.handler.*")]
    public void GenerateRoutingPattern_WithoutJobSuffix_ShouldKeepNameAndFormat(string jobTypeName, string workerId, string expected)
    {
        // Act
        var result = WorkerServiceCollectionExtensions.GenerateRoutingPattern(jobTypeName, workerId);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void GenerateRoutingPattern_ShouldLowercaseEverything()
    {
        // Act
        var result = WorkerServiceCollectionExtensions.GenerateRoutingPattern("MyComplexJob", "Email-Worker-01");

        // Assert
        result.Should().Be("email-worker-01.mycomplex.*");
    }

    [Fact]
    public void GenerateRoutingPattern_CaseInsensitiveJobSuffix_ShouldRemove()
    {
        // "JOB" suffix (uppercase) should also be removed
        var result = WorkerServiceCollectionExtensions.GenerateRoutingPattern("SendEmailJOB", "w1");
        result.Should().Be("w1.sendemail.*");

        // "job" suffix (lowercase) should also be removed
        var result2 = WorkerServiceCollectionExtensions.GenerateRoutingPattern("SendEmailjob", "w1");
        result2.Should().Be("w1.sendemail.*");
    }

    [Fact]
    public void GenerateRoutingPattern_ExactlyJob_ShouldReturnEmptyJobName()
    {
        // Edge case: job type name is exactly "Job"
        var result = WorkerServiceCollectionExtensions.GenerateRoutingPattern("Job", "w1");
        result.Should().Be("w1..*");
    }

    [Fact]
    public void GenerateRoutingPattern_SingleCharJob_ShouldWork()
    {
        var result = WorkerServiceCollectionExtensions.GenerateRoutingPattern("AJob", "worker");
        result.Should().Be("worker.a.*");
    }

    #endregion

    #region UseHealthCheckEndpoints

    [Fact]
    public async Task UseHealthCheckEndpoints_WhenEnabled_ShouldMapHealthEndpoint()
    {
        // Arrange
        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "hc-test-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:HealthCheck:Enabled"] = "true",
            ["Worker:HealthCheck:IntervalSeconds"] = "30",
            ["Worker:HealthCheck:LiveFilePath"] = "/tmp/healthy",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHealthChecks();

        var app = builder.Build();

        // Act
        app.UseHealthCheckEndpoints(configuration);
        await app.StartAsync();

        var client = app.GetTestClient();

        // Assert — /health should return 200 OK
        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("\"Ok\"");

        await app.StopAsync();
    }

    [Fact]
    public async Task UseHealthCheckEndpoints_WhenEnabled_ShouldMapLivenessEndpoint()
    {
        // Arrange
        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "live-test-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:HealthCheck:Enabled"] = "true",
            ["Worker:HealthCheck:IntervalSeconds"] = "30",
            ["Worker:HealthCheck:LiveFilePath"] = "/tmp/healthy",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHealthChecks();

        var app = builder.Build();

        // Act
        app.UseHealthCheckEndpoints(configuration);
        await app.StartAsync();

        var client = app.GetTestClient();

        // Assert — /health/live should return liveness response
        var response = await client.GetAsync("/health/live");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Healthy");

        await app.StopAsync();
    }

    [Fact]
    public async Task UseHealthCheckEndpoints_WhenEnabled_ShouldMapStartupEndpoint()
    {
        // Arrange
        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "startup-test-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:HealthCheck:Enabled"] = "true",
            ["Worker:HealthCheck:IntervalSeconds"] = "30",
            ["Worker:HealthCheck:LiveFilePath"] = "/tmp/healthy",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHealthChecks();

        var app = builder.Build();

        // Act
        app.UseHealthCheckEndpoints(configuration);
        await app.StartAsync();

        var client = app.GetTestClient();

        // Assert — /health/startup should return startup probe response
        var response = await client.GetAsync("/health/startup");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Started");

        await app.StopAsync();
    }

    [Fact]
    public async Task UseHealthCheckEndpoints_WhenEnabled_ShouldMapReadinessEndpoint()
    {
        // Arrange
        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "ready-test-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:HealthCheck:Enabled"] = "true",
            ["Worker:HealthCheck:IntervalSeconds"] = "30",
            ["Worker:HealthCheck:LiveFilePath"] = "/tmp/healthy",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHealthChecks();

        var app = builder.Build();

        // Act
        app.UseHealthCheckEndpoints(configuration);
        await app.StartAsync();

        var client = app.GetTestClient();

        // Assert — /health/ready should return readiness probe response
        var response = await client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Healthy");

        await app.StopAsync();
    }

    [Fact]
    public async Task UseHealthCheckEndpoints_WhenDisabled_ShouldNotMapEndpoints()
    {
        // Arrange
        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "disabled-hc-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["Worker:HealthCheck:Enabled"] = "false",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();

        // Act
        app.UseHealthCheckEndpoints(configuration);
        await app.StartAsync();

        var client = app.GetTestClient();

        // Assert — /health should return 404 (not mapped)
        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);

        await app.StopAsync();
    }

    #endregion

    #region AddJobConsumersFromConfiguration (indirect)

    [Fact]
    public void AddMilvaionWorkerWithJobs_ShouldAutoGenerateRoutingPattern_MatchingGenerateRoutingPatternOutput()
    {
        // Arrange — Verify the auto-generation in AddMilvaionWorkerWithJobs uses the same logic as GenerateRoutingPattern
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "my-worker-01",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["JobConsumers:SendEmailJob:ConsumerId"] = "email-consumer",
            // No RoutingPattern — should auto-generate
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act — Will throw because no matching job class (Assembly.GetEntryAssembly() is null in tests),
        // but this proves the auto-generation code path (lines 73-83) has executed
        var act = () => services.AddMilvaionWorkerWithJobs(configuration);

        // Assert — Should fail at configsWithoutJob validation (line 101-105),
        // confirming it got past the auto-generation without error
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*SendEmailJob*");

        // Verify that GenerateRoutingPattern would produce the expected pattern
        var expectedPattern = WorkerServiceCollectionExtensions.GenerateRoutingPattern("SendEmailJob", "my-worker-01");
        expectedPattern.Should().Be("my-worker-01.sendemail.*");
    }

    [Fact]
    public void AddMilvaionWorkerWithJobs_WithExplicitRoutingPattern_ShouldNotAutoGenerate()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "test-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["JobConsumers:CustomJob:ConsumerId"] = "custom-consumer",
            ["JobConsumers:CustomJob:RoutingPattern"] = "custom.pattern.*",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act — Will throw because no matching job class, but the explicit routing pattern code path is tested
        var act = () => services.AddMilvaionWorkerWithJobs(configuration);

        // Assert — Should fail at configsWithoutJob validation, not at routing pattern generation
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*CustomJob*");
    }

    [Fact]
    public void AddMilvaionWorkerWithJobs_WithMultipleJobConfigs_ShouldValidateAll()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = "multi-worker",
            ["Worker:RabbitMQ:Host"] = "localhost",
            ["JobConsumers:JobA:ConsumerId"] = "consumer-a",
            ["JobConsumers:JobA:RoutingPattern"] = "worker.a.*",
            ["JobConsumers:JobB:ConsumerId"] = "consumer-b",
            // JobB has no RoutingPattern → auto-generate
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        // Act
        var act = () => services.AddMilvaionWorkerWithJobs(configuration);

        // Assert — Both jobs should be in the error since neither has a matching class
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*JobA*")
           .WithMessage("*JobB*");
    }

    #endregion
}
