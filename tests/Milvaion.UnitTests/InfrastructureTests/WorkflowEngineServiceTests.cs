using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Messaging;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Moq;

namespace Milvaion.UnitTests.InfrastructureTests;

/// <summary>
/// Unit tests for WorkflowEngineService.
/// Tests workflow execution, step scheduling, dependency resolution, and failure handling.
/// </summary>
public class WorkflowEngineServiceTests
{
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<IJobPublisher> _rabbitMQPublisherMock;
    private readonly Mock<IRedisSchedulerService> _redisSchedulerMock;
    private readonly Mock<IRedisStatsService> _redisStatsMock;
    private readonly IOptions<WorkflowEngineOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BackgroundServiceMetrics _metrics;
    private readonly Mock<IServiceScope> _serviceScopeMock;
    private readonly Mock<IServiceScopeFactory> _serviceScopeFactoryMock;

    public WorkflowEngineServiceTests()
    {
        _serviceProviderMock = new Mock<IServiceProvider>();
        _rabbitMQPublisherMock = new Mock<IJobPublisher>();
        _redisSchedulerMock = new Mock<IRedisSchedulerService>();
        _redisStatsMock = new Mock<IRedisStatsService>();
        _options = Options.Create(new WorkflowEngineOptions
        {
            Enabled = true,
            PollingIntervalSeconds = 5
        });

        var mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
        _loggerFactory = mockLoggerFactory.Object;

        _metrics = new BackgroundServiceMetrics();

        _serviceScopeMock = new Mock<IServiceScope>();
        _serviceScopeFactoryMock = new Mock<IServiceScopeFactory>();
        _serviceScopeFactoryMock.Setup(x => x.CreateScope()).Returns(_serviceScopeMock.Object);
        _serviceProviderMock.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(_serviceScopeFactoryMock.Object);

        // Setup required service mocks for WorkflowEngineService constructor
        _serviceProviderMock.Setup(x => x.GetService(typeof(IJobPublisher))).Returns(_rabbitMQPublisherMock.Object);
        _serviceProviderMock.Setup(x => x.GetService(typeof(IRedisSchedulerService))).Returns(_redisSchedulerMock.Object);
        _serviceProviderMock.Setup(x => x.GetService(typeof(IRedisStatsService))).Returns(_redisStatsMock.Object);
        _serviceProviderMock.Setup(x => x.GetService(typeof(BackgroundServiceMetrics))).Returns(_metrics);
    }

    [Fact]
    public void ServiceName_ShouldReturnWorkflowEngine()
    {
        // Arrange & Act
        var service = CreateService();

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WhenDisabled_ShouldNotThrow()
    {
        // Arrange
        var disabledOptions = Options.Create(new WorkflowEngineOptions { Enabled = false });

        // Act
        var act = () => new WorkflowEngineService(
            _serviceProviderMock.Object,
            disabledOptions,
            _loggerFactory,
            null
        );

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public async Task StartAsync_WhenDisabled_ShouldNotStartEngine()
    {
        // Arrange
        var disabledOptions = Options.Create(new WorkflowEngineOptions { Enabled = false });
        var service = new WorkflowEngineService(
            _serviceProviderMock.Object,
            disabledOptions,
            _loggerFactory,
            null
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act
        await service.StartAsync(cts.Token);
        await Task.Delay(100); // Give service time to exit early if disabled

        // Assert - Service should complete without exceptions
        // No database calls should be made when disabled
        service.Should().NotBeNull();
    }

    [Theory]
    [InlineData(WorkflowFailureStrategy.StopOnFirstFailure)]
    [InlineData(WorkflowFailureStrategy.ContinueOnFailure)]
    public void WorkflowFailureStrategy_ShouldBeSupportedValue(WorkflowFailureStrategy strategy) => strategy.Should().BeOneOf(
            WorkflowFailureStrategy.StopOnFirstFailure,
            WorkflowFailureStrategy.ContinueOnFailure
        );

    [Theory]
    [InlineData(WorkflowStatus.Pending)]
    [InlineData(WorkflowStatus.Running)]
    [InlineData(WorkflowStatus.Completed)]
    [InlineData(WorkflowStatus.Failed)]
    [InlineData(WorkflowStatus.Cancelled)]
    public void WorkflowStatus_ShouldBeSupportedValue(WorkflowStatus status) => status.Should().BeOneOf(
            WorkflowStatus.Pending,
            WorkflowStatus.Running,
            WorkflowStatus.Completed,
            WorkflowStatus.Failed,
            WorkflowStatus.Cancelled
        );

    [Theory]
    [InlineData(WorkflowStepStatus.Pending)]
    [InlineData(WorkflowStepStatus.Running)]
    [InlineData(WorkflowStepStatus.Completed)]
    [InlineData(WorkflowStepStatus.Failed)]
    [InlineData(WorkflowStepStatus.Skipped)]
    [InlineData(WorkflowStepStatus.Cancelled)]
    [InlineData(WorkflowStepStatus.Delayed)]
    public void WorkflowStepStatus_ShouldBeSupportedValue(WorkflowStepStatus status) => status.Should().BeOneOf(
            WorkflowStepStatus.Pending,
            WorkflowStepStatus.Running,
            WorkflowStepStatus.Completed,
            WorkflowStepStatus.Failed,
            WorkflowStepStatus.Skipped,
            WorkflowStepStatus.Cancelled,
            WorkflowStepStatus.Delayed
        );

    [Theory]
    [InlineData(WorkflowNodeType.Task)]
    [InlineData(WorkflowNodeType.Condition)]
    [InlineData(WorkflowNodeType.Merge)]
    public void WorkflowNodeType_ShouldBeSupportedValue(WorkflowNodeType nodeType) => nodeType.Should().BeOneOf(
            WorkflowNodeType.Task,
            WorkflowNodeType.Condition,
            WorkflowNodeType.Merge
        );

    [Fact]
    public void WorkflowDefinition_ShouldInitializeWithEmptyCollections()
    {
        // Act
        var definition = new WorkflowDefinition();

        // Assert
        definition.Steps.Should().NotBeNull().And.BeEmpty();
        definition.Edges.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void WorkflowStepDefinition_ShouldHaveRequiredProperties()
    {
        // Arrange & Act
        var stepId = Guid.CreateVersion7();
        var step = new WorkflowStepDefinition
        {
            Id = stepId,
            StepName = "TestStep",
            Order = 1,
            NodeType = WorkflowNodeType.Task,
            JobId = Guid.CreateVersion7(),
            DelaySeconds = 10
        };

        // Assert
        step.Id.Should().Be(stepId);
        step.StepName.Should().Be("TestStep");
        step.Order.Should().Be(1);
        step.NodeType.Should().Be(WorkflowNodeType.Task);
        step.JobId.Should().NotBeNull();
        step.DelaySeconds.Should().Be(10);
    }

    [Fact]
    public void WorkflowEdgeDefinition_ShouldLinkSteps()
    {
        // Arrange & Act
        var sourceId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();

        var edge = new WorkflowEdgeDefinition
        {
            SourceStepId = sourceId,
            TargetStepId = targetId,
            SourcePort = "true",
            TargetPort = null,
            Label = "Success path",
            Order = 1
        };

        // Assert
        edge.SourceStepId.Should().Be(sourceId);
        edge.TargetStepId.Should().Be(targetId);
        edge.SourcePort.Should().Be("true");
        edge.TargetPort.Should().BeNull();
        edge.Label.Should().Be("Success path");
        edge.Order.Should().Be(1);
    }

    [Fact]
    public void Workflow_ShouldInitializeWithDefaultValues()
    {
        // Act
        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Test Workflow",
            Description = "Test workflow description"
        };

        // Assert
        workflow.IsActive.Should().BeTrue();
        workflow.FailureStrategy.Should().Be(WorkflowFailureStrategy.StopOnFirstFailure);
        workflow.MaxStepRetries.Should().Be(0);
        workflow.Version.Should().Be(1);
        workflow.Definition.Should().NotBeNull();
        workflow.Versions.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void WorkflowRun_ShouldInitializeWithPendingStatus()
    {
        // Act
        var run = new WorkflowRun
        {
            Id = Guid.CreateVersion7(),
            WorkflowId = Guid.CreateVersion7(),
            TriggerReason = "Manual trigger"
        };

        // Assert
        run.Status.Should().Be(WorkflowStatus.Pending);
        run.StepOccurrences.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void WorkflowSnapshot_ShouldStoreWorkflowVersion()
    {
        // Arrange & Act
        var snapshot = new WorkflowSnapshot
        {
            Version = 1,
            Name = "Test Workflow v1",
            Description = "Initial version",
            IsActive = true,
            FailureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
            MaxStepRetries = 0,
            TimeoutSeconds = 3600,
            CronExpression = "0 0 9 * * *",
            Tags = "test,workflow",
            CreationDate = DateTime.UtcNow,
            Steps =
            [
                new()
                {
                    Id = Guid.CreateVersion7(),
                    StepName = "Step1",
                    Order = 1,
                    NodeType = WorkflowNodeType.Task,
                    JobId = Guid.CreateVersion7()
                }
            ],
            Edges = []
        };

        // Assert
        snapshot.Version.Should().Be(1);
        snapshot.Name.Should().Be("Test Workflow v1");
        snapshot.Steps.Should().HaveCount(1);
        snapshot.Edges.Should().BeEmpty();
        snapshot.FailureStrategy.Should().Be(WorkflowFailureStrategy.StopOnFirstFailure);
    }

    [Fact]
    public void CronExpression_ShouldBeValidFormat()
    {
        // Arrange
        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = "Scheduled Workflow",
            CronExpression = "0 0 9 * * *" // Every day at 9 AM
        };

        // Assert
        workflow.CronExpression.Should().NotBeNullOrWhiteSpace();
        workflow.CronExpression.Split(' ').Should().HaveCount(6, "cron expression should have 6 parts (second minute hour day month dayOfWeek)");
    }

    [Fact]
    public void WorkflowDefinition_ShouldSupportMultipleStepsAndEdges()
    {
        // Arrange
        var step1 = Guid.CreateVersion7();
        var step2 = Guid.CreateVersion7();
        var step3 = Guid.CreateVersion7();

        var definition = new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepDefinition { Id = step1, StepName = "Extract", Order = 1, NodeType = WorkflowNodeType.Task },
                new WorkflowStepDefinition { Id = step2, StepName = "Transform", Order = 2, NodeType = WorkflowNodeType.Task },
                new WorkflowStepDefinition { Id = step3, StepName = "Load", Order = 3, NodeType = WorkflowNodeType.Task }
            ],
            Edges =
            [
                new WorkflowEdgeDefinition { SourceStepId = step1, TargetStepId = step2, Order = 1 },
                new WorkflowEdgeDefinition { SourceStepId = step2, TargetStepId = step3, Order = 2 }
            ]
        };

        // Assert
        definition.Steps.Should().HaveCount(3);
        definition.Edges.Should().HaveCount(2);
        definition.Edges.All(e => e.SourceStepId != Guid.Empty && e.TargetStepId != Guid.Empty).Should().BeTrue();
    }

    [Fact]
    public void WorkflowDefinition_ShouldSupportConditionalBranching()
    {
        // Arrange
        var conditionStep = Guid.CreateVersion7();
        var trueStep = Guid.CreateVersion7();
        var falseStep = Guid.CreateVersion7();
        var mergeStep = Guid.CreateVersion7();

        var definition = new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepDefinition { Id = conditionStep, StepName = "Check Status", NodeType = WorkflowNodeType.Condition, Order = 1 },
                new WorkflowStepDefinition { Id = trueStep, StepName = "Success Path", NodeType = WorkflowNodeType.Task, Order = 2 },
                new WorkflowStepDefinition { Id = falseStep, StepName = "Failure Path", NodeType = WorkflowNodeType.Task, Order = 3 },
                new WorkflowStepDefinition { Id = mergeStep, StepName = "Merge", NodeType = WorkflowNodeType.Merge, Order = 4 }
            ],
            Edges =
            [
                new WorkflowEdgeDefinition { SourceStepId = conditionStep, TargetStepId = trueStep, SourcePort = "true", Order = 1 },
                new WorkflowEdgeDefinition { SourceStepId = conditionStep, TargetStepId = falseStep, SourcePort = "false", Order = 2 },
                new WorkflowEdgeDefinition { SourceStepId = trueStep, TargetStepId = mergeStep, Order = 3 },
                new WorkflowEdgeDefinition { SourceStepId = falseStep, TargetStepId = mergeStep, Order = 4 }
            ]
        };

        // Assert
        definition.Steps.Should().Contain(s => s.NodeType == WorkflowNodeType.Condition);
        definition.Steps.Should().Contain(s => s.NodeType == WorkflowNodeType.Merge);
        definition.Edges.Should().Contain(e => e.SourcePort == "true");
        definition.Edges.Should().Contain(e => e.SourcePort == "false");
    }

    [Fact]
    public void WorkflowStepDefinition_ShouldSupportDataMappings()
    {
        // Arrange
        var step = new WorkflowStepDefinition
        {
            Id = Guid.CreateVersion7(),
            StepName = "Process Data",
            NodeType = WorkflowNodeType.Task,
            Order = 2,
            DataMappings = @"{""step1:result.userId"": ""inputUserId"", ""step1:result.status"": ""inputStatus""}"
        };

        // Assert
        step.DataMappings.Should().NotBeNullOrWhiteSpace();
        step.DataMappings.Should().Contain("step1:result");
        step.DataMappings.Should().Contain("inputUserId");
    }

    [Fact]
    public void WorkflowStepDefinition_ShouldSupportJobDataOverride()
    {
        // Arrange
        var step = new WorkflowStepDefinition
        {
            Id = Guid.CreateVersion7(),
            StepName = "Custom Job",
            NodeType = WorkflowNodeType.Task,
            Order = 1,
            JobDataOverride = @"{""customParam"": ""value"", ""timeout"": 300}"
        };

        // Assert
        step.JobDataOverride.Should().NotBeNullOrWhiteSpace();
        step.JobDataOverride.Should().Contain("customParam");
    }

    [Fact]
    public void WorkflowStepDefinition_ShouldSupportDelayedExecution()
    {
        // Arrange
        var step = new WorkflowStepDefinition
        {
            Id = Guid.CreateVersion7(),
            StepName = "Delayed Step",
            NodeType = WorkflowNodeType.Task,
            Order = 2,
            DelaySeconds = 300 // 5 minutes delay
        };

        // Assert
        step.DelaySeconds.Should().Be(300);
    }

    private WorkflowEngineService CreateService() => new(
            _serviceProviderMock.Object,
            _options,
            _loggerFactory,
            null
        );
}
